namespace Gateway.Checkpoint;

/// <summary>
/// 1 ジョブ分のチェックポイント文脈。バルク書き込みのチャンクに連番を割り当て、
/// 既コミット分をスキップし、成功分を記録する(F-11 の再開)。
/// 連番は入力位置に対して決定的(baseSeq + チャンク index)なので、同一入力の再送で再開できる。
/// </summary>
public sealed class CheckpointSession
{
    private readonly CheckpointStore _store;
    private readonly HashSet<long> _committed;
    private readonly HashSet<string> _tracked;
    private readonly object _seqGate = new();
    private readonly object _trackGate = new();
    private long _nextSeq;

    public long JobId { get; }
    public bool IsResumed { get; }
    public int AlreadyCommittedBatches => _committed.Count;

    /// <summary>ローカル追跡キーが有効か(代替キー/GUID の無いプレーン INSERT のみ)。</summary>
    public bool RowTrackingEnabled { get; }

    public CheckpointSession(CheckpointStore store, long jobId, HashSet<long> committed,
        HashSet<string> tracked, bool isResumed, bool rowTrackingEnabled)
    {
        _store = store;
        JobId = jobId;
        _committed = committed;
        _tracked = tracked;
        IsResumed = isResumed;
        RowTrackingEnabled = rowTrackingEnabled;
    }

    /// <summary>n 個のチャンクに連番を予約し、先頭連番を返す(スレッド安全)。</summary>
    public long ReserveSequences(int n)
    {
        lock (_seqGate)
        {
            var start = _nextSeq;
            _nextSeq += n;
            return start;
        }
    }

    /// <summary>この連番のチャンクは既にコミット済みか(スキップ判定)。</summary>
    public bool IsCommitted(long seq) => _committed.Contains(seq);

    /// <summary>チャンクのコミットを記録する。</summary>
    public void Record(long seq, int rowCount) => _store.RecordBatch(JobId, seq, rowCount, DateTime.UtcNow);

    /// <summary>この行ハッシュは作成済みか(ローカル追跡キーによるスキップ判定)。</summary>
    public bool IsRowTracked(string rowHash)
    {
        lock (_trackGate) return _tracked.Contains(rowHash);
    }

    /// <summary>作成済み行(ハッシュ↔GUID)を記録し、セッション内集合にも反映する。</summary>
    public void RecordTrackedRows(IReadOnlyList<(string Hash, string RecordId)> rows)
    {
        if (rows.Count == 0) return;
        _store.RecordTrackedRows(JobId, rows);
        lock (_trackGate)
            foreach (var (h, _) in rows) _tracked.Add(h);
    }

    public void Complete() => _store.CompleteJob(JobId);
}
