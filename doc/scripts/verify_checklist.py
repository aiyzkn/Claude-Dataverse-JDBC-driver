#!/usr/bin/env python3
"""Dataverse GW 動作確認チェックリスト（WBS 3.2.1 §10）を一括実行する。

前提:
  - ゲートウェイが ClientSecret で起動済み（既定 grpc://localhost:32010）。
  - `pip install adbc-driver-flightsql pyarrow`（xlsx 検証をするなら `openpyxl` も）。

使い方（macOS 例）:
  # 別ターミナルでゲートウェイ起動（ClientSecret）
  export GW=grpc://localhost:32010
  export GW_EXE=~/DataverseGW/DataverseGW   # dvload/本番ガード検証に使用（任意）
  python3 doc/scripts/verify_checklist.py

環境変数:
  GW        接続先(既定 grpc://localhost:32010)
  GW_EXE    ゲートウェイ実行ファイルの絶対パス（dvload=#6/#7, 本番ガード=#9 を検証。未設定なら SKIP）
  RUN_TRUNCATE=1  #4 で TRUNCATE も実行（既定は条件付き DELETE のみ。合成表を全削除する点に注意）

終了コード: FAIL が 1 件でもあれば 1、それ以外 0。SKIP は失敗扱いしない。

所要時間: ADBC 駆動のみなら数十秒。GW_EXE 設定時は dvload(#6/#7)と本番ガード(#9,別ポートで
ゲートウェイ起動)で数分かかる。進捗をリアルタイムに見るには `python3 -u` で実行するとよい。
"""
import os
import sys
import time
import uuid
import subprocess
import platform

try:
    import adbc_driver_flightsql.dbapi as fs
except Exception as e:  # noqa
    print("ERROR: adbc-driver-flightsql が必要です → pip install adbc-driver-flightsql pyarrow")
    raise

# macOS/Linux でロケールが非 UTF-8 でも標準出力が落ちないようにする
for _s in (sys.stdout, sys.stderr):
    try:
        _s.reconfigure(encoding="utf-8", errors="replace")  # Python 3.7+
    except Exception:
        pass


def emsg(e):
    """例外メッセージを安全に整形（ADBC 例外が不正 UTF-8 を含んでも UnicodeDecodeError で落ちない）。"""
    try:
        return str(e)[:200]
    except Exception:
        try:
            return repr(getattr(e, "args", e))[:200]
        except Exception:
            return "<メッセージ復号不可>"


GW = os.environ.get("GW", "grpc://localhost:32010")
GW_EXE = os.environ.get("GW_EXE")
RUN_TRUNCATE = os.environ.get("RUN_TRUNCATE") == "1"
CONTACT_LOOKUP_TARGET = "account"  # 名前→ID 解決(#10)で使う polymorphic lookup の対象

results = []  # (no, name, status, detail)


def record(no, name, status, detail=""):
    results.append((no, name, status, detail))
    mark = {"PASS": "PASS", "FAIL": "FAIL", "SKIP": "SKIP"}[status]
    print(f"  [{mark}] #{no} {name}  {detail}")


def tag(prefix):
    return f"{prefix}-{uuid.uuid4().hex[:8]}"


def audit_dirs():
    """監査ディレクトリの候補（OS/ロケール/環境で LocalApplicationData の実体が異なるため複数探索）。"""
    cands = []
    if platform.system() == "Windows":
        cands.append(os.path.join(os.environ.get("LOCALAPPDATA", ""), "DataverseGW", "audit"))
    else:
        xdg = os.environ.get("XDG_DATA_HOME")
        if xdg:
            cands.append(os.path.join(xdg, "DataverseGW", "audit"))
        cands.append(os.path.expanduser("~/.local/share/DataverseGW/audit"))
        cands.append(os.path.expanduser("~/Library/Application Support/DataverseGW/audit"))
    return cands


def one(cur, sql):
    cur.execute(sql)
    return cur.fetchall()


def run_exe(args, extra_env=None, wait_listen_port=None, timeout=90):
    """GW_EXE をサブプロセス起動。wait_listen_port 指定時は listening まで待って Popen を返す。"""
    env = dict(os.environ)
    env["DOTNET_ROLL_FORWARD"] = "LatestMajor"
    if extra_env:
        env.update(extra_env)
    if wait_listen_port is None:
        return subprocess.run([GW_EXE] + args, env=env, capture_output=True,
                              text=True, encoding="utf-8", errors="replace", timeout=timeout)
    proc = subprocess.Popen([GW_EXE] + args, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                            text=True, encoding="utf-8", errors="replace")
    start = time.time()
    while time.time() - start < timeout:
        line = proc.stdout.readline()
        if not line and proc.poll() is not None:
            break
        if "listening" in line.lower():
            return proc
    return proc


# ---- 各チェック --------------------------------------------------------------

def check_select(conn):
    try:
        cur = conn.cursor(); rows = one(cur, "SELECT TOP 5 accountid, name FROM account"); cur.close()
        record(1, "SELECT(読み取り)", "PASS", f"{len(rows)}行")
    except Exception as e:
        record(1, "SELECT(読み取り)", "FAIL", emsg(e))


def check_insert(conn):
    t = tag("CHK-INS")
    try:
        cur = conn.cursor()
        cur.executemany("INSERT INTO poc_gadget (poc_name, poc_quantity) VALUES (?, ?)", [(t, i) for i in range(20)])
        cur.close()
        cur = conn.cursor(); c = one(cur, f"SELECT COUNT(*) AS n FROM poc_gadget WHERE poc_name='{t}'")[0][0]; cur.close()
        record(2, "INSERT(バルク)", "PASS" if c == 20 else "FAIL", f"作成{c}/20")
        return t
    except Exception as e:
        record(2, "INSERT(バルク)", "FAIL", emsg(e)); return None


def check_upsert(conn):
    gid = str(uuid.uuid4()); t = tag("CHK-UPS")
    try:
        for _ in range(2):  # 同一GUIDで2回 → 冪等
            cur = conn.cursor()
            cur.executemany("UPSERT INTO poc_gadget (poc_gadgetid, poc_name) VALUES (?, ?)", [(gid, t)])
            cur.close()
        cur = conn.cursor(); c = one(cur, f"SELECT COUNT(*) AS n FROM poc_gadget WHERE poc_name='{t}'")[0][0]; cur.close()
        record(3, "UPSERT(冪等)", "PASS" if c == 1 else "FAIL", f"件数{c}(=1期待)")
    except Exception as e:
        record(3, "UPSERT(冪等)", "FAIL", emsg(e))


def check_delete(conn):
    t = tag("CHK-DEL")
    try:
        cur = conn.cursor()
        cur.executemany("INSERT INTO poc_gadget (poc_name, poc_quantity) VALUES (?, ?)", [(t, i) for i in range(10)])
        cur.close()
        cur = conn.cursor(); cur.execute(f"DELETE FROM poc_gadget WHERE poc_name='{t}'")
        try: cur.fetchall()
        except Exception: pass
        cur.close()
        cur = conn.cursor(); c = one(cur, f"SELECT COUNT(*) AS n FROM poc_gadget WHERE poc_name='{t}'")[0][0]; cur.close()
        record(4, "条件付きDELETE", "PASS" if c == 0 else "FAIL", f"残{c}(=0期待)")
    except Exception as e:
        record(4, "条件付きDELETE", "FAIL", emsg(e))
    if RUN_TRUNCATE:
        try:
            cur = conn.cursor(); cur.execute("TRUNCATE TABLE poc_gadget")
            try: cur.fetchall()
            except Exception: pass
            cur.close()
            record(4, "TRUNCATE(発行)", "PASS", "BulkDelete発行")
        except Exception as e:
            record(4, "TRUNCATE(発行)", "FAIL", emsg(e))


def check_nn(conn):
    gid = str(uuid.uuid4()); ext = tag("CHK-NN-W")
    try:
        cur = conn.cursor(); cur.executemany("UPSERT INTO poc_gadget (poc_gadgetid, poc_name) VALUES (?, ?)", [(gid, "chk-nn-g")]); cur.close()
        cur = conn.cursor(); cur.executemany("INSERT INTO poc_widget (poc_externalid, poc_name) VALUES (?, ?)", [(ext, "chk-nn-w")]); cur.close()
        cur = conn.cursor(); wid = str(one(cur, f"SELECT poc_widgetid FROM poc_widget WHERE poc_externalid='{ext}'")[0][0]); cur.close()
        cur = conn.cursor(); before = one(cur, "SELECT COUNT(*) AS n FROM poc_poc_gadget_poc_widget")[0][0]; cur.close()
        cur = conn.cursor(); cur.executemany("INSERT INTO poc_gadget_poc_widget (record1id, record2id) VALUES (?, ?)", [(gid, wid)]); cur.close()
        cur = conn.cursor(); after = one(cur, "SELECT COUNT(*) AS n FROM poc_poc_gadget_poc_widget")[0][0]; cur.close()
        record(5, "N:N 関連付け", "PASS" if after == before + 1 else "FAIL", f"交差 {before}->{after}")
    except Exception as e:
        record(5, "N:N 関連付け", "FAIL", emsg(e))


def _proc_tail(r):
    """サブプロセス出力の末尾（失敗理由の可視化用）。"""
    out = ((getattr(r, "stdout", "") or "") + (getattr(r, "stderr", "") or "")).strip().replace("\n", " | ")
    return out[-240:] if out else "(出力なし)"


# GW_EXE サブプロセスに渡す認証環境。シェルに残った Interactive/DeviceCode を継承しないよう ClientSecret を明示。
_SUBPROC_ENV = {"DATAVERSE_AUTH": "ClientSecret"}


def check_dvload(conn):
    if not GW_EXE:
        record(6, "dvload CSV", "SKIP", "GW_EXE 未設定"); record(7, "dvload xlsx", "SKIP", "GW_EXE 未設定"); return
    import csv, tempfile
    # CSV
    t = tag("CHK-DVL-CSV"); csv_path = os.path.join(tempfile.gettempdir(), t + ".csv")
    with open(csv_path, "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f); w.writerow(["poc_name", "poc_quantity"])
        for i in range(15): w.writerow([t, i + 1])
    try:
        r = run_exe(["dvload", "--file", csv_path, "--table", "poc_gadget"], extra_env=_SUBPROC_ENV, timeout=120)
        cur = conn.cursor(); c = one(cur, f"SELECT COUNT(*) AS n FROM poc_gadget WHERE poc_name='{t}'")[0][0]; cur.close()
        detail = f"作成{c}/15 rc={r.returncode}"
        if c != 15:
            detail += " out=" + _proc_tail(r)
        record(6, "dvload CSV", "PASS" if c == 15 else "FAIL", detail)
    except Exception as e:
        record(6, "dvload CSV", "FAIL", emsg(e))
    # xlsx
    try:
        import openpyxl
        t2 = tag("CHK-DVL-XLS"); xlsx_path = os.path.join(tempfile.gettempdir(), t2 + ".xlsx")
        wb = openpyxl.Workbook(); ws = wb.active; ws.append(["poc_name", "poc_quantity"])
        for i in range(10): ws.append([t2, i + 1])
        wb.save(xlsx_path)
        r = run_exe(["dvload", "--file", xlsx_path, "--table", "poc_gadget"], extra_env=_SUBPROC_ENV, timeout=120)
        cur = conn.cursor(); c = one(cur, f"SELECT COUNT(*) AS n FROM poc_gadget WHERE poc_name='{t2}'")[0][0]; cur.close()
        detail = f"作成{c}/10 rc={r.returncode}"
        if c != 10:
            detail += " out=" + _proc_tail(r)
        record(7, "dvload xlsx", "PASS" if c == 10 else "FAIL", detail)
    except ImportError:
        record(7, "dvload xlsx", "SKIP", "openpyxl 未導入")
    except Exception as e:
        record(7, "dvload xlsx", "FAIL", emsg(e))


def check_dryrun(conn):
    t = tag("CHK-DRY")
    try:
        cur = conn.cursor(); cur.execute("SET DRY_RUN = ON"); cur.close()
        cur = conn.cursor(); cur.executemany("INSERT INTO poc_gadget (poc_name, poc_quantity) VALUES (?, ?)", [(t, i) for i in range(30)]); cur.close()
        cur = conn.cursor(); cur.execute("SET DRY_RUN = OFF"); cur.close()
        cur = conn.cursor(); c = one(cur, f"SELECT COUNT(*) AS n FROM poc_gadget WHERE poc_name='{t}'")[0][0]; cur.close()
        record(8, "ドライラン(未実行)", "PASS" if c == 0 else "FAIL", f"作成{c}(=0期待)")
    except Exception as e:
        record(8, "ドライラン(未実行)", "FAIL", emsg(e))


def check_production_guard():
    if not GW_EXE:
        record(9, "本番環境ガード", "SKIP", "GW_EXE 未設定"); return
    proc = None
    try:
        proc = run_exe([], extra_env={"Gateway__IsProduction": "true", "Gateway__Port": "32011",
                                      "DATAVERSE_AUTH": "ClientSecret"}, wait_listen_port=32011, timeout=120)
        # 起動直後の猶予を見て接続をリトライ(macOS は auth に時間がかかる)
        conn = None; lasterr = ""
        for _ in range(12):
            if proc.poll() is not None:  # サブプロセスが早期終了
                break
            try:
                conn = fs.connect("grpc://localhost:32011")
                c0 = conn.cursor(); c0.execute("SELECT 1"); c0.close()
                break
            except Exception as e:
                lasterr = emsg(e); conn = None; time.sleep(3)
        if conn is None:
            # サブプロセス出力を回収して原因を表示
            out = "(取得不可)"
            try:
                proc.terminate(); o, _ = proc.communicate(timeout=10); out = _proc_tail(type("R", (), {"stdout": o, "stderr": ""}))
            except Exception:
                pass
            record(9, "本番環境ガード", "FAIL", f"32011 接続不可: {lasterr} / gw_out={out}")
            return
        t = tag("CHK-PRODG")
        blocked = False
        try:
            cur = conn.cursor(); cur.executemany("INSERT INTO poc_gadget (poc_name, poc_quantity) VALUES (?, ?)", [(t, 1)]); cur.close()
        except Exception as e:
            blocked = "本番環境ガード" in str(e)
        cur = conn.cursor(); cur.execute("SET ALLOW_PRODUCTION_WRITES = ON"); cur.close()
        cur = conn.cursor(); cur.executemany("INSERT INTO poc_gadget (poc_name, poc_quantity) VALUES (?, ?)", [(t, 2)]); cur.close()
        cur = conn.cursor(); c = one(cur, f"SELECT COUNT(*) AS n FROM poc_gadget WHERE poc_name='{t}'")[0][0]; cur.close()
        conn.close()
        record(9, "本番環境ガード", "PASS" if (blocked and c == 1) else "FAIL", f"ブロック={blocked}, 解除後={c}")
    except Exception as e:
        record(9, "本番環境ガード", "FAIL", emsg(e))
    finally:
        if proc is not None and proc.poll() is None:
            proc.terminate()
            try: proc.wait(timeout=15)
            except Exception: proc.kill()


def check_name_resolve(conn):
    U = tag("CHK-RNAME"); A = tag("CHK-RAMB")
    try:
        cur = conn.cursor(); cur.executemany("INSERT INTO account (name) VALUES (?)", [(U,)]); cur.close()
        cur = conn.cursor(); cur.executemany("INSERT INTO account (name) VALUES (?)", [(A,), (A,)]); cur.close()  # 曖昧
        uid = str(one(conn.cursor(), f"SELECT accountid FROM account WHERE name='{U}'")[0][0])
        # 一意解決
        lu = tag("chk-c-u")
        cur = conn.cursor(); cur.executemany("INSERT /*+ RESOLVE_NAMES */ INTO contact (lastname, parentcustomerid) VALUES (?, ?)", [(lu, f"{CONTACT_LOOKUP_TARGET}:{U}")]); cur.close()
        got = one(conn.cursor(), f"SELECT parentcustomerid FROM contact WHERE lastname='{lu}'")
        ok_unique = bool(got) and str(got[0][0]).lower() == uid.lower()
        # 曖昧 → 行エラー(未作成)
        la = tag("chk-c-a")
        try:
            cur = conn.cursor(); cur.executemany("INSERT /*+ RESOLVE_NAMES */ INTO contact (lastname, parentcustomerid) VALUES (?, ?)", [(la, f"{CONTACT_LOOKUP_TARGET}:{A}")]); cur.close()
        except Exception:
            pass
        amb = one(conn.cursor(), f"SELECT COUNT(*) AS n FROM contact WHERE lastname='{la}'")[0][0]
        record(10, "名前→ID解決", "PASS" if (ok_unique and amb == 0) else "FAIL", f"一意={ok_unique}, 曖昧未作成={amb==0}")
    except Exception as e:
        record(10, "名前→ID解決", "FAIL", emsg(e))


def check_progress(conn):
    try:
        cur = conn.cursor(); cols = one(cur, "SELECT operation, rows_done, status FROM gateway_progress"); cur.close()
        record(11, "進捗照会 gateway_progress", "PASS", f"{len(cols)}件のエントリ取得")
    except Exception as e:
        record(11, "進捗照会 gateway_progress", "FAIL", emsg(e))


def check_auth_methods():
    # 実行中ゲートウェイは ClientSecret 前提 → ClientSecret は疎通済。他は要ゲートウェイ再起動＋人手。
    record(12, "認証4方式", "SKIP", "ClientSecret=稼働中で確認。Certificate/Interactive/DeviceCode は §6/§7 手順で手動")


def check_checkpoint_idempotency(conn):
    t = tag("CHK-CKPT")
    rows = [(t, i) for i in range(25)]
    try:
        for _ in range(2):  # 同一データを2回投入 → 2回目は行追跡でスキップ
            cur = conn.cursor(); cur.executemany("INSERT INTO poc_gadget (poc_name, poc_quantity) VALUES (?, ?)", rows); cur.close()
        cur = conn.cursor(); c = one(cur, f"SELECT COUNT(*) AS n FROM poc_gadget WHERE poc_name='{t}'")[0][0]; cur.close()
        record(13, "チェックポイント冪等(再投入skip)", "PASS" if c == 25 else "FAIL", f"件数{c}(=25期待, 重複なし)")
    except Exception as e:
        record(13, "チェックポイント冪等(再投入skip)", "FAIL", emsg(e))


def check_audit():
    try:
        found = None
        for d in audit_dirs():
            if os.path.isdir(d) and any(f.startswith("audit_") and f.endswith(".csv") for f in os.listdir(d)):
                found = d; break
        if found:
            record(14, "監査ログ出力", "PASS", found)
        else:
            record(14, "監査ログ出力", "FAIL", "audit_*.csv 見つからず。探索: " + " ; ".join(audit_dirs()))
    except Exception as e:
        record(14, "監査ログ出力", "FAIL", emsg(e))


def main():
    print(f"=== Dataverse GW 動作確認チェックリスト（3.2.1 §10）===")
    print(f"GW={GW}  GW_EXE={'set' if GW_EXE else '(未設定→dvload/本番ガードはSKIP)'}  TRUNCATE={'ON' if RUN_TRUNCATE else 'OFF'}\n")
    conn = fs.connect(GW)
    check_select(conn)
    check_insert(conn)
    check_upsert(conn)
    check_delete(conn)
    check_nn(conn)
    check_dvload(conn)
    check_dryrun(conn)
    check_name_resolve(conn)
    check_progress(conn)
    check_checkpoint_idempotency(conn)
    check_audit()
    check_auth_methods()
    conn.close()
    check_production_guard()  # 別ポートで別インスタンス起動するため最後・別接続

    # サマリ
    n_pass = sum(1 for r in results if r[2] == "PASS")
    n_fail = sum(1 for r in results if r[2] == "FAIL")
    n_skip = sum(1 for r in results if r[2] == "SKIP")
    print(f"\n=== 結果: PASS={n_pass} FAIL={n_fail} SKIP={n_skip} ===")
    if n_fail:
        print("FAIL 項目:")
        for no, name, st, detail in results:
            if st == "FAIL":
                print(f"  #{no} {name}: {detail}")
    sys.exit(1 if n_fail else 0)


if __name__ == "__main__":
    main()
