using System.Collections.Concurrent;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;

namespace Gateway.Dataverse;

/// <summary>属性 1 列の書き込み変換に必要な型情報(2.1.4/F-13)。</summary>
public sealed record AttributeInfo(
    string LogicalName,
    AttributeTypeCode TypeCode,
    IReadOnlyList<string> LookupTargets,
    DateTimeBehavior? DateTimeBehavior)
{
    /// <summary>単一ターゲット Lookup のターゲット。複数(ポリモーフィック)なら null。</summary>
    public string? SingleLookupTarget => LookupTargets.Count == 1 ? LookupTargets[0] : null;
}

/// <summary>
/// エンティティの属性メタデータ(型・Lookup ターゲット・DateTime 挙動)を取得しキャッシュする。
/// 書き込み時に Arrow 値を正しい SDK 型(EntityReference/OptionSetValue/Money 等)へ変換するために使う。
/// </summary>
public sealed class EntityMetadataCache
{
    private readonly DataverseConnection _connection;
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, AttributeInfo>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (string IdAttribute, string? NameAttribute)> _primary = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _elastic = new(StringComparer.OrdinalIgnoreCase);

    public EntityMetadataCache(DataverseConnection connection) => _connection = connection;

    /// <summary>エラスティックテーブル(Cosmos バッキング)か。DeleteMultiple 経路判定に使う(4.2.3)。</summary>
    public bool IsElastic(string table)
        => _elastic.GetOrAdd(table, t =>
        {
            var resp = (RetrieveEntityResponse)_connection.Client.Execute(new RetrieveEntityRequest
            {
                LogicalName = t,
                EntityFilters = EntityFilters.Entity,
                RetrieveAsIfPublished = true,
            });
            return string.Equals(resp.EntityMetadata.TableType, "Elastic", StringComparison.OrdinalIgnoreCase);
        });

    public IReadOnlyDictionary<string, AttributeInfo> GetAttributes(string table)
        => _cache.GetOrAdd(table, Load);

    /// <summary>プライマリ ID 属性・プライマリ名属性(名前→ID 解決 3.1.4 用)。名前属性が無ければ NameAttribute=null。</summary>
    public (string IdAttribute, string? NameAttribute) PrimaryAttributes(string table)
        => _primary.GetOrAdd(table, t =>
        {
            var resp = (RetrieveEntityResponse)_connection.Client.Execute(new RetrieveEntityRequest
            {
                LogicalName = t,
                EntityFilters = EntityFilters.Entity,
                RetrieveAsIfPublished = true,
            });
            var em = resp.EntityMetadata;
            return (em.PrimaryIdAttribute ?? t + "id",
                    string.IsNullOrEmpty(em.PrimaryNameAttribute) ? null : em.PrimaryNameAttribute);
        });

    public AttributeInfo? Find(string table, string column)
        => GetAttributes(table).TryGetValue(column, out var info) ? info : null;

    private IReadOnlyDictionary<string, AttributeInfo> Load(string table)
    {
        var resp = (RetrieveEntityResponse)_connection.Client.Execute(new RetrieveEntityRequest
        {
            LogicalName = table,
            EntityFilters = EntityFilters.Attributes,
            RetrieveAsIfPublished = true,
        });

        var map = new Dictionary<string, AttributeInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in resp.EntityMetadata.Attributes)
        {
            if (string.IsNullOrEmpty(a.LogicalName) || a.AttributeType is null) continue;

            IReadOnlyList<string> targets = a is LookupAttributeMetadata lam && lam.Targets is not null
                ? lam.Targets
                : System.Array.Empty<string>();
            DateTimeBehavior? dtb = a is DateTimeAttributeMetadata dtm ? dtm.DateTimeBehavior : null;

            map[a.LogicalName] = new AttributeInfo(a.LogicalName, a.AttributeType.Value, targets, dtb);
        }
        return map;
    }
}
