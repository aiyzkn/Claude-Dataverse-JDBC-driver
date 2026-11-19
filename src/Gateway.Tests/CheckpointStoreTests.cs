using Gateway.Checkpoint;
using Xunit;

namespace Gateway.Tests;

/// <summary>チェックポイントストアとジョブ同定の仕様(F-11/D-15)。</summary>
public class CheckpointStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"cp_{Guid.NewGuid():N}.db");
    private static readonly DateTime Now = new(2026, 7, 7, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void New_job_then_resume_same_fingerprint_returns_same_id()
    {
        long id;
        using (var store = new CheckpointStore(_path))
        {
            var j1 = store.StartOrResume("fp-1", "poc_gadget", Now);
            Assert.True(j1.IsNew);
            id = j1.JobId;
        }
        // プロセス再起動を模して開き直す
        using (var store = new CheckpointStore(_path))
        {
            var j2 = store.StartOrResume("fp-1", "poc_gadget", Now);
            Assert.False(j2.IsNew);
            Assert.Equal(id, j2.JobId);
        }
    }

    [Fact]
    public void Different_fingerprint_is_a_new_job()
    {
        using var store = new CheckpointStore(_path);
        var a = store.StartOrResume("fp-a", "t", Now);
        var b = store.StartOrResume("fp-b", "t", Now);
        Assert.NotEqual(a.JobId, b.JobId);
    }

    [Fact]
    public void Committed_batches_are_persisted_and_resumable()
    {
        long id;
        using (var store = new CheckpointStore(_path))
        {
            id = store.StartOrResume("fp-2", "t", Now).JobId;
            store.RecordBatch(id, 0, 100, Now);
            store.RecordBatch(id, 1, 100, Now);
            Assert.Equal(200, store.CommittedRowCount(id));
        }
        using (var store = new CheckpointStore(_path))
        {
            var committed = store.GetCommittedBatches(id);
            // 中断→再開: バッチ 0,1 は済み、2 以降のみ処理すればよい
            Assert.Contains(0L, committed);
            Assert.Contains(1L, committed);
            Assert.DoesNotContain(2L, committed);
            Assert.Equal(200, store.CommittedRowCount(id));
        }
    }

    [Fact]
    public void RecordBatch_is_idempotent()
    {
        using var store = new CheckpointStore(_path);
        var id = store.StartOrResume("fp-3", "t", Now).JobId;
        store.RecordBatch(id, 0, 100, Now);
        store.RecordBatch(id, 0, 100, Now); // 二重記録は無視される
        Assert.Equal(100, store.CommittedRowCount(id));
    }

    [Fact]
    public void Tracked_rows_persist_and_resume_across_reopen()
    {
        long id;
        using (var store = new CheckpointStore(_path))
        {
            id = store.StartOrResume("fp-track", "poc_gadget", Now).JobId;
            store.RecordTrackedRows(id, new[] { ("h1", "g1"), ("h2", "g2") });
            Assert.Equal(new[] { "h1", "h2" }.OrderBy(x => x), store.GetTrackedHashes(id).OrderBy(x => x));
        }
        using (var store = new CheckpointStore(_path))
        {
            var tracked = store.GetTrackedHashes(id);
            Assert.Contains("h1", tracked);   // 作成済み → 再開時スキップ対象
            Assert.DoesNotContain("h3", tracked);
        }
    }

    [Fact]
    public void RecordTrackedRows_is_idempotent_on_same_hash()
    {
        using var store = new CheckpointStore(_path);
        var id = store.StartOrResume("fp-track2", "t", Now).JobId;
        store.RecordTrackedRows(id, new[] { ("h1", "g1") });
        store.RecordTrackedRows(id, new[] { ("h1", "g1-dup") }); // 同一ハッシュは無視
        Assert.Single(store.GetTrackedHashes(id));
    }

    [Fact]
    public void Fingerprint_is_stable_and_sensitive_to_inputs()
    {
        var cols = new[] { "poc_name", "poc_quantity" };
        var f1 = JobFingerprint.Compute("https://org.crm.dynamics.com/", "poc_gadget", cols, 1000, "a", "z");
        var f2 = JobFingerprint.Compute("https://org.crm.dynamics.com", "poc_gadget", cols, 1000, "a", "z");
        Assert.Equal(f1, f2); // 末尾スラッシュ・大小は正規化
        var f3 = JobFingerprint.Compute("https://org.crm.dynamics.com/", "poc_gadget", cols, 1001, "a", "z");
        Assert.NotEqual(f1, f3); // 行数が違えば別ジョブ
    }

    public void Dispose()
    {
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(_path)!, Path.GetFileName(_path) + "*"))
            try { File.Delete(f); } catch { /* WAL 付随ファイルの削除失敗は無視 */ }
    }
}
