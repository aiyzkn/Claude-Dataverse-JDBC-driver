using System.Collections.Concurrent;
using System.Diagnostics;

namespace Gateway.Dataverse;

/// <summary>
/// 進捗トラッキング(3.1.5)。実行中/直近の書き込み操作の進捗・スループット・残時間(ETA)・状態を保持し、
/// 仮想テーブル `gateway_progress` の SELECT やログで可視化する。別接続からポーリングして
/// DBeaver 等の進捗表示に用いる。安全なキャンセルは ct 伝播＋チェックポイント再開で担保する。
/// </summary>
public sealed class ProgressTracker
{
    /// <summary>進捗1件のスナップショット。ETA/割合は Done/Total とレートから算出。</summary>
    public sealed record Entry(string Id, string Table, string Operation, long Done, long? Total,
        double ElapsedSec, string Status)
    {
        public double RowsPerSec => ElapsedSec > 0 ? Done / ElapsedSec : 0;

        /// <summary>残時間(秒)。総数既知かつ進行中のみ。完了/総数超過は 0、総数不明は null。</summary>
        public double? EtaSec
        {
            get
            {
                if (Total is not long t) return null;
                if (Done >= t) return 0;
                var rate = RowsPerSec;
                return rate > 0 ? (t - Done) / rate : (double?)null;
            }
        }

        /// <summary>進捗率(0-100)。総数不明は null。</summary>
        public int? Percent => Total is long t && t > 0 ? (int)Math.Min(100, 100.0 * Done / t) : null;
    }

    private sealed class Op
    {
        public required string Table;
        public required string Operation;
        public long Done;
        public long? Total;
        public required Stopwatch Sw;
        public string Status = "running";
        public long Seq; // 生成順(プルーニング用)
    }

    private const int MaxRetained = 50; // 完了分の保持上限(古いものからプルーン)
    private readonly ConcurrentDictionary<string, Op> _ops = new();
    private long _counter;

    /// <summary>操作の開始を記録し、進捗 ID を返す。total 不明なら null。</summary>
    public string Begin(string table, string operation, long? total)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        _ops[id] = new Op { Table = table, Operation = operation, Total = total, Sw = Stopwatch.StartNew(), Seq = Interlocked.Increment(ref _counter) };
        Prune();
        return id;
    }

    public void Report(string id, long done)
    {
        if (_ops.TryGetValue(id, out var op)) op.Done = done;
    }

    public void Complete(string id, string status = "completed")
    {
        if (_ops.TryGetValue(id, out var op)) { op.Status = status; op.Sw.Stop(); }
    }

    /// <summary>現在保持している全操作のスナップショット(新しい順)。</summary>
    public IReadOnlyList<Entry> Snapshot()
        => _ops.Select(kv => new Entry(kv.Key, kv.Value.Table, kv.Value.Operation,
                kv.Value.Done, kv.Value.Total, kv.Value.Sw.Elapsed.TotalSeconds, kv.Value.Status))
            .OrderByDescending(e => e.Status == "running").ThenByDescending(e => e.ElapsedSec)
            .ToList();

    private void Prune()
    {
        if (_ops.Count <= MaxRetained) return;
        foreach (var kv in _ops.Where(k => k.Value.Status != "running").OrderBy(k => k.Value.Seq).Take(_ops.Count - MaxRetained))
            _ops.TryRemove(kv.Key, out _);
    }
}
