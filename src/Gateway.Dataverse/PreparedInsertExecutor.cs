using System.ServiceModel;
using System.Text.RegularExpressions;
using Apache.Arrow;
using Gateway.Checkpoint;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;

namespace Gateway.Dataverse;

/// <summary>
/// パラメータ化 INSERT/UPSERT を Dataverse のバルク/EM メッセージで実行する(製品版)。
/// 経路(要件定義書 v1.2 3.2):
///   INSERT: CreateMultiple(対応テーブル) / ExecuteMultiple+CreateRequest(非対応)
///   UPSERT: ExecuteMultiple+UpsertRequest(UpsertMultiple は業務テーブル非対応。OBS-3)
/// batchSize 単位に分割し、dop 個の ServiceClient クローンで並列実行する(OBS-7)。
///
/// 今後の拡張: 経路のメタデータ事前判定(2.2.1)、型ロスレス(2.1.4)、
///   代替キー名→キー列の解決(現状は ON KEY にキー列を列挙)。
/// </summary>
public sealed class PreparedInsertExecutor
{
    // テーブル名は logical name のほか、スキーマ修飾(dbo.t)・角括弧([dbo].[t])・二重引用符("dbo"."t")を許容する。
    // DBeaver など JDBC クライアントは INSERT INTO dbo.poc_gadget の形で送るため(NormalizeTable で素の名前に戻す)。
    private const string TablePattern = @"(?<table>[\w.\[\]""]+)";

    private static readonly Regex InsertPattern = new(
        @"^\s*INSERT\s+INTO\s+" + TablePattern + @"\s*\(\s*(?<cols>[^)]*?)\s*\)\s*VALUES\s*\(\s*(?<vals>[^)]*?)\s*\)\s*;?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex UpsertPattern = new(
        @"^\s*UPSERT\s+INTO\s+" + TablePattern + @"\s*\(\s*(?<cols>[^)]*?)\s*\)\s*VALUES\s*\(\s*(?<vals>[^)]*?)\s*\)(\s+ON\s+KEY\s*\(\s*(?<keys>[^)]*?)\s*\))?\s*;?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private readonly DataverseConnection _connection;
    private readonly EntityMetadataCache _metadata;
    private readonly BulkCapabilityCache _bulkCapability;
    private readonly PrivilegeChecker _privilege;
    private readonly CheckpointStore? _checkpoint;
    private readonly ProductionGuard _guard;
    private readonly NameResolver _nameResolver;
    private readonly ILogger _log;
    private readonly int _batchSize;
    private readonly int _dop;
    private readonly bool _includeErrorMessage;

    public PreparedInsertExecutor(DataverseConnection connection, EntityMetadataCache metadata,
        BulkCapabilityCache bulkCapability, PrivilegeChecker privilege, IConfiguration config,
        ProductionGuard guard, NameResolver nameResolver, ILogger<PreparedInsertExecutor> log, CheckpointStore? checkpoint = null)
    {
        _connection = connection;
        _metadata = metadata;
        _bulkCapability = bulkCapability;
        _privilege = privilege;
        _checkpoint = checkpoint;
        _guard = guard;
        _nameResolver = nameResolver;
        _log = log;
        _batchSize = config.GetValue<int?>("Gateway:BatchSize") ?? 200;
        // M-2: 失敗行レポートの error 列は既定でエラーコードのみ(PII を含み得るメッセージ全文は出さない)。
        _includeErrorMessage = config.GetValue<bool?>("Gateway:FailureReportIncludeMessage") ?? false;
        // dop-hint 追従(F-12): 明示設定が無ければ環境の推奨並列度に従う
        var configuredDop = config.GetValue<int?>("Gateway:Dop");
        var dopHint = Math.Max(1, connection.Client.RecommendedDegreesOfParallelism);
        _dop = configuredDop ?? dopHint;
        _log.LogInformation("PreparedInsertExecutor: batchSize={Batch}, dop={Dop} (dop-hint={Hint}, source={Src})",
            _batchSize, _dop, dopHint, configuredDop.HasValue ? "config" : "dop-hint");
    }

    // 文単位ヒント: INSERT/UPSERT /*+ BYPASS_PLUGINS BYPASS_FLOWS ... */ INTO ...
    private static readonly Regex HintPattern = new(@"/\*\+\s*(?<body>.*?)\s*\*/",
        RegexOptions.Singleline | RegexOptions.Compiled);

    public enum WriteKind { Insert, Upsert }

    /// <summary>
    /// バイパス制御(F-09)。文単位ヒントまたはセッション既定から解決する。既定は非バイパス(安全側)。
    /// </summary>
    public sealed record WriteOptions(
        bool BypassSyncPlugins = false,
        bool BypassAsyncPlugins = false,
        bool BypassFlows = false,
        bool SuppressDuplicateDetection = false,
        Guid? ImpersonateUserId = null,
        string? JobId = null,
        bool ForceNewJob = false,
        bool ForceExecuteMultiple = false,
        bool ResolveNames = false)
    {
        public static readonly WriteOptions None = new();
        /// <summary>ApplyOptions で設定するバイパス系パラメータの有無(なりすましは別扱い)。</summary>
        public bool Any => BypassSyncPlugins || BypassAsyncPlugins || BypassFlows || SuppressDuplicateDetection;
    }

    public sealed record WriteCommand(
        WriteKind Kind, string Table, IReadOnlyList<string> Columns,
        IReadOnlyList<string> KeyColumns, string? IdColumn, WriteOptions Options);

    private static List<string> SplitCols(string s) => s
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(c => c.Trim('[', ']', '"', '`', ' ')).ToList();

    /// <summary>スキーマ修飾・角括弧・引用符を除去して Dataverse の logical name に正規化する。
    /// 例: dbo.poc_gadget / [dbo].[poc_gadget] / "dbo"."poc_gadget" → poc_gadget。</summary>
    public static string NormalizeTable(string raw)
    {
        var last = raw.Trim().Split('.').Last();      // schema.table → table(logical name にドットは無い)
        return last.Trim('[', ']', '"', '`', ' ');
    }

    /// <summary>SQL 中の /*+ ... */ ヒントを解析して WriteOptions を返す(トークンは大文字小文字無視)。</summary>
    public static WriteOptions ParseHints(string sql)
    {
        var opt = WriteOptions.None;
        foreach (Match m in HintPattern.Matches(sql))
        {
            foreach (var tok in m.Groups["body"].Value.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var up = tok.ToUpperInvariant();
                // なりすまし(createdby 保持。Q-6): IMPERSONATE=<guid> / AS_USER=<guid>
                if ((up.StartsWith("IMPERSONATE=") || up.StartsWith("AS_USER=")) && Guid.TryParse(tok[(tok.IndexOf('=') + 1)..], out var uid))
                {
                    opt = opt with { ImpersonateUserId = uid };
                    continue;
                }
                // ジョブ同定の上書き(D-15): JOB_ID=<任意> / FORCE_NEW_JOB
                if (up.StartsWith("JOB_ID="))
                {
                    opt = opt with { JobId = tok[(tok.IndexOf('=') + 1)..] };
                    continue;
                }
                if (up == "FORCE_NEW_JOB")
                {
                    opt = opt with { ForceNewJob = true };
                    continue;
                }
                opt = up switch
                {
                    "BYPASS_PLUGINS" => opt with { BypassSyncPlugins = true },
                    "BYPASS_ASYNC_PLUGINS" => opt with { BypassAsyncPlugins = true },
                    "BYPASS_FLOWS" => opt with { BypassFlows = true },
                    "SUPPRESS_DUPLICATE_DETECTION" => opt with { SuppressDuplicateDetection = true },
                    // バルク(CreateMultiple)を使わず ExecuteMultiple 経路を強制(F-04 検証/バルク不調時の退避)
                    "NO_BULK" or "FORCE_EXECUTE_MULTIPLE" => opt with { ForceExecuteMultiple = true },
                    // 名前→ID 自動解決を有効化(3.1.4 / R-5。既定 OFF。曖昧・未一致は行エラー)
                    "RESOLVE_NAMES" => opt with { ResolveNames = true },
                    _ => opt,
                };
            }
        }
        return opt;
    }

    public static WriteCommand? TryParseWrite(string sql)
    {
        var options = ParseHints(sql);
        sql = HintPattern.Replace(sql, " "); // ヒントを除去してから構文解析

        var mi = InsertPattern.Match(sql);
        if (mi.Success)
        {
            var cols = SplitCols(mi.Groups["cols"].Value);
            var vals = SplitCols(mi.Groups["vals"].Value);
            if (cols.Count == 0 || vals.Count != cols.Count || vals.Any(v => v != "?")) return null;
            return new WriteCommand(WriteKind.Insert, NormalizeTable(mi.Groups["table"].Value), cols, System.Array.Empty<string>(), null, options);
        }

        var mu = UpsertPattern.Match(sql);
        if (mu.Success)
        {
            var table = NormalizeTable(mu.Groups["table"].Value);
            var cols = SplitCols(mu.Groups["cols"].Value);
            var vals = SplitCols(mu.Groups["vals"].Value);
            if (cols.Count == 0 || vals.Count != cols.Count || vals.Any(v => v != "?")) return null;

            if (mu.Groups["keys"].Success && mu.Groups["keys"].Value.Trim().Length > 0)
            {
                var keys = SplitCols(mu.Groups["keys"].Value);
                if (keys.Any(k => !cols.Contains(k, StringComparer.OrdinalIgnoreCase))) return null;
                return new WriteCommand(WriteKind.Upsert, table, cols, keys, null, options);
            }

            var idCol = cols.FirstOrDefault(c => c.Equals(table + "id", StringComparison.OrdinalIgnoreCase));
            if (idCol is null) return null;
            return new WriteCommand(WriteKind.Upsert, table, cols, System.Array.Empty<string>(), idCol, options);
        }

        return null;
    }

    /// <summary>
    /// チェックポイントジョブを開始/再開する(F-11/D-15)。無効時は null。
    /// ジョブ同定: FORCE_NEW_JOB → 毎回新規 / JOB_ID → 明示 / 既定 → 入力指紋(先頭行含む)。
    /// </summary>
    public CheckpointSession? BeginSession(WriteCommand cmd, RecordBatch firstBatch)
    {
        if (_checkpoint is null) return null;

        string fingerprint;
        if (cmd.Options.ForceNewJob)
            fingerprint = "force:" + Guid.NewGuid().ToString("N");
        else if (!string.IsNullOrWhiteSpace(cmd.Options.JobId))
            fingerprint = "job:" + cmd.Options.JobId;
        else
        {
            var env = _connection.Client.ConnectedOrgUriActual?.ToString() ?? "";
            var firstRowHash = firstBatch.Length > 0
                ? JobFingerprint.HashRow(Enumerable.Range(0, cmd.Columns.Count).Select(ci => ArrowValue.Get(firstBatch.Column(ci), 0)))
                : null;
            fingerprint = JobFingerprint.Compute(env, cmd.Table, cmd.Columns, rowCount: -1, firstRow: firstRowHash);
        }

        var job = _checkpoint.StartOrResume(fingerprint, cmd.Table, DateTime.UtcNow);
        var committed = _checkpoint.GetCommittedBatches(job.JobId);
        // 代替キーも GUID も無いプレーン INSERT のみ、行内容ハッシュで冪等化する(D-14 ③)
        var rowTracking = cmd.Kind == WriteKind.Insert && cmd.IdColumn is null && cmd.KeyColumns.Count == 0;
        var tracked = rowTracking ? _checkpoint.GetTrackedHashes(job.JobId) : new HashSet<string>();
        if (!job.IsNew && (committed.Count > 0 || tracked.Count > 0))
            _log.LogInformation("ジョブ再開: JobId={JobId} 既コミット {B} バッチ / 追跡 {R} 行をスキップ",
                job.JobId, committed.Count, tracked.Count);
        return new CheckpointSession(_checkpoint, job.JobId, committed, tracked, isResumed: !job.IsNew, rowTrackingEnabled: rowTracking);
    }

    /// <summary>監査用の経路説明(バルク/EM)。</summary>
    public string RouteFor(WriteCommand cmd)
    {
        if (cmd.Kind == WriteKind.Upsert) return "ExecuteMultiple(Upsert)";
        if (cmd.Options.ForceExecuteMultiple) return "ExecuteMultiple(Create)";
        return _bulkCapability.SupportsCreateMultiple(cmd.Table) ? "CreateMultiple" : "ExecuteMultiple(Create)";
    }

    /// <summary>監査用の使用オプション要約(バイパス・なりすまし・ジョブ同定)。</summary>
    public static string DescribeOptions(WriteOptions o)
    {
        var parts = new List<string>();
        var bypass = new List<string>();
        if (o.BypassSyncPlugins) bypass.Add("SyncPlugins");
        if (o.BypassAsyncPlugins) bypass.Add("AsyncPlugins");
        if (o.BypassFlows) bypass.Add("Flows");
        if (bypass.Count > 0) parts.Add("bypass=" + string.Join("/", bypass));
        if (o.SuppressDuplicateDetection) parts.Add("suppressDuplicateDetection");
        if (o.ImpersonateUserId is Guid u) parts.Add($"impersonate={u}");
        if (!string.IsNullOrWhiteSpace(o.JobId)) parts.Add($"jobId={o.JobId}");
        if (o.ForceNewJob) parts.Add("forceNewJob");
        if (o.ForceExecuteMultiple) parts.Add("noBulk");
        if (o.ResolveNames) parts.Add("resolveNames");
        return parts.Count == 0 ? "(既定)" : string.Join("; ", parts);
    }

    /// <summary>入力行(Arrow)の内容ハッシュ。ローカル追跡キーの識別子。</summary>
    private static string HashInputRow(WriteCommand cmd, RecordBatch batch, int row)
        => JobFingerprint.HashRow(Enumerable.Range(0, cmd.Columns.Count).Select(ci => ArrowValue.Get(batch.Column(ci), row)));

    public Task<int> ExecuteBatchAsync(WriteCommand cmd, RecordBatch batch, CancellationToken ct)
        => ExecuteBatchAsync(cmd, batch, ct, checkpoint: null, failures: null, rowOffset: 0);

    public async Task<int> ExecuteBatchAsync(WriteCommand cmd, RecordBatch batch, CancellationToken ct,
        CheckpointSession? checkpoint, FailureReport? failures = null, long rowOffset = 0,
        bool allowProduction = false, Action<long>? onProgress = null)
    {
        var rowCount = batch.Length;
        if (rowCount == 0) return 0;

        _guard.EnsureWriteAllowed(cmd.Kind.ToString(), cmd.Table, allowProduction); // 本番環境ガード(3.1.2/4.2.2b セッション許可)

        // 監査列(createdon/createdby)を含む場合は特権を実行前検査(F-08)
        if (cmd.Columns.Any(c => c.Equals("createdon", StringComparison.OrdinalIgnoreCase)
                              || c.Equals("createdby", StringComparison.OrdinalIgnoreCase)))
            _privilege.EnsureCanOverrideCreatedColumns();

        // なりすまし(IMPERSONATE/CallerId)を指定した場合は特権を実行前検査(F-08/M-1)。
        // CallerId を設定する全経路(単一ワーカー/スケールアウト。dvload も本 executor 経由)より前に通す。
        if (cmd.Options.ImpersonateUserId is not null)
            _privilege.EnsureCanImpersonate();

        // 型変換用の属性メタデータ(テーブル単位・キャッシュ済み)
        var attrs = _metadata.GetAttributes(cmd.Table);
        var entities = new List<Entity>(rowCount);
        var tracking = checkpoint?.RowTrackingEnabled == true;
        var rowHashes = tracking ? new List<string>(rowCount) : null;
        var rowIds = new List<long>(rowCount); // 各エンティティの元入力行番号(除外があっても失敗行を正しく特定)
        for (var row = 0; row < rowCount; row++)
        {
            Entity e;
            try
            {
                e = BuildEntity(cmd, attrs, batch, row);
            }
            catch (Exception ex) when (failures is not null && ex is not OperationCanceledException)
            {
                // 行構築時エラー(名前→ID 解決の曖昧/未一致 3.1.4 等)は行エラーとして記録し除外(良行は継続)
                // CSV には安全な要約のみ(M-2)。サーバーログには全文を残す。
                failures.Add(rowOffset + row, cmd.Table, null, ErrorSummary.Safe(ex, _includeErrorMessage));
                _log.LogWarning("失敗行 row={Row} table={Table}(構築時): {Msg}", rowOffset + row, cmd.Table, ex.Message);
                continue;
            }
            entities.Add(e);
            rowHashes?.Add(HashInputRow(cmd, batch, row));
            rowIds.Add(rowOffset + row);
        }

        var chunks = new List<List<Entity>>();
        var hashChunks = tracking ? new List<List<string>>() : null;
        var idChunks = new List<long[]>();
        for (var i = 0; i < entities.Count; i += _batchSize)
        {
            var len = Math.Min(_batchSize, entities.Count - i);
            chunks.Add(entities.GetRange(i, len));
            hashChunks?.Add(rowHashes!.GetRange(i, len));
            idChunks.Add(rowIds.GetRange(i, len).ToArray());
        }

        // このバッチのチャンクに決定的な連番を割り当てる(再開スキップ判定用)
        var baseSeq = checkpoint?.ReserveSequences(chunks.Count) ?? 0;
        long progressDone = 0; // このバッチ内で処理済みの行数(チャンク粒度の進捗通知用。3.1.5/4.1.4)

        // 1 チャンクを処理: 既コミットはスキップ、実行成功で記録。戻り値は「新規に処理した行数」
        int RunChunk(ServiceClient client, int index)
        {
            var seq = baseSeq + index;
            if (checkpoint is not null && checkpoint.IsCommitted(seq)) return 0; // バッチ単位の再開スキップ

            int n;
            if (tracking)
                n = ExecuteInsertWithTracking(client, cmd, chunks[index], hashChunks![index], idChunks[index], checkpoint!, failures);
            else
                n = ExecuteChunk(client, cmd, chunks[index], idChunks[index], failures);

            checkpoint?.Record(seq, chunks[index].Count);
            // チャンク完了ごとに累積処理行数を通知(大バッチ内でも進捗が進む。4.1.4)
            if (onProgress is not null)
                onProgress(rowOffset + Interlocked.Add(ref progressDone, chunks[index].Count));
            return n;
        }

        // スケールアウト(4.2.4): 追加 SP があれば書き込みを SP 間で分散し throttle 予算を合算する。
        // 実効並列度 = dop × SP 数(各 SP が個別の推奨並列度を持つ)。単一 SP なら従来どおり dop。
        var pool = _connection.ScaleOutPool;
        var poolCount = pool.Count;
        var workers = Math.Min(_dop * poolCount, chunks.Count);
        if (workers <= 1)
        {
            using var only = _connection.Clone();
            if (cmd.Options.ImpersonateUserId is Guid u1) only.CallerId = u1; // なりすまし(createdby 保持)
            var n = 0;
            for (var i = 0; i < chunks.Count; i++) n += RunChunk(only, i);
            return n;
        }
        if (poolCount > 1)
            _log.LogInformation("スケールアウト: {Sp} SP × dop={Dop} → worker={Workers}(chunks={Chunks})",
                poolCount, _dop, workers, chunks.Count);

        // 各 worker を SP プールに巡回割り当て(worker i → pool[i % poolCount] のクローン)
        var clients = new ServiceClient[workers];
        for (var i = 0; i < workers; i++)
        {
            clients[i] = pool[i % poolCount].Clone();
            if (cmd.Options.ImpersonateUserId is Guid u) clients[i].CallerId = u;
        }
        try
        {
            // ワークキュー方式: 各 worker は次の未処理チャンクを取得。速い(未 throttle の)SP が
            // 多くのチャンクを処理し、遅い SP に律速されず自然に負荷分散される。
            var nextIndex = -1;
            var tasks = new Task<int>[workers];
            for (var w = 0; w < workers; w++)
            {
                var worker = w;
                tasks[worker] = Task.Run(() =>
                {
                    var local = 0;
                    int idx;
                    while ((idx = Interlocked.Increment(ref nextIndex)) < chunks.Count)
                        local += RunChunk(clients[worker], idx);
                    return local;
                }, ct);
            }
            var results = await Task.WhenAll(tasks);
            return results.Sum();
        }
        finally
        {
            foreach (var c in clients) c.Dispose();
        }
    }

    /// <summary>
    /// 書き込み列名を実属性名にマップする(監査列。F-08)。
    /// createdon は作成日時の上書き属性 overriddencreatedon に写す(要 prvOverrideCreatedOnCreatedBy)。
    /// </summary>
    public static string MapWriteColumn(string name)
        => name.Equals("createdon", StringComparison.OrdinalIgnoreCase) ? "overriddencreatedon" : name;

    private Entity BuildEntity(WriteCommand cmd, IReadOnlyDictionary<string, AttributeInfo> attrs, RecordBatch batch, int row)
    {
        var e = new Entity(cmd.Table);
        KeyAttributeCollection? keys = cmd.KeyColumns.Count > 0 ? new KeyAttributeCollection() : null;
        for (var col = 0; col < cmd.Columns.Count; col++)
        {
            var name = cmd.Columns[col];

            if (cmd.IdColumn is not null && name.Equals(cmd.IdColumn, StringComparison.OrdinalIgnoreCase))
            {
                var idRaw = ArrowValue.Get(batch.Column(col), row);
                if (idRaw is not null) e.Id = ToGuid(idRaw);
                continue;
            }

            // 属性型に応じたロスレス変換(Lookup/Choice/Money/DateTime 等)。型情報は入力列名で引く。
            // ResolveNames 有効時は GUID でない Lookup 値をプライマリ名→ID 解決(3.1.4)。
            attrs.TryGetValue(name, out var info);
            var value = ArrowValue.ToAttributeValue(batch.Column(col), row, info, _nameResolver, cmd.Options.ResolveNames);

            // 監査列マッピング(createdon→overriddencreatedon)。実際に設定する属性名を決める。
            var attr = MapWriteColumn(name);

            if (keys is not null && cmd.KeyColumns.Contains(name, StringComparer.OrdinalIgnoreCase))
                keys[name] = value!;

            if (value is not null) e[attr] = value;
        }
        if (keys is not null) e.KeyAttributes = keys;
        return e;
    }

    private static Guid ToGuid(object value) => value switch
    {
        Guid g => g,
        string s => Guid.Parse(s),
        _ => throw new InvalidOperationException($"GUID 列に変換できない値: {value}"),
    };

    /// <summary>
    /// ローカル追跡キー方式(D-14 ③)のプレーン INSERT。行内容ハッシュで作成済みを除外し、
    /// 残りを二分探索分離付きで作成、成功行の(ハッシュ↔GUID)を記録する。失敗行はレポート(F-14)。
    /// </summary>
    private int ExecuteInsertWithTracking(ServiceClient client, WriteCommand cmd,
        List<Entity> chunk, List<string> hashes, long[] rowIds, CheckpointSession session, FailureReport? failures)
    {
        var keepEntities = new List<Entity>(chunk.Count);
        var keepHashes = new List<string>(chunk.Count);
        var keepRowIds = new List<long>(chunk.Count);
        for (var i = 0; i < chunk.Count; i++)
        {
            if (session.IsRowTracked(hashes[i])) continue; // 作成済み → スキップ
            keepEntities.Add(chunk[i]);
            keepHashes.Add(hashes[i]);
            keepRowIds.Add(rowIds[i]);
        }
        if (keepEntities.Count == 0) return 0;

        var created = BulkWithIsolation(cmd.Table, cmd.Columns, keepEntities, keepRowIds.ToArray(), failures,
            sub => CreateMultipleRaw(client, cmd.Table, cmd.Options, sub));
        var recorded = created.Select(kv => (keepHashes[kv.Key], kv.Value)).ToList();
        session.RecordTrackedRows(recorded);
        return created.Count;
    }

    private int ExecuteChunk(ServiceClient client, WriteCommand cmd, List<Entity> chunk, long[] rowIds, FailureReport? failures)
    {
        // rowIds は呼び出し側で用意済み(除外を反映した元入力行番号)
        // Upsert も二分探索で原因行を分離して継続(ContinueOnError 相当。F-14)
        if (cmd.Kind == WriteKind.Upsert)
            return BulkWithIsolation(cmd.Table, cmd.Columns, chunk, rowIds, failures, sub =>
            {
                ExecuteMultipleUpsert(client, cmd.Options, sub);
                return System.Array.Empty<string>();
            }).Count;

        return BulkWithIsolation(cmd.Table, cmd.Columns, chunk, rowIds, failures,
            sub => CreateMultipleRaw(client, cmd.Table, cmd.Options, sub)).Count;
    }

    /// <summary>
    /// バルク書き込み(作成/Upsert)を実行し、失敗時はバッチを二分探索して原因行を分離する
    /// (F-12 縮小 ＋ F-14 原因行/原因列特定)。良い行は書き込み、単一行まで絞れた失敗は
    /// レポートに記録して継続する(collector あり)。Upsert 経路も本メソッドで
    /// ContinueOnError 相当の分離を行う。戻り値: 書き込めた「チャンク内インデックス→発番GUID」。
    /// </summary>
    private Dictionary<int, string> BulkWithIsolation(string table, IReadOnlyList<string> columns,
        List<Entity> entities, long[] rowIds, FailureReport? failures,
        Func<List<Entity>, IReadOnlyList<string>> op)
    {
        var created = new Dictionary<int, string>();

        void Recurse(int lo, int hi)
        {
            var count = hi - lo;
            var sub = entities.GetRange(lo, count);
            try
            {
                var ids = op(sub);
                for (var k = 0; k < count; k++) created[lo + k] = k < ids.Count ? ids[k] : "";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (count > 1)
                {
                    var mid = lo + count / 2;
                    _log.LogWarning("バッチ分割({Table} {Count}件 → {A}+{B}): {Msg}", table, count, mid - lo, hi - mid, ex.Message);
                    Recurse(lo, mid);
                    Recurse(mid, hi);
                }
                else if (failures is not null)
                {
                    var col = DetectFailingColumn(ex, columns, entities[lo]); // 原因列を特定(F-14)
                    // CSV には安全な要約のみ(M-2)。原因列は別列に、全文はサーバーログにのみ残す。
                    failures.Add(rowIds[lo], table, col, ErrorSummary.Safe(ex, _includeErrorMessage));
                    _log.LogWarning("失敗行 row={Row} table={Table} column={Col}: {Msg}", rowIds[lo], table, col ?? "?", ex.Message);
                }
                else throw;
            }
        }

        Recurse(0, entities.Count);
        return created;
    }

    /// <summary>
    /// 単一行失敗のフォールト文言から原因列を推定する(F-14 列名特定)。
    /// 入力列名・実属性名のうちメッセージに含まれる最長一致を返す。特定できなければ null。
    /// </summary>
    private static string? DetectFailingColumn(Exception ex, IReadOnlyList<string> columns, Entity entity)
    {
        var msg = ex.Message;
        if (ex is FaultException<OrganizationServiceFault> fe && fe.Detail?.Message is string dm)
            msg += " " + dm;

        string? best = null;
        void Consider(string name)
        {
            if (!string.IsNullOrEmpty(name) && msg.Contains(name, StringComparison.OrdinalIgnoreCase)
                && (best is null || name.Length > best.Length))
                best = name;
        }
        foreach (var c in columns) Consider(c);       // 入力列名(ユーザー視点)
        foreach (var a in entity.Attributes.Keys) Consider(a); // 実属性名(overriddencreatedon 等)
        return best;
    }

    /// <summary>1 リクエスト分の作成。CreateMultiple(対応時)→ 非対応/失敗は EM。発番 GUID を返す。</summary>
    private IReadOnlyList<string> CreateMultipleRaw(ServiceClient client, string table, WriteOptions options, List<Entity> entities)
    {
        if (!options.ForceExecuteMultiple && _bulkCapability.SupportsCreateMultiple(table))
        {
            try
            {
                var collection = new EntityCollection(entities) { EntityName = table };
                var request = new CreateMultipleRequest { Targets = collection };
                ApplyOptions(request, options);
                // ネットワーク断は指数バックオフ再試行(D-9)。429 等は ServiceClient 内蔵
                var response = (CreateMultipleResponse)Resilience.Execute(() => client.Execute(request), _log);
                return response.Ids.Select(g => g.ToString()).ToArray();
            }
            catch (FaultException<OrganizationServiceFault> ex) when (IsBulkNotSupported(ex))
            {
                _log.LogWarning("CreateMultiple 実行時非対応: {Table} → ExecuteMultiple", table);
            }
        }
        ExecuteMultipleCreate(client, options, entities);
        return System.Array.Empty<string>(); // EM 経路は個別 ID を取得しない
    }

    /// <summary>バイパス制御(F-09)をリクエストの共有パラメータに適用する。既定は非バイパス。</summary>
    public static void ApplyOptions(OrganizationRequest req, WriteOptions o)
    {
        if (!o.Any) return;
        var bypass = new List<string>();
        if (o.BypassSyncPlugins) bypass.Add("CustomSync");
        if (o.BypassAsyncPlugins) bypass.Add("CustomAsync");
        if (bypass.Count > 0) req["BypassBusinessLogicExecution"] = string.Join(",", bypass);
        if (o.BypassFlows) req["SuppressCallbackRegistrationExpanderJob"] = true;
        if (o.SuppressDuplicateDetection) req["SuppressDuplicateDetection"] = true;
    }

    private int ExecuteMultipleUpsert(ServiceClient client, WriteOptions options, List<Entity> entities)
    {
        var requests = new OrganizationRequestCollection();
        foreach (var e in entities)
        {
            var up = new UpsertRequest { Target = e };
            ApplyOptions(up, options);
            requests.Add(up);
        }

        var request = new ExecuteMultipleRequest
        {
            Requests = requests,
            Settings = new ExecuteMultipleSettings { ContinueOnError = false, ReturnResponses = true },
        };
        var response = (ExecuteMultipleResponse)client.Execute(request);

        if (response.IsFaulted)
        {
            var first = response.Responses.FirstOrDefault(r => r.Fault is not null);
            throw new InvalidOperationException(
                $"ExecuteMultiple(Upsert) 内でエラー(要求 #{first?.RequestIndex}): {first?.Fault?.Message}");
        }

        var created = response.Responses.Count(r => r.Response is UpsertResponse ur && ur.RecordCreated);
        _log.LogDebug("ExecuteMultiple(Upsert): {Total} 件(新規 {Created} / 更新 {Updated})",
            entities.Count, created, entities.Count - created);
        return entities.Count;
    }

    /// <summary>
    /// バッチ縮小で解消しうる一時的フォールトか(タイムアウト・過大バッチ)。
    /// CreateWithIsolation は全フォールトを分割するが、本判定はログ/運用診断に用いる。
    /// </summary>
    public static bool IsReducibleFault(FaultException<OrganizationServiceFault> ex)
    {
        var msg = (ex.Detail?.Message ?? ex.Message).ToLowerInvariant();
        return msg.Contains("timeout") || msg.Contains("timed out")
            || msg.Contains("too large") || msg.Contains("maximum") || msg.Contains("size limit")
            || msg.Contains("combined execution time") || msg.Contains("execution time limit");
    }

    private int ExecuteMultipleCreate(ServiceClient client, WriteOptions options, List<Entity> entities)
    {
        var requests = new OrganizationRequestCollection();
        foreach (var e in entities)
        {
            var cr = new CreateRequest { Target = e };
            ApplyOptions(cr, options);
            requests.Add(cr);
        }

        var request = new ExecuteMultipleRequest
        {
            Requests = requests,
            Settings = new ExecuteMultipleSettings { ContinueOnError = false, ReturnResponses = false },
        };
        var response = (ExecuteMultipleResponse)client.Execute(request);

        if (response.IsFaulted)
        {
            var first = response.Responses.FirstOrDefault(r => r.Fault is not null);
            throw new InvalidOperationException(
                $"ExecuteMultiple 内でエラー(要求 #{first?.RequestIndex}): {first?.Fault?.Message}");
        }
        _log.LogDebug("ExecuteMultiple(CreateRequest): {Count} 件作成", entities.Count);
        return entities.Count;
    }

    private static bool IsBulkNotSupported(FaultException<OrganizationServiceFault> ex)
    {
        var msg = ex.Detail?.Message ?? ex.Message;
        return msg.Contains("not support", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("CreateMultiple", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("unknown", StringComparison.OrdinalIgnoreCase);
    }
}
