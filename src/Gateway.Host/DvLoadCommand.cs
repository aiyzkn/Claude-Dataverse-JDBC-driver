using System.Diagnostics;
using System.Text;
using Apache.Arrow;
using Apache.Arrow.Types;
using Gateway.Dataverse;
using Microsoft.Extensions.Logging;

namespace Gateway.Host;

/// <summary>
/// CSV/Excel 直接ローダー CLI(3.1.1 の下流。dvload サブコマンド)。
/// ファイルを読み、ゲートウェイの書き込みパイプライン(PreparedInsertExecutor)へ流す。
/// バルク/EM 経路・チェックポイント・失敗行レポート・本番ガード・ドライランをそのまま再利用する。
///
/// 使い方:
///   dvload --file data.csv --table poc_gadget [--mode insert|upsert] [--key col[,col]] [--id col]
///          [--batch 200] [--delimiter ,] [--dry-run] [--allow-production]
///          [--bypass-plugins] [--bypass-flows] [--suppress-duplicate-detection] [--no-bulk]
///          [--impersonate <guid>] [--job-id <id>]
/// </summary>
public static class DvLoadCommand
{
    public sealed record Options(
        string File, string Table, PreparedInsertExecutor.WriteKind Kind,
        IReadOnlyList<string> KeyColumns, string? IdColumn, int BatchSize, char Delimiter,
        bool DryRun, bool AllowProduction, PreparedInsertExecutor.WriteOptions Write);

    public static Options Parse(string[] args)
    {
        var m = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) continue;
            var key = args[i][2..];
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) { m[key] = args[++i]; }
            else flags.Add(key);
        }

        string Req(string k) => m.TryGetValue(k, out var v) ? v
            : throw new ArgumentException($"dvload: --{k} は必須です");

        var mode = m.GetValueOrDefault("mode", "insert").ToLowerInvariant();
        var kind = mode == "upsert" ? PreparedInsertExecutor.WriteKind.Upsert : PreparedInsertExecutor.WriteKind.Insert;
        var keys = m.TryGetValue("key", out var k2)
            ? k2.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : System.Array.Empty<string>();
        var delim = m.TryGetValue("delimiter", out var d) && d.Length > 0 ? d[0] : ',';

        var write = PreparedInsertExecutor.WriteOptions.None with
        {
            BypassSyncPlugins = flags.Contains("bypass-plugins"),
            BypassFlows = flags.Contains("bypass-flows"),
            SuppressDuplicateDetection = flags.Contains("suppress-duplicate-detection"),
            ForceExecuteMultiple = flags.Contains("no-bulk"),
            ResolveNames = flags.Contains("resolve-names"),
            ImpersonateUserId = m.TryGetValue("impersonate", out var imp) && Guid.TryParse(imp, out var g) ? g : null,
            JobId = m.GetValueOrDefault("job-id"),
        };

        return new Options(Req("file"), Req("table"), kind, keys, m.GetValueOrDefault("id"),
            int.TryParse(m.GetValueOrDefault("batch"), out var b) ? b : 200, delim,
            flags.Contains("dry-run"), flags.Contains("allow-production"), write);
    }

    public static async Task<int> RunAsync(Options o, PreparedInsertExecutor executor, DryRunAnalyzer dryRun,
        ProductionGuard guard, AuditLog audit, ProgressTracker progress, string reportDir, string dryRunDir, ILogger log, CancellationToken ct,
        int reportRetentionDays = 0, long maxInputBytes = 0)
    {
        if (!File.Exists(o.File)) { log.LogError("dvload: ファイルが見つかりません: {File}", o.File); return 2; }
        // 本番書き込み許可は ExecuteBatchAsync に渡す(4.2.2b でガードはセッション許可を引数化)

        var (header, rows) = ReadFile(o.File, o.Delimiter, maxInputBytes);
        log.LogInformation("dvload: {File} 読込 {Rows} 行 × {Cols} 列 → {Table} ({Mode})",
            o.File, rows.Count, header.Count, o.Table, o.Kind);

        // UPSERT の識別子検証(ON KEY 列 or 主キー列が列一覧にあること)
        if (o.Kind == PreparedInsertExecutor.WriteKind.Upsert && o.KeyColumns.Count == 0 && o.IdColumn is null)
        {
            log.LogError("dvload: UPSERT には --key <代替キー列> か --id <主キー列> が必要です");
            return 2;
        }
        var cmd = new PreparedInsertExecutor.WriteCommand(o.Kind, o.Table, header, o.KeyColumns, o.IdColumn, o.Write);

        // ドライラン: 実行せず件数・API・時間・特権を提示
        if (o.DryRun)
        {
            dryRun.Emit(dryRun.AnalyzeWrite(cmd, rows.Count, executor.RouteFor(cmd)), dryRunDir);
            log.LogInformation("dvload: ドライランのため実行しませんでした(--dry-run)");
            return 0;
        }

        var sw = Stopwatch.StartNew();
        var startUtc = DateTime.UtcNow;
        var failures = new FailureReport();
        Gateway.Checkpoint.CheckpointSession? session = null;
        long total = 0, offset = 0;
        // 進捗トラッキング(3.1.5)。総数既知のため残時間(ETA)を算出できる。
        var progressId = progress.Begin(o.Table, o.Kind.ToString(), rows.Count);
        try
        {
            for (var i = 0; i < rows.Count; i += o.BatchSize)
            {
                var chunk = rows.GetRange(i, Math.Min(o.BatchSize, rows.Count - i));
                using var batch = BuildBatch(header, chunk);
                session ??= executor.BeginSession(cmd, batch);
                total += await executor.ExecuteBatchAsync(cmd, batch, ct, session, failures, offset, o.AllowProduction);
                offset += chunk.Count;
                progress.Report(progressId, offset);
                var rate = offset / Math.Max(0.001, sw.Elapsed.TotalSeconds);
                var etaSec = rate > 0 ? (rows.Count - offset) / rate : 0;
                log.LogInformation("dvload: {Done}/{Total} 行 ({Pct}%, {Rate:F0} 行/秒, 残り約 {Eta:F0} 秒)",
                    offset, rows.Count, rows.Count > 0 ? 100 * offset / rows.Count : 100, rate, etaSec);
            }
            session?.Complete();
            progress.Complete(progressId);
        }
        catch (OperationCanceledException)
        {
            // 安全なキャンセル(3.1.5): チェックポイントに未完了分が残り、再投入で安全に再開できる。
            progress.Complete(progressId, "cancelled");
            log.LogWarning("dvload: キャンセルされました({Done}行処理済)。再投入で未完了分から再開可能", offset);
            throw;
        }

        string? failureCsv = null;
        if (failures.Count > 0)
        {
            failureCsv = failures.WriteCsv(reportDir, $"dvload_{o.Table}", reportRetentionDays);
            log.LogWarning("dvload: {Failed} 行が失敗。レポート: {Csv}", failures.Count, failureCsv);
        }
        audit.Record(new AuditRecord(startUtc, DateTime.UtcNow, o.Table, o.Kind.ToString(), executor.RouteFor(cmd),
            TotalRows: rows.Count, Written: total, Skipped: rows.Count - total - failures.Count,
            Failed: failures.Count, Options: PreparedInsertExecutor.DescribeOptions(o.Write), FailureCsv: failureCsv),
            $"dvload_{o.Table}");
        log.LogInformation("dvload 完了: 総 {Total} / 新規 {Written} / 失敗 {Failed} / {Sec:F1}秒",
            rows.Count, total, failures.Count, sw.Elapsed.TotalSeconds);
        return failures.Count > 0 ? 1 : 0;
    }

    /// <summary>
    /// 拡張子で CSV/xlsx を振り分けて (ヘッダ, 行) を返す。
    /// M-3: maxInputBytes>0 のとき、読み込み前にファイルサイズを検査し上限超過は明示エラー
    /// (全読み込みによる OOM を、突然のクラッシュではなく実行前の分かりやすい失敗に変える)。0=無制限。
    /// </summary>
    public static (List<string> Header, List<string?[]> Rows) ReadFile(string path, char delimiter, long maxInputBytes = 0)
    {
        if (maxInputBytes > 0)
        {
            var size = new FileInfo(path).Length;
            if (size > maxInputBytes)
                throw new InvalidOperationException(
                    $"dvload: 入力ファイルが上限を超えています({size / (1024 * 1024)}MB > {maxInputBytes / (1024 * 1024)}MB)。" +
                    "Gateway:MaxInputFileMB を上げるか、ファイルを分割してください。");
        }
        return Path.GetExtension(path).ToLowerInvariant() == ".xlsx"
            ? XlsxReader.Read(path, maxInputBytes) // M-3: 展開後サイズも同じ上限で検査(展開ボム対策)
            : CsvParser.Read(File.ReadAllText(path, Encoding.UTF8), delimiter);
    }

    /// <summary>文字列行から Arrow RecordBatch(全列 string)を構築する。型変換は executor 側で属性メタに基づき実施。</summary>
    private static RecordBatch BuildBatch(IReadOnlyList<string> cols, List<string?[]> rows)
    {
        var arrays = new IArrowArray[cols.Count];
        var schema = new Schema.Builder();
        for (var c = 0; c < cols.Count; c++)
        {
            var b = new StringArray.Builder();
            foreach (var row in rows)
            {
                var v = row[c];
                if (v is null) b.AppendNull(); else b.Append(v);
            }
            arrays[c] = b.Build();
            schema.Field(new Field(cols[c], StringType.Default, nullable: true));
        }
        return new RecordBatch(schema.Build(), arrays, rows.Count);
    }
}
