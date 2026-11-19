using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Gateway.Dataverse;

/// <summary>
/// 1 つの Dataverse 環境に対する接続・キャッシュ・本番ガード・各エグゼキュータの束(4.2.2 B2)。
/// 接続(クライアント供給の接続先)ごとに生成・キャッシュし、環境間を分離する。
/// 監査・チェックポイント・進捗・ドライラン・セッションは横断的にプロセス共有(コンテキスト外)。
/// </summary>
public sealed class DataverseContext : IDisposable
{
    public DataverseConnection Connection { get; }
    public ProductionGuard Guard { get; }
    public NameResolver NameResolver { get; }
    public PreparedInsertExecutor Inserter { get; }
    public BulkDeleteExecutor BulkDelete { get; }
    public AssociateExecutor Associate { get; }
    public DdlExecutor Ddl { get; }

    public DataverseContext(DataverseConnection connection, IConfiguration config,
        Gateway.Checkpoint.CheckpointStore? checkpoint, ILoggerFactory lf)
    {
        Connection = connection;
        var metadata = new EntityMetadataCache(connection);
        var bulkCap = new BulkCapabilityCache(connection, lf.CreateLogger<BulkCapabilityCache>());
        var privilege = new PrivilegeChecker(connection, lf.CreateLogger<PrivilegeChecker>());
        NameResolver = new NameResolver(connection, metadata, config, lf.CreateLogger<NameResolver>());
        // 本番判定は接続先 URL ごと(環境別)に行う
        Guard = ProductionGuard.Create(connection.Client.ConnectedOrgUriActual?.ToString(), config,
            lf.CreateLogger<ProductionGuard>());
        Inserter = new PreparedInsertExecutor(connection, metadata, bulkCap, privilege, config, Guard, NameResolver,
            lf.CreateLogger<PreparedInsertExecutor>(), checkpoint);
        BulkDelete = new BulkDeleteExecutor(connection, metadata, config, Guard, lf.CreateLogger<BulkDeleteExecutor>());
        Associate = new AssociateExecutor(connection, config, Guard, lf.CreateLogger<AssociateExecutor>());
        Ddl = new DdlExecutor(connection, metadata, lf.CreateLogger<DdlExecutor>());
    }

    public void Dispose() => Connection.Client.Dispose();
}
