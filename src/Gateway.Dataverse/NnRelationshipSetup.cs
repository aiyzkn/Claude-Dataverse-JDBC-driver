using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;

namespace Gateway.Dataverse;

/// <summary>
/// 検証用の N:N リレーションシップを冪等に作成する(ASSOCIATE 検証の環境準備)。
/// カスタムテーブル間のため隔離・安全。既に存在すればスキップ。
/// </summary>
public static class NnRelationshipSetup
{
    public static string EnsureManyToMany(DataverseConnection connection, ILogger log,
        string entity1, string entity2, string schemaName, string publisherPrefix)
    {
        var client = connection.Client;

        // 既存確認
        try
        {
            var existing = (RetrieveRelationshipResponse)client.Execute(new RetrieveRelationshipRequest { Name = schemaName });
            if (existing.RelationshipMetadata is ManyToManyRelationshipMetadata)
            {
                log.LogInformation("N:N '{Rel}' は既存。作成をスキップ。", schemaName);
                return schemaName;
            }
        }
        catch { /* 未存在 → 作成へ */ }

        var intersectName = $"{publisherPrefix}_{entity1}_{entity2}";
        var req = new CreateManyToManyRequest
        {
            IntersectEntitySchemaName = intersectName,
            ManyToManyRelationship = new ManyToManyRelationshipMetadata
            {
                SchemaName = schemaName,
                Entity1LogicalName = entity1,
                Entity2LogicalName = entity2,
                Entity1AssociatedMenuConfiguration = new AssociatedMenuConfiguration { Behavior = AssociatedMenuBehavior.UseCollectionName, Group = AssociatedMenuGroup.Details, Order = 10000 },
                Entity2AssociatedMenuConfiguration = new AssociatedMenuConfiguration { Behavior = AssociatedMenuBehavior.UseCollectionName, Group = AssociatedMenuGroup.Details, Order = 10000 },
            },
        };
        client.Execute(req);
        log.LogInformation("N:N '{Rel}' を作成(交差 {Intersect})。", schemaName, intersectName);
        return schemaName;
    }
}
