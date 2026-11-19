using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Gateway.Dataverse;

/// <summary>
/// 実行前ドライラン(3.1.1)のレポート。書き込みを実行せず、件数・経路・推定 API 消費・
/// 推定所要時間・必要特権を事前提示する。
/// </summary>
public sealed record DryRunReport(
    string Operation,
    string Table,
    long RowCount,
    string Route,
    long EstimatedApiRequests,
    double EstimatedSeconds,
    IReadOnlyList<string> RequiredPrivileges,
    string Options,
    IReadOnlyList<string> Notes)
{
    /// <summary>人間可読の Markdown 断片(ログ・レポートファイル用)。</summary>
    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# ドライラン: {Operation} {Table}");
        sb.AppendLine($"- 対象行数: {RowCount:N0}");
        sb.AppendLine($"- 経路: {Route}");
        sb.AppendLine($"- 推定 API 要求数(サーバ往復): {EstimatedApiRequests:N0}");
        sb.AppendLine($"- 推定所要時間: {FormatDuration(EstimatedSeconds)}");
        sb.AppendLine($"- 必要特権: {(RequiredPrivileges.Count == 0 ? "(なし)" : string.Join(" / ", RequiredPrivileges))}");
        sb.AppendLine($"- オプション: {Options}");
        foreach (var n in Notes) sb.AppendLine($"- 注記: {n}");
        return sb.ToString();
    }

    private static string FormatDuration(double seconds)
    {
        if (seconds < 60) return $"約 {seconds:F0} 秒";
        if (seconds < 3600) return $"約 {seconds / 60:F1} 分";
        return $"約 {seconds / 3600:F1} 時間";
    }
}

/// <summary>
/// ドライランレポートを組み立てる(3.1.1)。経路は BulkCapabilityCache で事前判定し、
/// 推定レートは設定 `Gateway:DryRun:*Rate`(既定は Phase 1 実測値)で調整可能。
/// </summary>
public sealed class DryRunAnalyzer
{
    private readonly ILogger<DryRunAnalyzer> _log;
    private readonly int _batchSize;
    private readonly double _bulkRate, _emRate, _assocRate, _deleteRate;
    private const int EmAssocChunk = 1000; // AssociateExecutor のバッチ単位

    public DryRunAnalyzer(IConfiguration config, ILogger<DryRunAnalyzer> log)
    {
        _log = log;
        _batchSize = config.GetValue<int?>("Gateway:BatchSize") ?? 200;
        // 既定レート(rows/sec)は Phase 1 実測に基づく: バルク~280 / EM~65 / N:N~15 / 削除~100。
        _bulkRate = config.GetValue<double?>("Gateway:DryRun:BulkRate") ?? 280;
        _emRate = config.GetValue<double?>("Gateway:DryRun:EmRate") ?? 65;
        _assocRate = config.GetValue<double?>("Gateway:DryRun:AssocRate") ?? 15;
        _deleteRate = config.GetValue<double?>("Gateway:DryRun:DeleteRate") ?? 100;
    }

    /// <summary>INSERT/UPSERT のドライラン。route は事前判定済み経路(PreparedInsertExecutor.RouteFor)。</summary>
    public DryRunReport AnalyzeWrite(PreparedInsertExecutor.WriteCommand cmd, long rowCount, string route)
    {
        var notes = new List<string>();
        var isBulk = route.StartsWith("CreateMultiple", StringComparison.OrdinalIgnoreCase);
        var rate = isBulk ? _bulkRate : _emRate;
        if (cmd.Kind == PreparedInsertExecutor.WriteKind.Upsert)
            notes.Add("UPSERT は UpsertMultiple 非対応のため EM+UpsertRequest 経路(OBS-3)。");
        else if (!isBulk)
        {
            if (cmd.Options.ForceExecuteMultiple) notes.Add("NO_BULK 指定によりバルクを使わず EM 経路。");
            else notes.Add("当テーブルは CreateMultiple 非対応のため EM 経路。");
        }

        var api = CeilDiv(rowCount, _batchSize);
        var privs = new List<string>();
        privs.Add(cmd.Kind == PreparedInsertExecutor.WriteKind.Upsert
            ? $"Create + Write ({cmd.Table})" : $"Create ({cmd.Table})");
        AddColumnAndOptionPrivileges(cmd, privs, notes);

        return new DryRunReport(cmd.Kind.ToString(), cmd.Table, rowCount, route,
            api, rowCount / Math.Max(1e-9, rate), privs, PreparedInsertExecutor.DescribeOptions(cmd.Options), notes);
    }

    /// <summary>DELETE/TRUNCATE のドライラン。matchCount は WHERE に一致する行数(TRUNCATE は全行)。
    /// route は実行時に選択される削除経路(4.2.3。<see cref="BulkDeleteExecutor.DescribeRoute"/>)。</summary>
    public DryRunReport AnalyzeDelete(string table, long matchCount, bool isTruncate, string? route = null)
    {
        route ??= "BulkDelete(標準・非同期ジョブ)";
        var isAsync = route.StartsWith("BulkDelete", StringComparison.OrdinalIgnoreCase);
        var notes = new List<string>
        {
            isAsync
                ? "BulkDelete は非同期ジョブ。発行は即時、削除完了はサーバ側で進行(進捗はログ/監査で確認)。"
                : "同期経路(4.2.3)。対象 ID を引き当ててバッチ削除し、削除完了まで待って件数を返す。",
        };
        // 同期経路の API 要求数: 標準 ExecuteMultiple は 1000 件/回、エラスティック DeleteMultiple は 100 件/回。
        var isElastic = route.StartsWith("DeleteMultiple", StringComparison.OrdinalIgnoreCase);
        var chunk = isElastic ? 100 : 1000;
        var apiRequests = isAsync ? 1 : Math.Max(1, (int)Math.Ceiling(matchCount / (double)chunk)) + 1; // +1=ID 引き当て
        var privileges = isAsync
            ? new[] { $"Delete ({table})", "BulkDelete (prvBulkDelete)" }
            : new[] { $"Delete ({table})" };
        var op = isTruncate ? "Truncate" : "Delete";
        return new DryRunReport(op, table, matchCount, route,
            EstimatedApiRequests: apiRequests,
            EstimatedSeconds: matchCount / Math.Max(1e-9, _deleteRate),
            RequiredPrivileges: privileges,
            Options: "(なし)", Notes: notes);
    }

    /// <summary>N:N 関連付けのドライラン。rowCount は関連付けペア数。</summary>
    public DryRunReport AnalyzeAssociate(AssociateExecutor.AssociateCommand cmd, long rowCount)
    {
        var notes = new List<string>
        {
            "N:N はサービス保護のスライディングウィンドウでバースト⇄throttle。持続レートは低め(~15/s)。",
        };
        return new DryRunReport(cmd.Verb.ToString(), cmd.Relationship, rowCount, "Associate(EM)",
            EstimatedApiRequests: CeilDiv(rowCount, EmAssocChunk),
            EstimatedSeconds: rowCount / Math.Max(1e-9, _assocRate),
            RequiredPrivileges: new[] { $"AppendTo / Append ({cmd.Relationship} の両端エンティティ)" },
            Options: "(なし)", Notes: notes);
    }

    private static void AddColumnAndOptionPrivileges(
        PreparedInsertExecutor.WriteCommand cmd, List<string> privs, List<string> notes)
    {
        if (cmd.Columns.Any(c => c.Equals("createdon", StringComparison.OrdinalIgnoreCase)
                              || c.Equals("createdby", StringComparison.OrdinalIgnoreCase)))
            privs.Add(PrivilegeChecker.OverrideCreatedByPrivilege + " (監査列上書き)");
        if (cmd.Options.ImpersonateUserId is not null)
        {
            privs.Add("prvActOnBehalfOfAnotherUser (なりすまし)");
            notes.Add("IMPERSONATE 指定: createdby は指定ユーザーで記録される。");
        }
    }

    private static long CeilDiv(long n, long d) => d <= 0 ? 0 : (n + d - 1) / d;

    public void Emit(DryRunReport report, string reportDir)
    {
        _log.LogInformation("ドライラン結果:\n{Report}", report.ToText());
        try
        {
            Directory.CreateDirectory(reportDir);
            var path = Path.Combine(reportDir, $"dryrun_{SafeName.ForFile(report.Table)}.md"); // L-2: 外部由来テーブル名を無害化
            File.WriteAllText(path, report.ToText(), new UTF8Encoding(true));
            _log.LogInformation("ドライランレポート: {Path}", path);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "ドライランレポートのファイル出力に失敗");
        }
    }
}
