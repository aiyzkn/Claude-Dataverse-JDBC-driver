using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;

namespace Gateway.Dataverse;

/// <summary>
/// TRUNCATE TABLE / 条件付き DELETE を BulkDeleteRequest(サーバーサイド非同期ジョブ)へ変換する。
/// 要件定義書 3.3 / F-10。TRUNCATE は「全行 BulkDelete＋完了待ち」の糖衣構文。
/// 条件付き DELETE は WHERE を安全に解釈できる部分集合(AND 連結の単純比較)のみ対応し、
/// 解釈できない WHERE は「削除しない(null 返却)」で安全側に倒す。
/// </summary>
public sealed class BulkDeleteExecutor
{
    // テーブル名はスキーマ修飾(dbo.t)・角括弧・引用符を許容(JDBC クライアント対応)。NormalizeTable で logical name に戻す。
    private const string TableToken = @"(?<table>[\w.\[\]""]+)";

    private static readonly Regex TruncatePattern = new(
        @"^\s*TRUNCATE\s+TABLE\s+" + TableToken + @"\s*;?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // DELETE FROM <table> [WHERE <conditions>]
    private static readonly Regex DeletePattern = new(
        @"^\s*DELETE\s+FROM\s+" + TableToken + @"\s*(?:WHERE\s+(?<where>.+?))?\s*;?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    // 1 条件: col IS [NOT] NULL / col LIKE '..' / col <op> value(値は 'quoted' か非空白列)
    private static readonly Regex ConditionPattern = new(
        @"(?<col>\w+)\s*(?:(?<isnull>IS\s+(?:NOT\s+)?NULL)|(?<like>LIKE\s+'(?<likeval>(?:[^']|'')*)')|(?<op><>|!=|>=|<=|=|>|<)\s*(?<val>'(?:[^']|'')*'|[^\s]+))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly DataverseConnection _connection;
    private readonly EntityMetadataCache _metadata;
    private readonly ILogger<BulkDeleteExecutor> _log;
    private readonly ProductionGuard _guard;
    private readonly int _waitMinutes;
    private readonly int _syncDeleteMaxRows;

    // 標準テーブルの同期削除で 1 回の ExecuteMultiple に載せる件数(SDK 上限 1000)。
    private const int ExecuteMultipleChunk = 1000;
    // エラスティックテーブルの DeleteMultiple で 1 回に載せる件数(推奨 100)。
    private const int DeleteMultipleChunk = 100;

    public BulkDeleteExecutor(DataverseConnection connection, EntityMetadataCache metadata,
        IConfiguration config, ProductionGuard guard, ILogger<BulkDeleteExecutor> log)
    {
        _connection = connection;
        _metadata = metadata;
        _log = log;
        _guard = guard;
        // 完了待ちの上限(OBS-PERF-3)。100 万行級 BulkDelete は 10 分を超えるため既定 60 分。
        // 0 以下なら「発行のみ・完了を待たない」(非同期ジョブとして継続)。
        _waitMinutes = config.GetValue<int?>("Gateway:BulkDeleteWaitMinutes") ?? 60;
        // 4.2.3 高速削除: 標準テーブルでこの件数以下の削除は同期 ExecuteMultiple(即時)で実行し、
        // BulkDelete 非同期ジョブの生成・ポーリングのオーバーヘッドを避ける。超過は従来 BulkDelete。
        // RetrieveMultiple の 1 ページ上限 5000 に合わせ上限を 5000 に丸める。0 以下で無効(常に BulkDelete)。
        _syncDeleteMaxRows = Math.Min(config.GetValue<int?>("Gateway:SyncDeleteMaxRows") ?? 5000, 5000);
    }

    /// <summary>削除対象(テーブル＋任意の絞り込み条件)。条件が空なら全行。</summary>
    public sealed record DeleteCommand(string Table, FilterExpression Criteria, string Description);

    public static string? TryParseTruncate(string sql)
    {
        var m = TruncatePattern.Match(sql);
        return m.Success ? PreparedInsertExecutor.NormalizeTable(m.Groups["table"].Value) : null;
    }

    /// <summary>
    /// DELETE FROM &lt;table&gt; [WHERE ...] を解析する。WHERE 無しは全行削除。
    /// WHERE は AND 連結の単純比較(=,!=,&lt;&gt;,&gt;,&lt;,&gt;=,&lt;=,LIKE,IS [NOT] NULL)のみ対応。
    /// OR・括弧・関数など解釈できない構文が含まれる場合は null(＝非対応→削除しない)を返す。
    /// </summary>
    public static DeleteCommand? TryParseDelete(string sql)
    {
        var m = DeletePattern.Match(sql);
        if (!m.Success) return null;
        var table = PreparedInsertExecutor.NormalizeTable(m.Groups["table"].Value);

        if (!m.Groups["where"].Success)
            return new DeleteCommand(table, new FilterExpression(LogicalOperator.And), $"DELETE FROM {table} (全行)");

        var where = m.Groups["where"].Value.Trim();
        var filter = new FilterExpression(LogicalOperator.And);

        // 条件を順に抽出し、条件間の隙間が空白/AND のみであることを検証(未知構文の取りこぼし防止)
        var matches = ConditionPattern.Matches(where);
        if (matches.Count == 0) return null;
        var cursor = 0;
        foreach (Match c in matches)
        {
            if (!IsGapAndOrWhitespace(where.Substring(cursor, c.Index - cursor))) return null;
            var cond = BuildCondition(c);
            if (cond is null) return null;
            filter.Conditions.Add(cond);
            cursor = c.Index + c.Length;
        }
        if (!IsGapAndOrWhitespace(where.Substring(cursor))) return null; // 末尾の残り

        return new DeleteCommand(table, filter, $"DELETE FROM {table} WHERE {where}");
    }

    /// <summary>条件間の区切りが空白または AND だけか(OR や未知トークンが無いか)。</summary>
    private static bool IsGapAndOrWhitespace(string gap)
        => Regex.IsMatch(gap, @"^(\s*(AND)?\s*)*$", RegexOptions.IgnoreCase);

    private static ConditionExpression? BuildCondition(Match c)
    {
        var col = c.Groups["col"].Value.ToLowerInvariant();
        if (c.Groups["isnull"].Success)
        {
            var isNot = Regex.IsMatch(c.Groups["isnull"].Value, @"NOT", RegexOptions.IgnoreCase);
            return new ConditionExpression(col, isNot ? ConditionOperator.NotNull : ConditionOperator.Null);
        }
        if (c.Groups["like"].Success)
            return new ConditionExpression(col, ConditionOperator.Like, c.Groups["likeval"].Value.Replace("''", "'"));

        var op = c.Groups["op"].Value switch
        {
            "=" => ConditionOperator.Equal,
            "!=" or "<>" => ConditionOperator.NotEqual,
            ">" => ConditionOperator.GreaterThan,
            "<" => ConditionOperator.LessThan,
            ">=" => ConditionOperator.GreaterEqual,
            "<=" => ConditionOperator.LessEqual,
            _ => (ConditionOperator?)null,
        };
        if (op is null) return null;
        return new ConditionExpression(col, op.Value, ParseValue(c.Groups["val"].Value));
    }

    /// <summary>リテラル値を型付けする(文字列/GUID/整数/小数)。</summary>
    private static object ParseValue(string raw)
    {
        if (raw.Length >= 2 && raw[0] == '\'' && raw[^1] == '\'')
            return raw[1..^1].Replace("''", "'");
        if (Guid.TryParse(raw, out var g)) return g;
        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
        if (decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)) return d;
        return raw; // 無引用の文字列(true/false 等)はそのまま
    }

    /// <summary>削除経路の見積り表示(ドライラン用。4.2.3)。matchCount は WHERE 一致行数。</summary>
    public string DescribeRoute(string table, long matchCount)
    {
        if (_metadata.IsElastic(table)) return "DeleteMultiple(エラスティック・同期・即時)";
        if (_syncDeleteMaxRows > 0 && matchCount <= _syncDeleteMaxRows)
            return $"ExecuteMultiple(標準・同期・即時。{_syncDeleteMaxRows}件以下)";
        return "BulkDelete(標準・非同期ジョブ)";
    }

    /// <summary>全行を削除し、削除件数を返す(経路は自動選択。4.2.3)。</summary>
    public Task<long> TruncateAndWaitAsync(string table, CancellationToken ct, bool allowProduction = false)
        => RouteDeleteAsync(
            new DeleteCommand(table, new FilterExpression(LogicalOperator.And), $"TRUNCATE {table}"), ct, allowProduction);

    /// <summary>条件付き削除を実行し、削除件数を返す(経路は自動選択。4.2.3)。</summary>
    public Task<long> DeleteAndWaitAsync(DeleteCommand cmd, CancellationToken ct, bool allowProduction = false)
        => RouteDeleteAsync(cmd, ct, allowProduction);

    /// <summary>
    /// 削除経路を自動選択する(4.2.3)。
    ///   - エラスティックテーブル: <c>DeleteMultiple</c>(同期・即時・部分成功可)。
    ///   - 標準テーブルで対象が <c>Gateway:SyncDeleteMaxRows</c> 件以下: 同期 <c>ExecuteMultiple(DeleteRequest)</c>(即時)。
    ///   - それ以外(大量削除・件数不明): 従来の <c>BulkDelete</c> 非同期ジョブ＋完了待ち。
    /// </summary>
    private async Task<long> RouteDeleteAsync(DeleteCommand cmd, CancellationToken ct, bool allowProduction)
    {
        _guard.EnsureWriteAllowed("DELETE", cmd.Table, allowProduction); // 本番環境ガード(3.1.2/4.2.2b)

        // エラスティックテーブルは BulkDelete よりも DeleteMultiple が高速・即時(カスケード無し)。
        if (_metadata.IsElastic(cmd.Table))
            return await DeleteMultipleAsync(cmd, ct);

        // 標準テーブル: まず対象 ID を上限+1 件だけ引き当て、少量なら同期 ExecuteMultiple で即時削除。
        if (_syncDeleteMaxRows > 0)
        {
            var idAttr = _metadata.PrimaryAttributes(cmd.Table).IdAttribute;
            var (ids, more) = RetrieveIds(cmd.Table, cmd.Criteria, cap: _syncDeleteMaxRows);
            if (!more)
            {
                _log.LogInformation("{Desc}: 同期削除経路(件数={Count} ≤ {Max})。ExecuteMultiple で即時削除",
                    cmd.Description, ids.Count, _syncDeleteMaxRows);
                return await ExecuteMultipleDeleteAsync(cmd.Table, idAttr, ids, ct);
            }
            _log.LogInformation("{Desc}: 対象が {Max} 件超のため BulkDelete 非同期ジョブへフォールバック", cmd.Description, _syncDeleteMaxRows);
        }

        return await BulkDeleteAndWaitAsync(cmd, ct);
    }

    /// <summary>標準テーブルの少量削除を同期 ExecuteMultiple(DeleteRequest 束)で即時実行し、成功件数を返す。</summary>
    private async Task<long> ExecuteMultipleDeleteAsync(string table, string idAttr, List<Guid> ids, CancellationToken ct)
    {
        var client = _connection.Client;
        long deleted = 0;
        for (var i = 0; i < ids.Count; i += ExecuteMultipleChunk)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = ids.GetRange(i, Math.Min(ExecuteMultipleChunk, ids.Count - i));
            var req = new ExecuteMultipleRequest
            {
                Settings = new ExecuteMultipleSettings { ContinueOnError = true, ReturnResponses = false },
                Requests = new OrganizationRequestCollection(),
            };
            foreach (var id in chunk)
                req.Requests.Add(new DeleteRequest { Target = new EntityReference(table, id) });

            var resp = (ExecuteMultipleResponse)client.Execute(req);
            var failed = resp.Responses?.Count(r => r.Fault is not null) ?? 0;
            deleted += chunk.Count - failed;
            if (failed > 0)
                _log.LogWarning("同期削除: {Table} で {Failed}/{Total} 件が失敗", table, failed, chunk.Count);
            await Task.Yield();
        }
        _log.LogInformation("同期削除完了: {Table} {Count} 件", table, deleted);
        return deleted;
    }

    /// <summary>エラスティックテーブルを DeleteMultiple(同期・即時)でページごとに削除し、削除件数を返す。</summary>
    private async Task<long> DeleteMultipleAsync(DeleteCommand cmd, CancellationToken ct)
    {
        var client = _connection.Client;
        var idAttr = _metadata.PrimaryAttributes(cmd.Table).IdAttribute;
        long deleted = 0;
        // エラスティックは対象全件を引き当て、100 件ずつ DeleteMultiple(partitionid があればキーに含める)。
        var (rows, _) = RetrieveEntities(cmd.Table, cmd.Criteria, new ColumnSet(idAttr, "partitionid"));
        for (var i = 0; i < rows.Count; i += DeleteMultipleChunk)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = rows.GetRange(i, Math.Min(DeleteMultipleChunk, rows.Count - i));
            var targets = new EntityReferenceCollection();
            foreach (var e in chunk)
            {
                var partition = e.GetAttributeValue<string>("partitionid");
                if (!string.IsNullOrEmpty(partition))
                    targets.Add(new EntityReference(cmd.Table,
                        new KeyAttributeCollection { { idAttr, e.Id }, { "partitionid", partition } }));
                else
                    targets.Add(new EntityReference(cmd.Table, e.Id));
            }
            var req = new OrganizationRequest("DeleteMultiple") { ["Targets"] = targets };
            client.Execute(req);
            deleted += chunk.Count;
            await Task.Yield();
        }
        _log.LogInformation("{Desc}: DeleteMultiple(エラスティック) {Count} 件削除", cmd.Description, deleted);
        return deleted;
    }

    /// <summary>絞り込みに一致する ID を最大 cap 件まで取得する(More=さらに存在する=同期経路を諦める合図)。</summary>
    private (List<Guid> Ids, bool More) RetrieveIds(string table, FilterExpression criteria, int cap)
    {
        var query = new QueryExpression(table)
        {
            ColumnSet = new ColumnSet(false), // 主キーのみ(Entity.Id に入る)
            Criteria = criteria,
            PageInfo = new PagingInfo { PageNumber = 1, Count = Math.Min(cap, 5000) },
        };
        var ec = _connection.Client.RetrieveMultiple(query);
        return (ec.Entities.Select(e => e.Id).ToList(), ec.MoreRecords);
    }

    /// <summary>絞り込みに一致する全行を指定列で取得する(ページング。エラスティック削除用)。</summary>
    private (List<Entity> Rows, bool More) RetrieveEntities(string table, FilterExpression criteria, ColumnSet columns)
    {
        var all = new List<Entity>();
        var query = new QueryExpression(table)
        {
            ColumnSet = columns,
            Criteria = criteria,
            PageInfo = new PagingInfo { PageNumber = 1, Count = 5000 },
        };
        while (true)
        {
            var ec = _connection.Client.RetrieveMultiple(query);
            all.AddRange(ec.Entities);
            if (!ec.MoreRecords) break;
            query.PageInfo.PageNumber++;
            query.PageInfo.PagingCookie = ec.PagingCookie;
        }
        return (all, false);
    }

    private async Task<long> BulkDeleteAndWaitAsync(DeleteCommand cmd, CancellationToken ct)
    {
        var client = _connection.Client;
        var query = new QueryExpression(cmd.Table) { Criteria = cmd.Criteria };

        var request = new BulkDeleteRequest
        {
            JobName = $"{cmd.Description} ({DateTime.UtcNow:o})",
            QuerySet = new[] { query },
            StartDateTime = DateTime.Now,
            RecurrencePattern = string.Empty,
            SendEmailNotification = false,
            ToRecipients = System.Array.Empty<Guid>(),
            CCRecipients = System.Array.Empty<Guid>(),
        };

        var jobId = ((BulkDeleteResponse)client.Execute(request)).JobId;
        _log.LogInformation("{Desc}: BulkDelete ジョブ発行 JobId={JobId}", cmd.Description, jobId);

        // 完了を待たない設定(0 以下)の場合は発行のみでジョブ ID 起点の継続に委ねる
        if (_waitMinutes <= 0)
        {
            _log.LogInformation("{Desc}: 完了待ち無効(BulkDeleteWaitMinutes<=0)。JobId={JobId} で継続", cmd.Description, jobId);
            return -1;
        }

        // asyncoperation の完了(statecode=3)を待つ。上限は設定可能(OBS-PERF-3)。
        var deadline = DateTime.UtcNow.AddMinutes(_waitMinutes);
        var delay = TimeSpan.FromSeconds(2);
        var nextProgressLog = DateTime.UtcNow.AddMinutes(1);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var op = client.Retrieve("asyncoperation", jobId, new ColumnSet("statecode", "statuscode", "message"));
            var state = op.GetAttributeValue<OptionSetValue>("statecode")?.Value ?? -1;
            if (state == 3) // Completed
            {
                var msg = op.GetAttributeValue<string>("message");
                if (!string.IsNullOrEmpty(msg)) _log.LogWarning("BulkDelete 完了(メッセージ有): {Msg}", msg);
                break;
            }
            if (DateTime.UtcNow > deadline)
            {
                var partial = TryGetSuccessCount(client, jobId);
                _log.LogWarning("{Desc}: 完了待ちタイムアウト({Min}分)。ジョブは継続中。JobId={JobId} 削除済(暫定)={Partial}",
                    cmd.Description, _waitMinutes, jobId, partial);
                return -1;
            }
            // 進捗ログ(1 分間隔)。削除済件数を非同期ジョブ操作から取得
            if (DateTime.UtcNow >= nextProgressLog)
            {
                _log.LogInformation("{Desc}: BulkDelete 進行中 JobId={JobId} 削除済(暫定)={Count}",
                    cmd.Description, jobId, TryGetSuccessCount(client, jobId));
                nextProgressLog = DateTime.UtcNow.AddMinutes(1);
            }
            await Task.Delay(delay, ct);
            if (delay < TimeSpan.FromSeconds(30)) delay += TimeSpan.FromSeconds(2);
        }

        var success = TryGetSuccessCount(client, jobId);
        _log.LogInformation("{Desc}: {Count} 件削除", cmd.Description, success);
        return success;
    }

    /// <summary>BulkDelete 操作の削除済件数(successcount)を取得する。未取得時は 0。</summary>
    private static long TryGetSuccessCount(ServiceClient client, Guid jobId)
    {
        try
        {
            var ops = client.RetrieveMultiple(new QueryExpression("bulkdeleteoperation")
            {
                ColumnSet = new ColumnSet("successcount", "failurecount"),
                Criteria = new FilterExpression(LogicalOperator.And)
                {
                    Conditions = { new ConditionExpression("asyncoperationid", ConditionOperator.Equal, jobId) },
                },
            });
            return ops.Entities.FirstOrDefault()?.GetAttributeValue<int>("successcount") ?? 0;
        }
        catch
        {
            return 0;
        }
    }
}
