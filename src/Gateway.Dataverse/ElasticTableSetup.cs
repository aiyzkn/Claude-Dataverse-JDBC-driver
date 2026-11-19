using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;

namespace Gateway.Dataverse;

/// <summary>
/// 検証用にエラスティックテーブル(Cosmos バッキング)を冪等作成する(4.2.3 DeleteMultiple 経路の実機検証準備)。
/// エラスティックは <see cref="EntityMetadata.TableType"/> = "Elastic" を指定して作成する。
/// partitionid 列は自動付与される。標準テーブルとは異なり DeleteMultiple が利用可能。
/// </summary>
public static class ElasticTableSetup
{
    public static string EnsureElastic(DataverseConnection connection, ILogger log,
        string schemaName, string primaryAttribute)
    {
        var client = connection.Client;
        var logical = schemaName.ToLowerInvariant();

        // 既存確認(あれば TableType を確認して終了)
        try
        {
            var meta = (RetrieveEntityResponse)client.Execute(new RetrieveEntityRequest
            {
                LogicalName = logical,
                EntityFilters = EntityFilters.Entity,
            });
            log.LogInformation("既存テーブル {Table}(TableType={Type})。作成をスキップ",
                schemaName, meta.EntityMetadata.TableType);
            return schemaName;
        }
        catch (Exception)
        {
            // 見つからない → 作成へ
        }

        var lang = GetBaseLanguage(client, log);
        var entity = new EntityMetadata
        {
            SchemaName = schemaName,
            DisplayName = new Label(schemaName, lang),
            DisplayCollectionName = new Label(schemaName, lang),
            OwnershipType = OwnershipTypes.UserOwned,
            TableType = "Elastic",  // ← エラスティック(Cosmos バッキング)指定
            IsActivity = false,
            // エラスティックはチャート非対応。未指定だと作成時にチャート生成を試み
            // "Cannot create charts for virtual Entity" で失敗するため明示的に無効化する。
            CanCreateCharts = new BooleanManagedProperty(false),
        };
        var primary = new StringAttributeMetadata
        {
            SchemaName = primaryAttribute,
            MaxLength = 200,
            FormatName = StringFormatName.Text,
            RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None),
            DisplayName = new Label(primaryAttribute, lang),
        };
        client.Execute(new CreateEntityRequest
        {
            Entity = entity,
            PrimaryAttribute = primary,
            HasActivities = false,
            HasNotes = false,
        });
        log.LogInformation("エラスティックテーブル作成: {Table}(主名 {Primary})", schemaName, primaryAttribute);
        return schemaName;
    }

    private static int GetBaseLanguage(Microsoft.PowerPlatform.Dataverse.Client.ServiceClient client, ILogger log)
    {
        try
        {
            var org = client.RetrieveMultiple(new QueryExpression("organization")
            { ColumnSet = new ColumnSet("languagecode") }).Entities.FirstOrDefault();
            return org?.GetAttributeValue<int>("languagecode") is int lc and > 0 ? lc : 1033;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "組織の基本言語取得に失敗。1033(en-US)を使用");
            return 1033;
        }
    }
}
