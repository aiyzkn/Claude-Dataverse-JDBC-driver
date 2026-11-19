using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;

namespace Gateway.Dataverse;

/// <summary>
/// 検証用に代替キーを冪等作成し、アクティブ化(インデックス作成ジョブ完了)まで待つ。
/// 代替キー参照 Lookup(F-06)の実環境検証の環境準備。
/// </summary>
public static class AltKeySetup
{
    public static void EnsureAlternateKey(DataverseConnection connection, ILogger log,
        string entity, string keyAttribute, string keySchemaName)
    {
        var client = connection.Client;

        // 既存確認(アクティブなら何もしない)
        var meta = (RetrieveEntityResponse)client.Execute(new RetrieveEntityRequest
        {
            LogicalName = entity,
            EntityFilters = EntityFilters.Entity,
        });
        var existing = meta.EntityMetadata.Keys?.FirstOrDefault(k =>
            string.Equals(k.SchemaName, keySchemaName, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            client.Execute(new CreateEntityKeyRequest
            {
                EntityName = entity,
                EntityKey = new EntityKeyMetadata
                {
                    SchemaName = keySchemaName,
                    DisplayName = new Label(keySchemaName, 1041),
                    KeyAttributes = new[] { keyAttribute },
                },
            });
            log.LogInformation("代替キー '{Key}' を作成({Entity}.{Attr})", keySchemaName, entity, keyAttribute);
        }
        else
        {
            log.LogInformation("代替キー '{Key}' は既存(status={Status})", keySchemaName, existing.EntityKeyIndexStatus);
        }

        // アクティブ化待ち(インデックス作成ジョブ)
        var deadline = DateTime.UtcNow.AddMinutes(5);
        while (true)
        {
            var m = (RetrieveEntityResponse)client.Execute(new RetrieveEntityRequest { LogicalName = entity, EntityFilters = EntityFilters.Entity });
            var key = m.EntityMetadata.Keys?.FirstOrDefault(k => string.Equals(k.SchemaName, keySchemaName, StringComparison.OrdinalIgnoreCase));
            if (string.Equals(key?.EntityKeyIndexStatus.ToString(), "Active", StringComparison.OrdinalIgnoreCase))
            {
                log.LogInformation("代替キー '{Key}' はアクティブ", keySchemaName);
                return;
            }
            if (DateTime.UtcNow > deadline)
            {
                log.LogWarning("代替キー '{Key}' のアクティブ化待ちタイムアウト(status={Status})", keySchemaName, key?.EntityKeyIndexStatus);
                return;
            }
            Thread.Sleep(3000);
        }
    }
}
