using Microsoft.Data.Sqlite;

namespace Gateway.Checkpoint;

public sealed record JobState(long JobId, string Fingerprint, string Table, bool IsNew);

/// <summary>
/// チェックポイントのローカル状態ストア(SQLite WAL)。ジョブ単位・バッチ単位のコミット記録を保持し、
/// 中断後の再開で未完了バッチのみ処理できるようにする(F-11/D-11)。
///
/// スキーマ:
///   jobs(job_id PK, fingerprint UNIQUE, table_name, created_utc, status)
///   committed_batches(job_id, batch_seq, row_count, committed_utc, PK(job_id,batch_seq))
/// </summary>
public sealed class CheckpointStore : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly object _gate = new(); // 単一接続への並列アクセスを直列化

    public CheckpointStore(string databasePath)
    {
        var dir = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _db = new SqliteConnection($"Data Source={databasePath}");
        _db.Open();
        // 破損検知(2.4.2): 破損した DB のまま再開すると誤ってスキップ/重複しうるため、
        // 開いた時点で integrity_check し、異常なら明確なエラーで停止する(DB 削除で再作成可)。
        EnsureIntegrity(databasePath);
        Exec("PRAGMA journal_mode=WAL;");
        Exec("PRAGMA synchronous=FULL;");
        Exec(@"CREATE TABLE IF NOT EXISTS jobs(
                 job_id INTEGER PRIMARY KEY AUTOINCREMENT,
                 fingerprint TEXT NOT NULL UNIQUE,
                 table_name TEXT NOT NULL,
                 created_utc TEXT NOT NULL,
                 status TEXT NOT NULL DEFAULT 'running');");
        Exec(@"CREATE TABLE IF NOT EXISTS committed_batches(
                 job_id INTEGER NOT NULL,
                 batch_seq INTEGER NOT NULL,
                 row_count INTEGER NOT NULL,
                 committed_utc TEXT NOT NULL,
                 PRIMARY KEY(job_id, batch_seq));");
        // ローカル追跡キー(D-14 ③): 代替キー/GUID の無い行の内容ハッシュ↔発番 GUID
        Exec(@"CREATE TABLE IF NOT EXISTS tracked_rows(
                 job_id INTEGER NOT NULL,
                 row_hash TEXT NOT NULL,
                 record_id TEXT,
                 PRIMARY KEY(job_id, row_hash));");
    }

    /// <summary>フィンガープリントでジョブを同定。既存なら再開、無ければ新規採番。</summary>
    public JobState StartOrResume(string fingerprint, string table, DateTime nowUtc)
    {
        lock (_gate)
        {
        using var find = _db.CreateCommand();
        find.CommandText = "SELECT job_id FROM jobs WHERE fingerprint=$fp;";
        find.Parameters.AddWithValue("$fp", fingerprint);
        var existing = find.ExecuteScalar();
        if (existing is not null and not DBNull)
            return new JobState((long)existing, fingerprint, table, IsNew: false);

        using var ins = _db.CreateCommand();
        ins.CommandText = @"INSERT INTO jobs(fingerprint, table_name, created_utc)
                            VALUES($fp,$t,$c); SELECT last_insert_rowid();";
        ins.Parameters.AddWithValue("$fp", fingerprint);
        ins.Parameters.AddWithValue("$t", table);
        ins.Parameters.AddWithValue("$c", nowUtc.ToString("o"));
        var id = (long)ins.ExecuteScalar()!;
        return new JobState(id, fingerprint, table, IsNew: true);
        }
    }

    /// <summary>コミット済みバッチ連番の集合(再開時のスキップ判定用)。</summary>
    public HashSet<long> GetCommittedBatches(long jobId)
    {
        lock (_gate)
        {
        var set = new HashSet<long>();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT batch_seq FROM committed_batches WHERE job_id=$j;";
        cmd.Parameters.AddWithValue("$j", jobId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) set.Add(r.GetInt64(0));
        return set;
        }
    }

    /// <summary>バッチのコミットを記録(バルク書き込みの成功後に呼ぶ)。</summary>
    public void RecordBatch(long jobId, long batchSeq, int rowCount, DateTime nowUtc)
    {
        lock (_gate)
        {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = @"INSERT OR IGNORE INTO committed_batches(job_id,batch_seq,row_count,committed_utc)
                            VALUES($j,$s,$n,$c);";
        cmd.Parameters.AddWithValue("$j", jobId);
        cmd.Parameters.AddWithValue("$s", batchSeq);
        cmd.Parameters.AddWithValue("$n", rowCount);
        cmd.Parameters.AddWithValue("$c", nowUtc.ToString("o"));
        cmd.ExecuteNonQuery();
        }
    }

    /// <summary>これまでにコミットされた行数の合計。</summary>
    public long CommittedRowCount(long jobId)
    {
        lock (_gate)
        {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(SUM(row_count),0) FROM committed_batches WHERE job_id=$j;";
        cmd.Parameters.AddWithValue("$j", jobId);
        return (long)cmd.ExecuteScalar()!;
        }
    }

    public void CompleteJob(long jobId)
    {
        lock (_gate)
        {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "UPDATE jobs SET status='completed' WHERE job_id=$j;";
        cmd.Parameters.AddWithValue("$j", jobId);
        cmd.ExecuteNonQuery();
        }
    }

    /// <summary>ジョブの追跡済み行ハッシュ集合(再開時のスキップ判定用)。</summary>
    public HashSet<string> GetTrackedHashes(long jobId)
    {
        lock (_gate)
        {
        var set = new HashSet<string>();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT row_hash FROM tracked_rows WHERE job_id=$j;";
        cmd.Parameters.AddWithValue("$j", jobId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) set.Add(r.GetString(0));
        return set;
        }
    }

    /// <summary>作成済み行(内容ハッシュ↔発番 GUID)をまとめて記録する。</summary>
    public void RecordTrackedRows(long jobId, IEnumerable<(string Hash, string RecordId)> rows)
    {
        lock (_gate)
        {
        using var tx = _db.BeginTransaction();
        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"INSERT OR IGNORE INTO tracked_rows(job_id,row_hash,record_id) VALUES($j,$h,$r);";
        var pj = cmd.Parameters.Add("$j", SqliteType.Integer);
        var ph = cmd.Parameters.Add("$h", SqliteType.Text);
        var pr = cmd.Parameters.Add("$r", SqliteType.Text);
        foreach (var (hash, id) in rows)
        {
            pj.Value = jobId; ph.Value = hash; pr.Value = (object?)id ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        }
    }

    /// <summary>SQLite の整合性を検査し、破損していれば明確なエラーで停止する(2.4.2)。</summary>
    private void EnsureIntegrity(string databasePath)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check;";
        var result = cmd.ExecuteScalar() as string;
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"チェックポイント DB が破損しています({result})。ファイルを削除して再作成してください: {databasePath}");
    }

    private void Exec(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _db.Dispose();
}
