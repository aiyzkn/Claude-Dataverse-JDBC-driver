using System.Collections.Concurrent;
using System.Data.Common;
using Apache.Arrow;

namespace Gateway.Flight;

/// <summary>
/// GetFlightInfo で開始したクエリ。DoGet が来るまで保持する。
/// 結果種別: Reader あり=SELECT / StaticBatches あり=メタデータ応答 / どちらも null=RowsAffected。
/// Connection/Command は基底型(Sql4Cds への直接依存を避ける)。
/// </summary>
public sealed class ActiveQuery : IDisposable
{
    public required string Handle { get; init; }
    public required string Sql { get; init; }
    public DbConnection? Connection { get; init; }
    public DbCommand? Command { get; init; }
    public DbDataReader? Reader { get; init; }
    public required Schema Schema { get; init; }
    public long RowsAffected { get; init; } = -1;
    public IReadOnlyList<RecordBatch>? StaticBatches { get; init; }

    public void Dispose()
    {
        Reader?.Dispose();
        Command?.Dispose();
        Connection?.Dispose();
    }
}

/// <summary>gRPC 呼び出しをまたいで状態を保持するシングルトンストア。</summary>
public sealed class StatementRegistry
{
    private readonly ConcurrentDictionary<string, ActiveQuery> _activeQueries = new();
    private readonly ConcurrentDictionary<string, string> _preparedStatements = new();

    public void Register(ActiveQuery query) => _activeQueries[query.Handle] = query;

    public bool TryTake(string handle, out ActiveQuery? query) => _activeQueries.TryRemove(handle, out query);

    public string RegisterPrepared(string sql)
    {
        var handle = Guid.NewGuid().ToString("N");
        _preparedStatements[handle] = sql;
        return handle;
    }

    public bool TryGetPrepared(string handle, out string? sql) => _preparedStatements.TryGetValue(handle, out sql);

    public void RemovePrepared(string handle) => _preparedStatements.TryRemove(handle, out _);
}
