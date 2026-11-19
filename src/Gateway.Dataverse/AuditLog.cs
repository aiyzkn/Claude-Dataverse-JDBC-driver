using System.Text;
using Microsoft.Extensions.Logging;

namespace Gateway.Dataverse;

/// <summary>1 書き込みジョブの監査記録(NFR 監査: 対象・件数・経路・所要・オプション)。</summary>
public sealed record AuditRecord(
    DateTime StartUtc,
    DateTime EndUtc,
    string Table,
    string Operation,
    string Route,
    long TotalRows,
    long Written,
    long Skipped,
    long Failed,
    string Options,
    string? FailureCsv)
{
    public double DurationSec => Math.Round((EndUtc - StartUtc).TotalSeconds, 1);
    public double RowsPerSec => DurationSec > 0 ? Math.Round(Written / DurationSec, 0) : 0;
}

/// <summary>
/// 全書き込みジョブの監査ログ(NFR)。CSV へ追記(監査証跡)し、ジョブごとに Markdown 要約を出力する。
/// 実施報告に転用可能な形式。バイパス等の使用オプションも記録する。
/// </summary>
public sealed class AuditLog
{
    private readonly string _directory;
    private readonly ILogger<AuditLog> _log;
    private readonly object _gate = new();

    public AuditLog(string directory, ILogger<AuditLog> log)
    {
        _directory = directory;
        _log = log;
        Directory.CreateDirectory(_directory);
    }

    public void Record(AuditRecord r, string jobLabel)
    {
        lock (_gate)
        {
            AppendCsv(r);
            var md = WriteMarkdown(r, jobLabel);
            _log.LogInformation(
                "監査: {Table} {Op}({Route}) 総{Total}/新規{Written}/スキップ{Skipped}/失敗{Failed} {Sec}s → {Md}",
                r.Table, r.Operation, r.Route, r.TotalRows, r.Written, r.Skipped, r.Failed, r.DurationSec, md);
        }
    }

    private void AppendCsv(AuditRecord r)
    {
        var path = Path.Combine(_directory, $"audit_{r.StartUtc:yyyyMMdd}.csv");
        var newFile = !File.Exists(path);
        var sb = new StringBuilder();
        if (newFile) sb.AppendLine("start_utc,end_utc,duration_sec,table,operation,route,total,written,skipped,failed,rows_per_sec,options,failure_csv");
        sb.AppendLine(string.Join(",",
            r.StartUtc.ToString("o"), r.EndUtc.ToString("o"), r.DurationSec,
            Csv(r.Table), Csv(r.Operation), Csv(r.Route),
            r.TotalRows, r.Written, r.Skipped, r.Failed, r.RowsPerSec,
            Csv(r.Options), Csv(r.FailureCsv ?? "")));
        File.AppendAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    private string WriteMarkdown(AuditRecord r, string jobLabel)
    {
        var path = Path.Combine(_directory, $"job_{SafeName.ForFile(jobLabel)}.md"); // L-2: 外部由来ラベルを無害化
        var sb = new StringBuilder();
        sb.AppendLine($"# 移行ジョブ実施報告: {r.Table}");
        sb.AppendLine();
        sb.AppendLine("| 項目 | 内容 |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| 対象テーブル | {r.Table} |");
        sb.AppendLine($"| 操作 | {r.Operation} |");
        sb.AppendLine($"| 経路 | {r.Route} |");
        sb.AppendLine($"| 開始(UTC) | {r.StartUtc:o} |");
        sb.AppendLine($"| 終了(UTC) | {r.EndUtc:o} |");
        sb.AppendLine($"| 所要 | {r.DurationSec} 秒 |");
        sb.AppendLine($"| 総行数 | {r.TotalRows} |");
        sb.AppendLine($"| 新規書き込み | {r.Written} |");
        sb.AppendLine($"| スキップ(再開/追跡) | {r.Skipped} |");
        sb.AppendLine($"| 失敗 | {r.Failed} |");
        sb.AppendLine($"| スループット | {r.RowsPerSec} rows/sec |");
        sb.AppendLine($"| 使用オプション | {r.Options} |");
        if (!string.IsNullOrEmpty(r.FailureCsv))
            sb.AppendLine($"| 失敗行レポート | {r.FailureCsv} |");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        return path;
    }

    private static string Csv(string s)
    {
        var v = s.ReplaceLineEndings(" ");
        // CSV フォーミュラインジェクション対策: 数式記号で始まる値は先頭に ' を付与して無害化
        // (移行元データ由来の値や Dataverse エラー文が Excel で数式評価されるのを防ぐ)。
        if (v.Length > 0 && v[0] is '=' or '+' or '-' or '@' or '\t')
            v = "'" + v;
        return v.Contains(',') || v.Contains('"') ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }
}
