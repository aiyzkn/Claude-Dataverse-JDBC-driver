using System.Diagnostics;
using Apache.Arrow;
using Apache.Arrow.Flight;
using Apache.Arrow.Flight.Server;
using Gateway.Dataverse;
using Gateway.Flight.Protocol.Sql;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace Gateway.Flight;

/// <summary>
/// Arrow Flight SQL のサーバー実装(製品版。PoC の FlightSqlGatewayService を移植)。
///  - SELECT(アドホック/プリペアド)→ GetFlightInfo/DoGet
///  - INSERT/UPSERT(executeBatch)→ DoPut(CommandPreparedStatementUpdate)
///  - メタデータ(SqlInfo/Catalogs/DbSchemas/TableTypes/Tables)
/// 型ロスレス(2.1.4)・全件メタデータ・経路事前判定(2.2.1)は後続で拡張。
/// </summary>
public class FlightSqlService : FlightServer
{
    private const int RowsPerBatch = 4096;

    private readonly ILogger<FlightSqlService> _log;
    private readonly StatementRegistry _registry;
    private readonly Gateway.Dataverse.AuditLog _audit;
    private readonly Gateway.Dataverse.SessionRegistry _sessions;
    private readonly Gateway.Dataverse.DryRunAnalyzer _dryRun;
    private readonly Gateway.Dataverse.ProgressTracker _progress;
    private readonly Gateway.Dataverse.DataverseContextRegistry _contexts;
    private readonly int _reportRetentionDays; // M-2: 失敗行レポートの保持期間(日。0=無制限)

    // リクエストごとの接続コンテキスト(4.2.2 B2)。RPC 入口で設定し、以降の呼び出しに AsyncLocal で伝播する。
    private readonly AsyncLocal<Gateway.Dataverse.DataverseContext?> _current = new();
    private Gateway.Dataverse.DataverseContext Current => _current.Value
        ?? throw new RpcException(new Status(StatusCode.FailedPrecondition,
            "接続先が指定されていません。起動時 DATAVERSE_URL を設定するか、接続ヘッダ(x-dataverse-url 等)を送ってください。"));

    // 現在コンテキストのエグゼキュータ/接続/ガード(既存の使用箇所はそのまま動く)
    private DataverseConnection _connection => Current.Connection;
    private PreparedInsertExecutor _inserter => Current.Inserter;
    private BulkDeleteExecutor _bulkDelete => Current.BulkDelete;
    private AssociateExecutor _associate => Current.Associate;
    private Gateway.Dataverse.DdlExecutor _ddl => Current.Ddl;
    private Gateway.Dataverse.ProductionGuard _guard => Current.Guard;
    private Gateway.Dataverse.NameResolver _nameResolver => Current.NameResolver;

    // ナビゲーターに見せる検証用テーブル(全件列挙は F-13)
    private static readonly string[] PocTables = { "poc_gadget", "poc_widget", "account", "contact" };

    public FlightSqlService(
        ILogger<FlightSqlService> log,
        StatementRegistry registry,
        Gateway.Dataverse.AuditLog audit,
        Gateway.Dataverse.SessionRegistry sessions,
        Gateway.Dataverse.DryRunAnalyzer dryRun,
        Gateway.Dataverse.ProgressTracker progress,
        Gateway.Dataverse.DataverseContextRegistry contexts,
        Microsoft.Extensions.Configuration.IConfiguration config)
    {
        _log = log;
        _registry = registry;
        _audit = audit;
        _sessions = sessions;
        _dryRun = dryRun;
        _progress = progress;
        _contexts = contexts;
        _reportRetentionDays = Microsoft.Extensions.Configuration.ConfigurationBinder
            .GetValue<int?>(config, "Gateway:ReportRetentionDays") ?? 0;
    }

    // 接続ヘッダから接続先を解決して現在コンテキストを設定(4.2.2 B2)。ヘッダ無しは起動時の既定コンテキスト。
    private void SetCurrentContext(ServerCallContext context)
    {
        var p = ReadConnParams(context.RequestHeaders);
        _current.Value = p is not null ? _contexts.Get(p) : _contexts.Default;
    }

    private static string? Header(Metadata h, string key) => h.FirstOrDefault(e => e.Key == key)?.Value;

    private static Gateway.Dataverse.DataverseContextRegistry.ConnParams? ReadConnParams(Metadata headers)
    {
        var url = Header(headers, "x-dataverse-url");
        if (string.IsNullOrEmpty(url)) return null;
        var authStr = Header(headers, "x-dataverse-auth") ?? "ClientSecret";
        var auth = System.Enum.TryParse<Gateway.Core.AuthKind>(authStr, ignoreCase: true, out var k)
            ? k : Gateway.Core.AuthKind.ClientSecret;
        return new Gateway.Dataverse.DataverseContextRegistry.ConnParams(url, auth,
            Header(headers, "x-dataverse-tenant-id"), Header(headers, "x-dataverse-client-id"),
            Header(headers, "x-dataverse-client-secret"), Header(headers, "x-dataverse-cert-thumbprint"));
    }

    // 名前→ID 辞書の明示リフレッシュ(4.1.3): `SET REFRESH_NAME_CACHE [= <entity>]`
    private static readonly System.Text.RegularExpressions.Regex RefreshNameCachePattern =
        new(@"^\s*SET\s+REFRESH_NAME_CACHE\s*(=\s*(?<entity>[\w]+)\s*)?;?\s*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>`SET REFRESH_NAME_CACHE` を処理して名前辞書を無効化する。該当時 true。</summary>
    private bool TryRefreshNameCache(string sql)
    {
        var m = RefreshNameCachePattern.Match(sql);
        if (!m.Success) return false;
        _nameResolver.Invalidate(m.Groups["entity"].Success ? m.Groups["entity"].Value : null);
        return true;
    }

    private static readonly string DryRunDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DataverseGW", "dryrun");

    /// <summary>DELETE/TRUNCATE の対象件数を COUNT で取得(ドライラン用。失敗時 -1)。</summary>
    private long DryRunCount(string countSql)
    {
        try
        {
            using var con = _connection.CreateSqlConnection();
            using var cmd = con.CreateCommand();
            cmd.CommandText = countSql;
            cmd.CommandTimeout = 120;
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "ドライラン件数取得に失敗: {Sql}", Truncate(countSql));
            return -1;
        }
    }

    /// <summary>DELETE 文を COUNT(*) クエリへ変換する(先頭 DELETE FROM → SELECT COUNT(*) FROM)。</summary>
    private static string DeleteToCountSql(string sql)
        => System.Text.RegularExpressions.Regex.Replace(
            sql.TrimEnd().TrimEnd(';'), @"^\s*DELETE\s+FROM", "SELECT COUNT(*) FROM",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>DELETE/TRUNCATE のドライラン(クエリ経路用): レポート出力して 0 を返す。</summary>
    private long DryRunDelete(string table, long count, bool isTruncate)
    {
        // 4.2.3: 実行時に選択される削除経路(エラスティック DeleteMultiple / 標準同期 / BulkDelete)を反映
        _dryRun.Emit(_dryRun.AnalyzeDelete(table, count, isTruncate, _bulkDelete.DescribeRoute(table, count)), DryRunDir);
        return 0;
    }

    /// <summary>進捗スナップショットを Arrow の結果セットに変換する(gateway_progress。3.1.5)。</summary>
    private static (Schema Schema, RecordBatch Batch) BuildProgressBatch(IReadOnlyList<Gateway.Dataverse.ProgressTracker.Entry> entries)
    {
        var schema = new Schema.Builder()
            .Field(new Apache.Arrow.Field("id", Apache.Arrow.Types.StringType.Default, false))
            .Field(new Apache.Arrow.Field("table", Apache.Arrow.Types.StringType.Default, false))
            .Field(new Apache.Arrow.Field("operation", Apache.Arrow.Types.StringType.Default, false))
            .Field(new Apache.Arrow.Field("rows_done", Apache.Arrow.Types.Int64Type.Default, false))
            .Field(new Apache.Arrow.Field("rows_total", Apache.Arrow.Types.Int64Type.Default, true))
            .Field(new Apache.Arrow.Field("rows_per_sec", Apache.Arrow.Types.DoubleType.Default, false))
            .Field(new Apache.Arrow.Field("elapsed_sec", Apache.Arrow.Types.DoubleType.Default, false))
            .Field(new Apache.Arrow.Field("eta_sec", Apache.Arrow.Types.DoubleType.Default, true))
            .Field(new Apache.Arrow.Field("percent", Apache.Arrow.Types.Int32Type.Default, true))
            .Field(new Apache.Arrow.Field("status", Apache.Arrow.Types.StringType.Default, false))
            .Build();

        var id = new StringArray.Builder(); var table = new StringArray.Builder(); var op = new StringArray.Builder();
        var done = new Int64Array.Builder(); var total = new Int64Array.Builder();
        var rps = new DoubleArray.Builder(); var elapsed = new DoubleArray.Builder(); var eta = new DoubleArray.Builder();
        var pct = new Int32Array.Builder(); var status = new StringArray.Builder();
        foreach (var e in entries)
        {
            id.Append(e.Id); table.Append(e.Table); op.Append(e.Operation);
            done.Append(e.Done);
            if (e.Total is long t) total.Append(t); else total.AppendNull();
            rps.Append(Math.Round(e.RowsPerSec, 1)); elapsed.Append(Math.Round(e.ElapsedSec, 1));
            if (e.EtaSec is double es) eta.Append(Math.Round(es, 1)); else eta.AppendNull();
            if (e.Percent is int p) pct.Append(p); else pct.AppendNull();
            status.Append(e.Status);
        }
        var arrays = new IArrowArray[] { id.Build(), table.Build(), op.Build(), done.Build(), total.Build(),
            rps.Build(), elapsed.Build(), eta.Build(), pct.Build(), status.Build() };
        return (schema, new RecordBatch(schema, arrays, entries.Count));
    }

    public override Task<FlightInfo> GetFlightInfo(FlightDescriptor request, ServerCallContext context)
    {
        SetCurrentContext(context); // 4.2.2 B2: 接続ヘッダ/既定から接続コンテキストを解決
        var command = ParseCommand(request);
        switch (command)
        {
            case CommandStatementQuery q:
                _log.LogInformation("GetFlightInfo: ad-hoc query: {Sql}", Truncate(q.Query));
                return Task.FromResult(ExecuteAsFlightInfo(request, q.Query, context.Peer));

            case CommandPreparedStatementQuery pq:
            {
                var handle = pq.PreparedStatementHandle.ToStringUtf8();
                if (!_registry.TryGetPrepared(handle, out var sql) || sql is null)
                    throw new RpcException(new Status(StatusCode.NotFound, $"unknown prepared statement: {handle}"));
                _log.LogInformation("GetFlightInfo: prepared query {Handle}: {Sql}", handle, Truncate(sql));
                return Task.FromResult(ExecuteAsFlightInfo(request, sql, context.Peer));
            }

            case CommandGetSqlInfo:
                _log.LogInformation("GetFlightInfo: GetSqlInfo → 空応答");
                return Task.FromResult(StaticResultAsFlightInfo(request, "GetSqlInfo", SqlInfoResult.Schema, SqlInfoResult.Empty()));

            case CommandGetCatalogs:
                return Task.FromResult(StaticResultAsFlightInfo(request, "GetCatalogs", MetadataResults.CatalogsSchema, MetadataResults.Catalogs()));

            case CommandGetDbSchemas:
                return Task.FromResult(StaticResultAsFlightInfo(request, "GetDbSchemas", MetadataResults.DbSchemasSchema, MetadataResults.DbSchemas()));

            case CommandGetTableTypes:
                return Task.FromResult(StaticResultAsFlightInfo(request, "GetTableTypes", MetadataResults.TableTypesSchema, MetadataResults.TableTypes()));

            case CommandGetTables gt:
            {
                _log.LogInformation("GetFlightInfo: GetTables (include_schema={Inc})", gt.IncludeSchema);
                var batch = BuildTablesResult(gt);
                return Task.FromResult(StaticResultAsFlightInfo(request, "GetTables", batch.Schema, batch));
            }

            default:
                throw Unsupported(nameof(GetFlightInfo), command);
        }
    }

    public override async Task DoGet(FlightTicket ticket, FlightServerRecordBatchStreamWriter responseStream, ServerCallContext context)
    {
        var handle = ticket.Ticket.ToStringUtf8();
        if (!_registry.TryTake(handle, out var query) || query is null)
            throw new RpcException(new Status(StatusCode.NotFound, $"unknown statement handle: {handle}"));

        using (query)
        {
            var sw = Stopwatch.StartNew();
            long rows = 0;
            if (query.StaticBatches is not null)
            {
                foreach (var batch in query.StaticBatches)
                {
                    await responseStream.WriteAsync(batch);
                    rows += batch.Length;
                }
            }
            else if (query.Reader is null)
            {
                await responseStream.WriteAsync(ArrowConvert.RowsAffectedBatch(query.RowsAffected));
                rows = 1;
            }
            else
            {
                foreach (var batch in ArrowConvert.ReadBatches(query.Reader, query.Schema, RowsPerBatch))
                {
                    await responseStream.WriteAsync(batch);
                    rows += batch.Length;
                }
            }
            _log.LogInformation("DoGet {Handle}: {Rows} rows streamed in {Ms} ms", handle, rows, sw.ElapsedMilliseconds);
        }
    }

    public override async Task DoPut(FlightServerRecordBatchStreamReader requestStream, IAsyncStreamWriter<FlightPutResult> responseStream, ServerCallContext context)
    {
        SetCurrentContext(context); // 4.2.2 B2: 接続ヘッダ/既定から接続コンテキストを解決
        var descriptor = await requestStream.FlightDescriptor;
        var command = ParseCommand(descriptor);
        var sess = _sessions.Get(context.Peer);
        switch (command)
        {
            case CommandStatementUpdate u:
            {
                // セッション既定の設定(SET/RESET。F-09)＋本番書き込み許可(3.1.2)。副作用のみで 0 行。
                if (sess.TryApply(u.Query) || TryRefreshNameCache(u.Query))
                {
                    await responseStream.WriteAsync(new FlightPutResult(new DoPutUpdateResult { RecordCount = 0 }.ToByteString()));
                    return;
                }
                var truncTable = BulkDeleteExecutor.TryParseTruncate(u.Query);
                if (truncTable is not null)
                {
                    if (sess.DryRun) // 実行前ドライラン(3.1.1)
                    {
                        DryRunDelete(truncTable, DryRunCount($"SELECT COUNT(*) FROM {truncTable}"), isTruncate: true);
                        await responseStream.WriteAsync(new FlightPutResult(new DoPutUpdateResult { RecordCount = 0 }.ToByteString()));
                        return;
                    }
                    var n = await _bulkDelete.TruncateAndWaitAsync(truncTable, context.CancellationToken, sess.AllowProductionWrites);
                    await responseStream.WriteAsync(new FlightPutResult(new DoPutUpdateResult { RecordCount = n }.ToByteString()));
                    return;
                }
                // 条件付き DELETE → 経路自動選択(WHERE を絞り込みへ変換。F-10 / 4.2.3)
                var delCmd = BulkDeleteExecutor.TryParseDelete(u.Query);
                if (delCmd is not null)
                {
                    if (sess.DryRun) // 実行前ドライラン(3.1.1)
                    {
                        DryRunDelete(delCmd.Table, DryRunCount(DeleteToCountSql(u.Query)), isTruncate: false);
                        await responseStream.WriteAsync(new FlightPutResult(new DoPutUpdateResult { RecordCount = 0 }.ToByteString()));
                        return;
                    }
                    var n = await _bulkDelete.DeleteAndWaitAsync(delCmd, context.CancellationToken, sess.AllowProductionWrites);
                    await responseStream.WriteAsync(new FlightPutResult(new DoPutUpdateResult { RecordCount = n }.ToByteString()));
                    return;
                }
                // DDL サブセット(4.2.1): CREATE TABLE / ALTER TABLE ADD → メタデータ API
                var ddl = Gateway.Dataverse.DdlExecutor.TryParse(u.Query);
                if (ddl is not null)
                {
                    _guard.EnsureWriteAllowed("DDL", ddl.Table, sess.AllowProductionWrites); // 本番環境ガード(3.1.2)
                    var cols = _ddl.Execute(ddl);
                    await responseStream.WriteAsync(new FlightPutResult(new DoPutUpdateResult { RecordCount = cols }.ToByteString()));
                    return;
                }
                _guard.EnsureWriteAllowed("UPDATE", "(ad-hoc)", sess.AllowProductionWrites); // 本番環境ガード(3.1.2)
                _log.LogInformation("DoPut: ad-hoc update: {Sql}", Truncate(u.Query));
                var affected = ExecuteNonQuery(u.Query);
                var result = new DoPutUpdateResult { RecordCount = affected };
                await responseStream.WriteAsync(new FlightPutResult(result.ToByteString()));
                return;
            }

            case CommandPreparedStatementUpdate pu:
            {
                var handle = pu.PreparedStatementHandle.ToStringUtf8();
                if (!_registry.TryGetPrepared(handle, out var sql) || sql is null)
                    throw new RpcException(new Status(StatusCode.NotFound, $"unknown prepared statement: {handle}"));

                // セッション既定の設定(SET/RESET。F-09)＋本番書き込み許可(3.1.2)。副作用のみで 0 行。
                if (sess.TryApply(sql) || TryRefreshNameCache(sql))
                {
                    while (await requestStream.MoveNext(context.CancellationToken)) { /* パラメータなし。空ストリームを消費 */ }
                    await responseStream.WriteAsync(new FlightPutResult(new DoPutUpdateResult { RecordCount = 0 }.ToByteString()));
                    return;
                }

                // TRUNCATE TABLE → 経路自動選択(全行)＋完了待ち(F-10 / 3.3 / 4.2.3)
                var truncTable = BulkDeleteExecutor.TryParseTruncate(sql);
                if (truncTable is not null)
                {
                    while (await requestStream.MoveNext(context.CancellationToken)) { /* パラメータなし。空ストリームを消費 */ }
                    if (sess.DryRun) // 実行前ドライラン(3.1.1)
                    {
                        DryRunDelete(truncTable, DryRunCount($"SELECT COUNT(*) FROM {truncTable}"), isTruncate: true);
                        await responseStream.WriteAsync(new FlightPutResult(new DoPutUpdateResult { RecordCount = 0 }.ToByteString()));
                        return;
                    }
                    var n = await _bulkDelete.TruncateAndWaitAsync(truncTable, context.CancellationToken, sess.AllowProductionWrites);
                    await responseStream.WriteAsync(new FlightPutResult(new DoPutUpdateResult { RecordCount = n }.ToByteString()));
                    return;
                }
                // 条件付き DELETE → 経路自動選択(WHERE を絞り込みへ変換。F-10 / 4.2.3)
                var pdelCmd = BulkDeleteExecutor.TryParseDelete(sql);
                if (pdelCmd is not null)
                {
                    while (await requestStream.MoveNext(context.CancellationToken)) { /* パラメータなし。空ストリームを消費 */ }
                    if (sess.DryRun) // 実行前ドライラン(3.1.1)
                    {
                        DryRunDelete(pdelCmd.Table, DryRunCount(DeleteToCountSql(sql)), isTruncate: false);
                        await responseStream.WriteAsync(new FlightPutResult(new DoPutUpdateResult { RecordCount = 0 }.ToByteString()));
                        return;
                    }
                    var n = await _bulkDelete.DeleteAndWaitAsync(pdelCmd, context.CancellationToken, sess.AllowProductionWrites);
                    await responseStream.WriteAsync(new FlightPutResult(new DoPutUpdateResult { RecordCount = n }.ToByteString()));
                    return;
                }

                // N:N 関連付け(交差テーブル INSERT / ASSOCIATE / DISASSOCIATE。D-13/F-07)
                // 通常 INSERT より先に判定する(交差テーブル INSERT は両方の構文にマッチするため)
                var assoc = AssociateExecutor.TryParse(sql);
                if (assoc is not null)
                {
                    long assocBatches = 0, assocTotal = 0, assocRows = 0;
                    while (await requestStream.MoveNext(context.CancellationToken))
                    {
                        var b = requestStream.Current;
                        assocBatches++;
                        assocRows += b.Length;
                        if (sess.DryRun) continue; // 実行前ドライラン(3.1.1): 実行せず件数のみ集計
                        _log.LogInformation("DoPut(prepared {Handle}): {Verb} batch #{N}: {Rows} rows", handle, assoc.Verb, assocBatches, b.Length);
                        assocTotal += await _associate.ExecuteBatchAsync(assoc, b, context.CancellationToken, sess.AllowProductionWrites);
                    }
                    if (sess.DryRun)
                    {
                        _dryRun.Emit(_dryRun.AnalyzeAssociate(assoc, assocRows), DryRunDir);
                        await responseStream.WriteAsync(new FlightPutResult(new DoPutUpdateResult { RecordCount = 0 }.ToByteString()));
                        return;
                    }
                    await responseStream.WriteAsync(new FlightPutResult(new DoPutUpdateResult { RecordCount = assocTotal }.ToByteString()));
                    return;
                }

                var cmd = PreparedInsertExecutor.TryParseWrite(sql);
                if (cmd is null)
                    throw new RpcException(new Status(StatusCode.Unimplemented,
                        $"only parameterized INSERT/UPSERT is supported for batch execution. Got: {Truncate(sql)}"));

                // セッション既定を文単位ヒントに重ねる(ヒント優先。F-09)
                cmd = cmd with { Options = sess.Effective(cmd.Options) };

                if (sess.DryRun) // 実行前ドライラン(3.1.1): 実行せず件数のみ集計してレポート
                {
                    long dryRows = 0;
                    while (await requestStream.MoveNext(context.CancellationToken))
                        dryRows += requestStream.Current.Length;
                    _dryRun.Emit(_dryRun.AnalyzeWrite(cmd, dryRows, _inserter.RouteFor(cmd)), DryRunDir);
                    await responseStream.WriteAsync(new FlightPutResult(new DoPutUpdateResult { RecordCount = 0 }.ToByteString()));
                    return;
                }

                var startUtc = DateTime.UtcNow;
                var sw = Stopwatch.StartNew();
                long batches = 0, total = 0, rowOffset = 0;
                Gateway.Checkpoint.CheckpointSession? session = null;
                var failures = new Gateway.Dataverse.FailureReport(); // 失敗行レポート(F-14)
                // 進捗トラッキング(3.1.5)。ストリーム投入のため総数は不明(ETA は算出不可・レートで代替)。
                var progressId = _progress.Begin(cmd.Table, cmd.Kind.ToString(), total: null);
                try
                {
                    while (await requestStream.MoveNext(context.CancellationToken))
                    {
                        var batch = requestStream.Current;
                        batches++;
                        // 最初のバッチでチェックポイントジョブを開始/再開(F-11)
                        session ??= _inserter.BeginSession(cmd, batch);
                        _log.LogInformation("DoPut(prepared {Handle}): parameter batch #{N}: {Rows} rows ({Kind})", handle, batches, batch.Length, cmd.Kind);
                        // チャンク完了ごとに進捗を更新(大バッチ内でも rows_done が進む。4.1.4)
                        total += await _inserter.ExecuteBatchAsync(cmd, batch, context.CancellationToken, session, failures, rowOffset, sess.AllowProductionWrites,
                            onProgress: done => _progress.Report(progressId, done));
                        rowOffset += batch.Length;
                        _progress.Report(progressId, rowOffset);
                    }
                    session?.Complete();
                    _progress.Complete(progressId);
                }
                catch (OperationCanceledException)
                {
                    // 安全なキャンセル(3.1.5): 進捗を cancelled に。コミット済みバッチは
                    // チェックポイント(F-11)に残り、同一入力の再投入で未完了分から安全に再開できる。
                    _progress.Complete(progressId, "cancelled");
                    _log.LogWarning("DoPut(prepared {Handle}): キャンセルされました({Rows}行処理済)。再投入で未完了分から再開可能", handle, rowOffset);
                    throw;
                }

                // 失敗行があれば CSV を出力(F-14)
                var reportDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DataverseGW", "reports");
                string? failureCsv = null;
                if (failures.Count > 0)
                {
                    failureCsv = failures.WriteCsv(reportDir, $"{cmd.Table}_{handle}", _reportRetentionDays);
                    _log.LogWarning("DoPut(prepared {Handle}): {Failed} 行が失敗。レポート: {Csv}", handle, failures.Count, failureCsv);
                }

                // 監査ログ(NFR): 対象・件数・経路・所要・オプションを記録(実施報告に転用)
                _audit.Record(new Gateway.Dataverse.AuditRecord(
                    startUtc, DateTime.UtcNow, cmd.Table, cmd.Kind.ToString(), _inserter.RouteFor(cmd),
                    TotalRows: rowOffset, Written: total, Skipped: rowOffset - total - failures.Count,
                    Failed: failures.Count, Options: PreparedInsertExecutor.DescribeOptions(cmd.Options),
                    FailureCsv: failureCsv), $"{cmd.Table}_{handle}");
                _log.LogInformation(
                    "DoPut(prepared {Handle}): {Total} rows newly written into {Table} across {Batches} batches in {Ms} ms ({Rate:F0} rows/sec)",
                    handle, total, cmd.Table, batches, sw.ElapsedMilliseconds,
                    sw.Elapsed.TotalSeconds > 0 ? total / sw.Elapsed.TotalSeconds : 0);

                var result = new DoPutUpdateResult { RecordCount = total };
                await responseStream.WriteAsync(new FlightPutResult(result.ToByteString()));
                return;
            }

            default:
                throw Unsupported(nameof(DoPut), command);
        }
    }

    public override async Task DoAction(FlightAction request, IAsyncStreamWriter<FlightResult> responseStream, ServerCallContext context)
    {
        SetCurrentContext(context); // 4.2.2 B2: 接続ヘッダ/既定から接続コンテキストを解決
        switch (request.Type)
        {
            case "CreatePreparedStatement":
            {
                var body = Any.Parser.ParseFrom(request.Body);
                var req = body.Unpack<ActionCreatePreparedStatementRequest>();
                var handle = _registry.RegisterPrepared(req.Query);
                _log.LogInformation("CreatePreparedStatement {Handle}: {Sql}", handle, Truncate(req.Query));

                var result = new ActionCreatePreparedStatementResult
                {
                    PreparedStatementHandle = ByteString.CopyFromUtf8(handle),
                };

                if (ProducesResultSet(req.Query))
                {
                    var datasetSchema = TryDescribeQuerySchema(req.Query);
                    if (datasetSchema is not null)
                        result.DatasetSchema = SchemaSerializer.Serialize(datasetSchema);
                }
                else if (AssociateExecutor.TryParse(req.Query) is not null)
                {
                    // N:N 関連付けは 2 つの GUID(record1id, record2id)をパラメータとする
                    var s = new Apache.Arrow.Schema.Builder()
                        .Field(new Apache.Arrow.Field("record1id", Apache.Arrow.Types.StringType.Default, false))
                        .Field(new Apache.Arrow.Field("record2id", Apache.Arrow.Types.StringType.Default, false))
                        .Build();
                    result.ParameterSchema = SchemaSerializer.Serialize(s);
                }
                else
                {
                    var parameterSchema = TryDescribeInsertParameters(req.Query);
                    if (parameterSchema is not null)
                        result.ParameterSchema = SchemaSerializer.Serialize(parameterSchema);
                }

                await responseStream.WriteAsync(new FlightResult(Any.Pack(result).ToByteString()));
                return;
            }

            case "ClosePreparedStatement":
            {
                var body = Any.Parser.ParseFrom(request.Body);
                var req = body.Unpack<ActionClosePreparedStatementRequest>();
                var handle = req.PreparedStatementHandle.ToStringUtf8();
                _registry.RemovePrepared(handle);
                _log.LogInformation("ClosePreparedStatement {Handle}", handle);
                await responseStream.WriteAsync(new FlightResult(ByteString.Empty));
                return;
            }

            default:
                _log.LogWarning("DoAction: unsupported action {Type}", request.Type);
                throw new RpcException(new Status(StatusCode.Unimplemented, $"action not supported: {request.Type}"));
        }
    }

    public override async Task ListActions(IAsyncStreamWriter<FlightActionType> responseStream, ServerCallContext context)
    {
        await responseStream.WriteAsync(new FlightActionType("CreatePreparedStatement", "Create a prepared statement"));
        await responseStream.WriteAsync(new FlightActionType("ClosePreparedStatement", "Close a prepared statement"));
    }

    // ---- 内部処理 ----

    private FlightInfo ExecuteAsFlightInfo(FlightDescriptor descriptor, string sql, string? peer)
    {
        var query = ExecuteQuery(sql, peer);
        _registry.Register(query);
        var endpoint = new FlightEndpoint(new FlightTicket(query.Handle), System.Array.Empty<FlightLocation>());
        return new FlightInfo(query.Schema, descriptor, new[] { endpoint }, -1, -1);
    }

    private FlightInfo StaticResultAsFlightInfo(FlightDescriptor descriptor, string label, Schema schema, params RecordBatch[] batches)
    {
        var handle = Guid.NewGuid().ToString("N");
        _registry.Register(new ActiveQuery { Handle = handle, Sql = label, Schema = schema, StaticBatches = batches });
        var endpoint = new FlightEndpoint(new FlightTicket(handle), System.Array.Empty<FlightLocation>());
        return new FlightInfo(schema, descriptor, new[] { endpoint }, -1, -1);
    }

    private ActiveQuery ExecuteQuery(string sql, string? peer)
    {
        var sess = _sessions.Get(peer);
        // SET/RESET がクエリ経路で来た場合(F-09)＋本番書き込み許可(3.1.2)は副作用のみ・0 行返却
        if (sess.TryApply(sql) || TryRefreshNameCache(sql))
            return new ActiveQuery
            {
                Handle = Guid.NewGuid().ToString("N"), Sql = sql, Reader = null,
                Schema = ArrowConvert.RowsAffectedSchema(), RowsAffected = 0,
            };

        // TRUNCATE がクエリ経路で来た場合(Sql4Cds は TRUNCATE 非対応)は BulkDelete で処理
        var truncTable = BulkDeleteExecutor.TryParseTruncate(sql);
        if (truncTable is not null)
        {
            var deleted = sess.DryRun
                ? DryRunDelete(truncTable, DryRunCount($"SELECT COUNT(*) FROM {truncTable}"), isTruncate: true)
                : _bulkDelete.TruncateAndWaitAsync(truncTable, CancellationToken.None, sess.AllowProductionWrites).GetAwaiter().GetResult();
            return new ActiveQuery
            {
                Handle = Guid.NewGuid().ToString("N"), Sql = sql, Reader = null,
                Schema = ArrowConvert.RowsAffectedSchema(), RowsAffected = deleted,
            };
        }
        // 条件付き DELETE もクエリ経路で来うる(BulkDelete で処理。F-10)
        var qdelCmd = BulkDeleteExecutor.TryParseDelete(sql);
        if (qdelCmd is not null)
        {
            var deleted = sess.DryRun
                ? DryRunDelete(qdelCmd.Table, DryRunCount(DeleteToCountSql(sql)), isTruncate: false)
                : _bulkDelete.DeleteAndWaitAsync(qdelCmd, CancellationToken.None, sess.AllowProductionWrites).GetAwaiter().GetResult();
            return new ActiveQuery
            {
                Handle = Guid.NewGuid().ToString("N"), Sql = sql, Reader = null,
                Schema = ArrowConvert.RowsAffectedSchema(), RowsAffected = deleted,
            };
        }

        // DDL サブセット(4.2.1): CREATE TABLE / ALTER TABLE ADD もクエリ経路で来うる。メタデータ API で処理
        var ddl = Gateway.Dataverse.DdlExecutor.TryParse(sql);
        if (ddl is not null)
        {
            _guard.EnsureWriteAllowed("DDL", ddl.Table, sess.AllowProductionWrites); // 本番環境ガード(3.1.2)
            var cols = _ddl.Execute(ddl);
            return new ActiveQuery
            {
                Handle = Guid.NewGuid().ToString("N"), Sql = sql, Reader = null,
                Schema = ArrowConvert.RowsAffectedSchema(), RowsAffected = cols,
            };
        }

        // 進捗仮想テーブル(3.1.5): SELECT ... FROM gateway_progress をトラッカーのスナップショットで返す
        // (別接続からポーリングして進捗バー/残時間を表示する用途)。Sql4Cds に渡す前に横取り。
        if (System.Text.RegularExpressions.Regex.IsMatch(sql, @"\bfrom\s+gateway_progress\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            var (pschema, pbatch) = BuildProgressBatch(_progress.Snapshot());
            return new ActiveQuery { Handle = Guid.NewGuid().ToString("N"), Sql = sql, Reader = null, Schema = pschema, StaticBatches = new[] { pbatch } };
        }

        var sw = Stopwatch.StartNew();
        var con = _connection.CreateSqlConnection();
        var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 600;
        var handle = Guid.NewGuid().ToString("N");

        if (!ProducesResultSet(sql))
        {
            _guard.EnsureWriteAllowed("UPDATE", "(ad-hoc)", sess.AllowProductionWrites); // 本番環境ガード(3.1.2)
            var affected = cmd.ExecuteNonQuery();
            _log.LogInformation("non-result statement executed: {Affected} rows in {Ms} ms", affected, sw.ElapsedMilliseconds);
            return new ActiveQuery
            {
                Handle = handle, Sql = sql, Connection = con, Command = cmd, Reader = null,
                Schema = ArrowConvert.RowsAffectedSchema(), RowsAffected = affected,
            };
        }

        var reader = cmd.ExecuteReader();
        var schema = ArrowConvert.SchemaFromReader(reader);
        _log.LogInformation("query started in {Ms} ms ({Fields} fields)", sw.ElapsedMilliseconds, reader.FieldCount);
        return new ActiveQuery { Handle = handle, Sql = sql, Connection = con, Command = cmd, Reader = reader, Schema = schema };
    }

    private static bool ProducesResultSet(string sql)
    {
        var s = sql.AsSpan().TrimStart();
        while (s.Length > 0)
        {
            if (s[0] == '-' && s.Length > 1 && s[1] == '-')
            {
                var nl = s.IndexOf('\n');
                if (nl < 0) return false;
                s = s[(nl + 1)..].TrimStart();
            }
            else if (s[0] == '/' && s.Length > 1 && s[1] == '*')
            {
                var end = s.IndexOf("*/".AsSpan());
                if (end < 0) return false;
                s = s[(end + 2)..].TrimStart();
            }
            else break;
        }
        var word = 0;
        while (word < s.Length && char.IsLetter(s[word])) word++;
        var first = s[..word].ToString().ToUpperInvariant();
        return first is "SELECT" or "WITH";
    }

    private RecordBatch BuildTablesResult(CommandGetTables gt)
    {
        var pattern = gt.HasTableNameFilterPattern ? gt.TableNameFilterPattern : null;
        var rows = new List<MetadataResults.TableRow>();
        foreach (var table in PocTables)
        {
            if (!string.IsNullOrEmpty(pattern) && !LikeMatch(table, pattern)) continue;
            byte[]? schemaBytes = null;
            if (gt.IncludeSchema)
            {
                var schema = TryDescribeTableSchema(table);
                if (schema is not null)
                    schemaBytes = SchemaSerializer.Serialize(schema).ToByteArray();
            }
            rows.Add(new MetadataResults.TableRow(table, "TABLE", schemaBytes));
        }
        return MetadataResults.Tables(rows, gt.IncludeSchema);
    }

    private Schema? TryDescribeTableSchema(string table)
    {
        try
        {
            using var con = _connection.CreateSqlConnection();
            using var cmd = con.CreateCommand();
            cmd.CommandText = $"SELECT * FROM {table} WHERE 1 = 0";
            cmd.CommandTimeout = 60;
            using var reader = cmd.ExecuteReader();
            return ArrowConvert.SchemaFromReader(reader);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "table_schema 取得に失敗: {Table}", table);
            return null;
        }
    }

    private static bool LikeMatch(string value, string pattern)
    {
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("%", ".*").Replace("_", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(value, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private Schema? TryDescribeInsertParameters(string sql)
    {
        var cmd = PreparedInsertExecutor.TryParseWrite(sql);
        if (cmd is null) return null;
        try
        {
            var cols = string.Join(", ", cmd.Columns);
            using var con = _connection.CreateSqlConnection();
            using var command = con.CreateCommand();
            command.CommandText = $"SELECT {cols} FROM {cmd.Table} WHERE 1 = 0";
            command.CommandTimeout = 60;
            using var reader = command.ExecuteReader();
            var schema = ArrowConvert.SchemaFromReader(reader);
            _log.LogInformation("prepared {Kind} parameters described: {Table} ({Count} params)", cmd.Kind, cmd.Table, cmd.Columns.Count);
            return schema;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "parameter_schema の取得に失敗(空で返す): {Sql}", Truncate(sql));
            return null;
        }
    }

    private Schema? TryDescribeQuerySchema(string sql)
    {
        try
        {
            using var con = _connection.CreateSqlConnection();
            using var cmd = con.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = 120;
            using var reader = cmd.ExecuteReader(System.Data.CommandBehavior.SchemaOnly);
            if (reader.FieldCount == 0) return null;
            return ArrowConvert.SchemaFromReader(reader);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "dataset_schema の取得に失敗(空で返す): {Sql}", Truncate(sql));
            return null;
        }
    }

    private long ExecuteNonQuery(string sql)
    {
        var sw = Stopwatch.StartNew();
        using var con = _connection.CreateSqlConnection();
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 600;
        var affected = cmd.ExecuteNonQuery();
        _log.LogInformation("update executed: {Affected} rows in {Ms} ms", affected, sw.ElapsedMilliseconds);
        return affected;
    }

    private object? ParseCommand(FlightDescriptor descriptor)
    {
        if (descriptor.Command.IsEmpty) return null;
        Any any;
        try { any = Any.Parser.ParseFrom(descriptor.Command); }
        catch (InvalidProtocolBufferException) { return null; }

        if (any.Is(CommandStatementQuery.Descriptor)) return any.Unpack<CommandStatementQuery>();
        if (any.Is(CommandGetSqlInfo.Descriptor)) return any.Unpack<CommandGetSqlInfo>();
        if (any.Is(CommandGetCatalogs.Descriptor)) return any.Unpack<CommandGetCatalogs>();
        if (any.Is(CommandGetDbSchemas.Descriptor)) return any.Unpack<CommandGetDbSchemas>();
        if (any.Is(CommandGetTables.Descriptor)) return any.Unpack<CommandGetTables>();
        if (any.Is(CommandGetTableTypes.Descriptor)) return any.Unpack<CommandGetTableTypes>();
        if (any.Is(CommandStatementUpdate.Descriptor)) return any.Unpack<CommandStatementUpdate>();
        if (any.Is(CommandPreparedStatementQuery.Descriptor)) return any.Unpack<CommandPreparedStatementQuery>();
        if (any.Is(CommandPreparedStatementUpdate.Descriptor)) return any.Unpack<CommandPreparedStatementUpdate>();
        return any;
    }

    private RpcException Unsupported(string operation, object? command)
    {
        var name = command switch { Any a => a.TypeUrl, null => "(no command)", _ => command.GetType().Name };
        _log.LogWarning("{Operation}: unsupported command {Command}", operation, name);
        return new RpcException(new Status(StatusCode.Unimplemented, $"{operation}: command not supported: {name}"));
    }

    private static string Truncate(string sql)
    {
        var flat = sql.ReplaceLineEndings(" ");
        return flat.Length <= 200 ? flat : flat[..200] + "…";
    }
}
