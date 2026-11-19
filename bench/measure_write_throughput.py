"""4.2.4 複数SPスケールアウト 書き込みスループット実測ハーネス。

同一ワークロード(N 行 INSERT)を、ゲートウェイの構成違い(単一SP / 複数SP)に対して実行し
rows/sec を比較する。ゲートウェイ側のスケールアウトは起動時環境変数
`Gateway__ScaleOut__Principals__*` で構成する(本スクリプトはクライアント=投入側)。

使い方:
  # 1) ベースライン(単一SP): スケールアウト無しでゲートウェイ起動 → 実行
  # 2) スケールアウト(2SP): Gateway__ScaleOut__Principals__0__* を設定してゲートウェイ起動 → 実行
  ROWS=3000 LABEL=baseline-1sp python bench/measure_write_throughput.py
  ROWS=3000 LABEL=scaleout-2sp python bench/measure_write_throughput.py

環境変数:
  GATEWAY_URI (既定 grpc://localhost:32010) / TABLE (既定 poc_gadget) / NAMECOL (既定 poc_name)
  ROWS (既定 3000) / CHUNK (executemany 1 回の行数。既定 ROWS=一括) / LABEL (出力ラベル)

一意な行値を用いるためチェックポイント重複排除でスキップされない。実行後は投入分を削除する。
"""
import os
import time
import uuid
import adbc_driver_flightsql.dbapi as flightsql

URI = os.environ.get("GATEWAY_URI", "grpc://localhost:32010")
TABLE = os.environ.get("TABLE", "poc_gadget")
NAMECOL = os.environ.get("NAMECOL", "poc_name")
ROWS = int(os.environ.get("ROWS", "3000"))
CHUNK = int(os.environ.get("CHUNK", str(ROWS)))
LABEL = os.environ.get("LABEL", "run")
# CLEANUP=1(既定) で投入分を削除。大量(>Gateway:SyncDeleteMaxRows)だと BulkDelete 非同期待ちで
# 遅くなるため、複数回連続計測時は CLEANUP=0 にして最後に一括削除するとよい。
CLEANUP = os.environ.get("CLEANUP", "1") != "0"
RUN = uuid.uuid4().hex[:8]
PREFIX = f"tp-{RUN}-"


def main():
    with flightsql.connect(URI) as c, c.cursor() as cur:
        inserted = 0
        t0 = time.time()
        for base in range(0, ROWS, CHUNK):
            n = min(CHUNK, ROWS - base)
            rows = [(f"{PREFIX}{base + i}",) for i in range(n)]
            cur.executemany(f"INSERT INTO {TABLE} ({NAMECOL}) VALUES (?)", rows)
            inserted += n
        dt = time.time() - t0
        rps = inserted / dt if dt > 0 else 0.0
        print(f"[{LABEL}] rows={inserted} elapsed={dt:.1f}s throughput={rps:.1f} rows/sec", flush=True)

        # 後始末(投入分のみ削除)。CLEANUP=0 ならスキップ(PREFIX を出力して後で一括削除できるように)
        if CLEANUP:
            cur.execute(f"DELETE FROM {TABLE} WHERE {NAMECOL} LIKE '{PREFIX}%'")
        else:
            print(f"[{LABEL}] cleanup skipped; rows kept with prefix '{PREFIX}'", flush=True)


if __name__ == "__main__":
    main()
