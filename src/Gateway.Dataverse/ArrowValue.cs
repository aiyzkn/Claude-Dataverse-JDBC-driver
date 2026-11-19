using Apache.Arrow;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;

namespace Gateway.Dataverse;

/// <summary>
/// Arrow 配列の 1 セルを Dataverse 属性値(CLR オブジェクト)に変換する。
/// メタデータがある場合は属性型に応じて Lookup=EntityReference・Choice=OptionSetValue・
/// Money=Money・DateTime(挙動別)へロスレス変換する(2.1.4/F-13)。
/// </summary>
public static class ArrowValue
{
    /// <summary>属性メタデータに基づく型付き変換。info が null なら素の CLR 値。
    /// resolver+resolveNames を与えると、GUID でない Lookup 値をプライマリ名→ID 解決する(3.1.4)。</summary>
    public static object? ToAttributeValue(IArrowArray array, int index, AttributeInfo? info,
        NameResolver? resolver = null, bool resolveNames = false)
    {
        var raw = Get(array, index);
        if (raw is null || info is null) return raw;

        switch (info.TypeCode)
        {
            case AttributeTypeCode.Lookup:
            case AttributeTypeCode.Customer:
            case AttributeTypeCode.Owner:
                // 単一ターゲットは GUID → EntityReference。ポリモーフィック(複数ターゲット)は
                // 値を "entitylogicalname:guid" 形式で受け、対象エンティティを特定する。
                return ToEntityReference(raw, info.LookupTargets, resolver, resolveNames);

            case AttributeTypeCode.PartyList:
                // activityparty: "entity:guid[,entity:guid...]" を activityparty コレクションへ変換
                return ToActivityParties(raw);

            case AttributeTypeCode.Picklist:
            case AttributeTypeCode.State:
            case AttributeTypeCode.Status:
                return new OptionSetValue(Convert.ToInt32(raw));

            case AttributeTypeCode.Money:
                return new Money(Convert.ToDecimal(raw));

            case AttributeTypeCode.DateTime:
                return CoerceDateTime(raw, info.DateTimeBehavior);

            case AttributeTypeCode.Boolean:
                return Convert.ToBoolean(raw);

            case AttributeTypeCode.Uniqueidentifier:
                return ToGuid(raw);

            case AttributeTypeCode.Integer:
                return Convert.ToInt32(raw);
            case AttributeTypeCode.BigInt:
                return Convert.ToInt64(raw);
            case AttributeTypeCode.Decimal:
                return Convert.ToDecimal(raw);
            case AttributeTypeCode.Double:
                return Convert.ToDouble(raw);

            default:
                return raw; // String/Memo 等はそのまま
        }
    }

    private static Guid ToGuid(object raw) => raw switch
    {
        Guid g => g,
        string s => Guid.Parse(s),
        _ => throw new InvalidOperationException($"GUID に変換できない値: {raw}"),
    };

    /// <summary>
    /// Lookup 値を EntityReference に解決する。対応形式:
    ///   GUID                        … 単一ターゲット
    ///   entity:guid                 … ポリモーフィック(型ヒント)
    ///   keyName=keyValue            … 代替キー参照(単一ターゲット。F-06)
    ///   entity:keyName=keyValue     … 代替キー参照(ポリモーフィック)
    /// 複数ターゲットで型ヒントが無い場合は例外。
    /// </summary>
    private static object? ToEntityReference(object raw, IReadOnlyList<string> targets,
        NameResolver? resolver = null, bool resolveNames = false)
    {
        if (raw is Guid g)
            return new EntityReference(ResolveEntity(null, targets), g);

        var s = raw.ToString()!;

        // 先頭に "entity:" があれば型ヒント。ただし後続が key=value ではなく guid の場合もある
        string? entity = null;
        var remainder = s;
        var colon = s.IndexOf(':');
        if (colon > 0 && !s[..colon].Contains('=')) // "key=value" の値側に : が入る誤検出を避ける
        {
            entity = s[..colon];
            remainder = s[(colon + 1)..];
        }

        var eq = remainder.IndexOf('=');
        if (eq > 0)
        {
            // 代替キー参照(F-06)
            var keyName = remainder[..eq].Trim();
            var keyValue = remainder[(eq + 1)..];
            return new EntityReference(ResolveEntity(entity, targets), keyName, keyValue);
        }

        // GUID 参照
        if (Guid.TryParse(remainder, out var gid))
            return new EntityReference(ResolveEntity(entity, targets), gid);

        // 名前→ID 解決(3.1.4 / R-5。既定 OFF。曖昧・未一致は resolver が例外→行エラー)
        if (resolveNames && resolver is not null)
        {
            var target = ResolveEntity(entity, targets);
            return new EntityReference(target, resolver.Resolve(target, remainder));
        }
        throw new InvalidOperationException(
            $"Lookup 値 '{remainder}' は GUID ではありません。名前で参照するには名前→ID解決(RESOLVE_NAMES)を有効にするか、GUID/代替キーで指定してください。");
    }

    /// <summary>型ヒント entity とターゲット集合から対象エンティティを決める。</summary>
    private static string ResolveEntity(string? entity, IReadOnlyList<string> targets)
    {
        if (entity is not null)
        {
            if (targets.Count > 0 && !targets.Contains(entity, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"'{entity}' は対象 Lookup のターゲットではありません。");
            return entity;
        }
        if (targets.Count == 1) return targets[0];
        throw new InvalidOperationException("ポリモーフィック Lookup には 'entity:...' 形式の型ヒントが必要です。");
    }

    /// <summary>PartyList 値("entity:guid[,entity:guid...]")を activityparty の EntityCollection へ。</summary>
    private static EntityCollection ToActivityParties(object raw)
    {
        var parties = new EntityCollection { EntityName = "activityparty" };
        foreach (var token in raw.ToString()!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = token.IndexOf(':');
            if (colon <= 0) throw new InvalidOperationException($"activityparty は 'entity:guid' 形式が必要: {token}");
            var party = new Entity("activityparty");
            party["partyid"] = new EntityReference(token[..colon], Guid.Parse(token[(colon + 1)..]));
            parties.Entities.Add(party);
        }
        return parties;
    }

    private static DateTime CoerceDateTime(object raw, DateTimeBehavior? behavior)
    {
        var dt = raw switch
        {
            DateTime d => d,
            DateTimeOffset o => o.UtcDateTime,
            _ => Convert.ToDateTime(raw),
        };
        // DateOnly は日付のみ、その他(UserLocal/TimeZoneIndependent)は UTC として送る
        if (behavior?.Value == DateTimeBehavior.DateOnly.Value)
            return DateTime.SpecifyKind(dt.Date, DateTimeKind.Unspecified);
        return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
    }

    public static object? Get(IArrowArray array, int index)
    {
        if (array.IsNull(index)) return null;

        return array switch
        {
            StringArray a => a.GetString(index),
            BooleanArray a => a.GetValue(index),
            Int8Array a => (int)a.GetValue(index)!,
            Int16Array a => (int)a.GetValue(index)!,
            Int32Array a => a.GetValue(index),
            // 生の long を返し、属性型に応じた丸め(Integer→Int32 等)は ToAttributeValue が行う。
            // ここで Int32 へ切り詰めると BigInt 属性で桁落ち/オーバーフローする。
            Int64Array a => a.GetValue(index),
            FloatArray a => (double)a.GetValue(index)!,
            DoubleArray a => a.GetValue(index),
            TimestampArray a => a.GetTimestamp(index)?.UtcDateTime,
            Date32Array a => a.GetDateTime(index),
            Date64Array a => a.GetDateTime(index),
            _ => array.ToString(),
        };
    }
}
