using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;

namespace Gateway.Dataverse;

/// <summary>
/// DDL サブセット(4.2.1): `CREATE TABLE` / `ALTER TABLE ... ADD` を Dataverse のメタデータ API
/// (CreateEntityRequest / CreateAttributeRequest)へ変換する。移行前のスキーマ準備用途。
///
/// 安全な部分集合のみ対応:
///   CREATE TABLE prefix_name (col type [PRIMARY], ...)   -- 先頭の string 列(または PRIMARY 指定)を主名属性に
///   ALTER TABLE prefix_name ADD col type
/// 論理名にはパブリッシャー接頭辞(prefix_)が必要(Dataverse 制約)。型は安全な基本型のみ。
/// </summary>
public sealed class DdlExecutor
{
    public enum DdlKind { CreateTable, AlterTableAdd, DropTable }

    public sealed record ColumnDef(string Name, string SqlType, bool IsPrimary);
    public sealed record DdlCommand(DdlKind Kind, string Table, IReadOnlyList<ColumnDef> Columns);

    private static readonly Regex DropPattern = new(
        @"^\s*DROP\s+TABLE\s+(?<table>[\w.\[\]""]+)\s*;?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CreatePattern = new(
        @"^\s*CREATE\s+TABLE\s+(?<table>[\w.\[\]""]+)\s*\(\s*(?<cols>.+?)\s*\)\s*;?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex AlterPattern = new(
        @"^\s*ALTER\s+TABLE\s+(?<table>[\w.\[\]""]+)\s+ADD\s+(?<col>[\w\[\]""]+)\s+(?<type>[\w]+(\s*\(\s*\d+(\s*,\s*\d+)?\s*\))?)\s*;?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly DataverseConnection _connection;
    private readonly EntityMetadataCache _metadata;
    private readonly ILogger<DdlExecutor> _log;
    private readonly Lazy<int> _lang;

    public DdlExecutor(DataverseConnection connection, EntityMetadataCache metadata, ILogger<DdlExecutor> log)
    {
        _connection = connection;
        _metadata = metadata;
        _log = log;
        _lang = new Lazy<int>(GetBaseLanguage, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>DDL 文を解析する。対象外は null。</summary>
    public static DdlCommand? TryParse(string sql)
    {
        var mc = CreatePattern.Match(sql);
        if (mc.Success)
        {
            var cols = SplitColumns(mc.Groups["cols"].Value);
            if (cols.Count == 0) return null;
            return new DdlCommand(DdlKind.CreateTable, PreparedInsertExecutor.NormalizeTable(mc.Groups["table"].Value), cols);
        }
        var ma = AlterPattern.Match(sql);
        if (ma.Success)
        {
            var col = new ColumnDef(Clean(ma.Groups["col"].Value), NormalizeType(ma.Groups["type"].Value), false);
            return new DdlCommand(DdlKind.AlterTableAdd, PreparedInsertExecutor.NormalizeTable(ma.Groups["table"].Value),
                new[] { col });
        }
        var md = DropPattern.Match(sql);
        if (md.Success)
            return new DdlCommand(DdlKind.DropTable, PreparedInsertExecutor.NormalizeTable(md.Groups["table"].Value),
                System.Array.Empty<ColumnDef>());
        return null;
    }

    private static string Clean(string s) => s.Trim().Trim('[', ']', '"', '`', ' ');
    private static string NormalizeType(string t) => Regex.Replace(t, @"\s+", "").ToLowerInvariant();

    /// <summary>列定義リストを解析("col type [PRIMARY]" を , 区切り。型の () 内カンマは考慮)。</summary>
    private static List<ColumnDef> SplitColumns(string s)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '(') depth++;
            else if (s[i] == ')') depth--;
            else if (s[i] == ',' && depth == 0) { parts.Add(s[start..i]); start = i + 1; }
        }
        parts.Add(s[start..]);

        var cols = new List<ColumnDef>();
        foreach (var raw in parts)
        {
            var tokens = raw.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2) continue;
            var isPrimary = tokens.Any(t => t.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase));
            cols.Add(new ColumnDef(Clean(tokens[0]), NormalizeType(tokens[1]), isPrimary));
        }
        return cols;
    }

    /// <summary>DDL を実行する。戻り値は作成/追加した列数。</summary>
    public int Execute(DdlCommand cmd)
    {
        var prefix = PrefixOf(cmd.Table);
        if (prefix is null)
            throw new InvalidOperationException(
                $"DDL: 論理名 '{cmd.Table}' にパブリッシャー接頭辞(例 poc_)がありません。'prefix_name' 形式で指定してください。");

        return cmd.Kind switch
        {
            DdlKind.CreateTable => CreateTable(cmd, prefix),
            DdlKind.AlterTableAdd => AlterTableAdd(cmd, prefix),
            DdlKind.DropTable => DropTable(cmd),
            _ => throw new InvalidOperationException($"DDL: 未対応 {cmd.Kind}"),
        };
    }

    private int DropTable(DdlCommand cmd)
    {
        _connection.Client.Execute(new DeleteEntityRequest { LogicalName = cmd.Table.ToLowerInvariant() });
        _log.LogInformation("DDL: テーブル削除 {Table}", cmd.Table);
        return 1;
    }

    private int CreateTable(DdlCommand cmd, string prefix)
    {
        // 主名属性: PRIMARY 指定 → なければ最初の文字列列 → なければ最初の列
        var primary = cmd.Columns.FirstOrDefault(c => c.IsPrimary)
            ?? cmd.Columns.FirstOrDefault(c => IsStringType(c.SqlType))
            ?? cmd.Columns[0];
        if (!IsStringType(primary.SqlType))
            throw new InvalidOperationException($"DDL: 主名属性 '{primary.Name}' は文字列型である必要があります。");

        var entity = new EntityMetadata
        {
            SchemaName = cmd.Table,
            DisplayName = Label(cmd.Table),
            DisplayCollectionName = Label(cmd.Table),
            OwnershipType = OwnershipTypes.UserOwned,
        };
        var primaryAttr = new StringAttributeMetadata
        {
            SchemaName = primary.Name,
            MaxLength = 200,
            FormatName = StringFormatName.Text,
            RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None),
            DisplayName = Label(primary.Name),
        };
        _connection.Client.Execute(new CreateEntityRequest { Entity = entity, PrimaryAttribute = primaryAttr });
        _log.LogInformation("DDL: テーブル作成 {Table}(主名 {Primary})", cmd.Table, primary.Name);

        // 主名以外の列を属性として追加
        var added = 1;
        foreach (var c in cmd.Columns.Where(c => !c.Name.Equals(primary.Name, StringComparison.OrdinalIgnoreCase)))
        {
            _connection.Client.Execute(new CreateAttributeRequest { EntityName = cmd.Table.ToLowerInvariant(), Attribute = BuildAttribute(c) });
            added++;
        }
        return added;
    }

    private int AlterTableAdd(DdlCommand cmd, string prefix)
    {
        var c = cmd.Columns[0];
        _connection.Client.Execute(new CreateAttributeRequest { EntityName = cmd.Table.ToLowerInvariant(), Attribute = BuildAttribute(c) });
        _log.LogInformation("DDL: 列追加 {Table}.{Col} ({Type})", cmd.Table, c.Name, c.SqlType);
        return 1;
    }

    /// <summary>SQL 型 → Dataverse 属性メタデータ。安全な基本型のみ。</summary>
    private AttributeMetadata BuildAttribute(ColumnDef c)
    {
        var t = c.SqlType;
        AttributeMetadata a = t switch
        {
            _ when IsStringType(t) => new StringAttributeMetadata { MaxLength = 200, FormatName = StringFormatName.Text },
            "int" or "integer" => new IntegerAttributeMetadata(),
            "bigint" => new BigIntAttributeMetadata(),
            "decimal" or "numeric" => new DecimalAttributeMetadata { Precision = 2 },
            "money" or "currency" => new MoneyAttributeMetadata { Precision = 2 },
            "float" or "double" or "real" => new DoubleAttributeMetadata { Precision = 2 },
            "datetime" or "datetime2" or "date" => new DateTimeAttributeMetadata { Format = DateTimeFormat.DateAndTime },
            "bit" or "bool" or "boolean" => BuildBoolean(),
            _ => throw new InvalidOperationException($"DDL: 未対応の型 '{c.SqlType}'(列 {c.Name})。string/int/bigint/decimal/money/float/datetime/bit のみ対応。"),
        };
        a.SchemaName = c.Name;
        a.RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None);
        a.DisplayName = Label(c.Name);
        return a;
    }

    private BooleanAttributeMetadata BuildBoolean() => new()
    {
        OptionSet = new BooleanOptionSetMetadata(
            new OptionMetadata(Label("Yes"), 1), new OptionMetadata(Label("No"), 0)),
    };

    private static bool IsStringType(string t)
        => t.StartsWith("nvarchar") || t.StartsWith("varchar") || t.StartsWith("char")
        || t is "string" or "text" or "nvarchar" or "varchar";

    /// <summary>論理名の接頭辞(prefix_)を返す。無ければ null。</summary>
    private static string? PrefixOf(string table)
    {
        var i = table.IndexOf('_');
        return i > 0 ? table[..i] : null;
    }

    private Label Label(string text) => new(text, _lang.Value);

    private int GetBaseLanguage()
    {
        try
        {
            var org = _connection.Client.RetrieveMultiple(new QueryExpression("organization")
            { ColumnSet = new ColumnSet("languagecode") }).Entities.FirstOrDefault();
            return org?.GetAttributeValue<int>("languagecode") is int lc and > 0 ? lc : 1033;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "組織の基本言語取得に失敗。1033(en-US)を使用");
            return 1033;
        }
    }
}
