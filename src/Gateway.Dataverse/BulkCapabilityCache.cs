using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk.Query;

namespace Gateway.Dataverse;

/// <summary>
/// テーブルが CreateMultiple/UpdateMultiple に対応するかを sdkmessagefilter メタデータで
/// 事前判定してキャッシュする(OBS-3 の申し送り)。実行時エラー任せの判定を避け、
/// 非対応テーブルへの無駄なバルク試行を防ぐ。Microsoft 公式 IsMessageAvailable と同一手法。
/// </summary>
public sealed class BulkCapabilityCache
{
    private readonly DataverseConnection _connection;
    private readonly ILogger<BulkCapabilityCache> _log;
    private readonly ConcurrentDictionary<string, bool> _createMultiple = new(StringComparer.OrdinalIgnoreCase);

    public BulkCapabilityCache(DataverseConnection connection, ILogger<BulkCapabilityCache> log)
    {
        _connection = connection;
        _log = log;
    }

    public bool SupportsCreateMultiple(string table)
        => _createMultiple.GetOrAdd(table, t => IsMessageAvailable(t, "CreateMultiple"));

    /// <summary>指定テーブルで指定メッセージが利用可能か(sdkmessagefilter × sdkmessage)。</summary>
    private bool IsMessageAvailable(string entityLogicalName, string messageName)
    {
        var query = new QueryExpression("sdkmessagefilter")
        {
            ColumnSet = new ColumnSet("sdkmessagefilterid"),
            Criteria = new FilterExpression(LogicalOperator.And)
            {
                Conditions = { new ConditionExpression("primaryobjecttypecode", ConditionOperator.Equal, entityLogicalName) },
            },
            LinkEntities =
            {
                new LinkEntity("sdkmessagefilter", "sdkmessage", "sdkmessageid", "sdkmessageid", JoinOperator.Inner)
                {
                    LinkCriteria = new FilterExpression(LogicalOperator.And)
                    {
                        Conditions = { new ConditionExpression("name", ConditionOperator.Equal, messageName) },
                    },
                },
            },
        };

        try
        {
            var count = _connection.Client.RetrieveMultiple(query).Entities.Count;
            var supported = count >= 1;
            _log.LogInformation("経路事前判定: {Table} の {Msg} 対応 = {Supported}", entityLogicalName, messageName, supported);
            return supported;
        }
        catch (Exception ex)
        {
            // 判定に失敗した場合は保守的に「対応」とみなし、実行時フォールバックに委ねる
            _log.LogWarning(ex, "{Table} の {Msg} 事前判定に失敗。対応とみなしフォールバックに委ねる", entityLogicalName, messageName);
            return true;
        }
    }
}
