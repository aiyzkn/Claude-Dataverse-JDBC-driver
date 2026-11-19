using Gateway.Dataverse;
using Gateway.Flight;
using Gateway.Host;
using Gateway.Host.Lifecycle;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Serilog;
using Serilog.Extensions.Logging;

// Phase 1 S1: 製品ゲートウェイのホスト。認証(F-01)→ Dataverse 接続 →
// Flight SQL サーバー(localhost)+ ライフサイクル(多重起動防止・アイドル終了・プロセス認証トークン)。

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Information)
    .WriteTo.Console()
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog();
    // 4.2.2a 常駐サービス化: Windows サービスとして起動された場合は SCM ライフタイムを使う
    // (コンソール実行時は無害。サービス実行時は自動起動・常駐)。
    builder.Host.UseWindowsService(o => o.ServiceName = "DataverseGW");

    var port = builder.Configuration.GetValue<int?>("Gateway:Port") ?? 32010;

    // CLI サブコマンド(setup-*/dvload)はポートを使わない直接実行。稼働中ゲートウェイと衝突させない
    // ため多重起動ガード・Kestrel バインドをスキップする。
    var isCliTool = args.Contains("setup-nn") || args.Contains("setup-altkey")
        || args.Contains("setup-elastic") || args.Contains("dvload");

    SingleInstanceGuard? guard = null;
    if (!isCliTool)
    {
        guard = SingleInstanceGuard.Acquire(port); // 多重起動防止(ローカルサイドカーは 1 プロセス)
        if (!guard.IsPrimary)
        {
            Log.Warning("ポート {Port} のゲートウェイは既に起動しています。二重起動を中止します。", port);
            guard.Dispose();
            return 0;
        }

        // バインド(4.2.2c): 既定 localhost。any/<IP> でネットワーク公開。
        var bind = builder.Configuration.GetValue<string>("Gateway:Bind") ?? "localhost";
        var isNetworkBind = !(bind.Equals("localhost", StringComparison.OrdinalIgnoreCase) || bind == "127.0.0.1");
        // TLS(4.2.2c): PFX パス or 証明書ストア拇印。設定時は h2 over TLS。
        var tlsCert = LoadTlsCertificate(builder.Configuration, Log.Logger);

        builder.WebHost.ConfigureKestrel(k =>
        {
            void Configure(Microsoft.AspNetCore.Server.Kestrel.Core.ListenOptions l)
            {
                l.Protocols = HttpProtocols.Http2;
                if (tlsCert is not null) l.UseHttps(tlsCert);
            }
            if (!isNetworkBind) k.ListenLocalhost(port, Configure);
            else if (bind.Equals("any", StringComparison.OrdinalIgnoreCase) || bind == "0.0.0.0") k.ListenAnyIP(port, Configure);
            else k.Listen(System.Net.IPAddress.Parse(bind), port, Configure);
        });

        Log.Information("バインド: {Bind} / TLS={Tls}", bind, tlsCert is not null);
        // セキュリティ(fail-closed): ネットワーク公開時は平文・無認証を許さず起動を拒否する。
        // localhost(ループバック)は従来どおり TLS/トークン任意(機密は端末外に出ない)。
        if (isNetworkBind)
        {
            if (tlsCert is null)
                throw new InvalidOperationException(
                    $"ネットワーク公開(Bind={bind})には TLS が必須です。Gateway:Tls:CertPath(+CertPassword) " +
                    "または Gateway:Tls:Thumbprint を設定してください(平文でのトークン/資格情報/データ送信を防止)。");
            if (!(builder.Configuration.GetValue<bool?>("Gateway:RequireToken") ?? false))
                throw new InvalidOperationException(
                    $"ネットワーク公開(Bind={bind})時は Gateway:RequireToken=true が必須です(無認証アクセスを防止)。");
        }
    }

    // 認証 → Dataverse 接続。起動時 DATAVERSE_URL があれば既定コンテキストを確立する。
    // B2(4.2.2c): クライアントが接続ヘッダ(x-dataverse-url 等)で接続先を供給する場合、起動時接続は任意。
    var bootstrapLog = new SerilogLoggerFactory(Log.Logger);
    var hasStartupUrl = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DATAVERSE_URL"));
    DataverseConnection? connection = null;
    if (hasStartupUrl)
    {
        var options = AuthOptionsFactory.FromEnvironment();
        var authenticator = new DataverseAuthenticator(bootstrapLog.CreateLogger<DataverseAuthenticator>());
        var serviceClient = await authenticator.ConnectAsync(options);
        connection = new DataverseConnection(serviceClient)
        {
            // 読み取り経路(F-15/D-16): 未設定=自動、false=FetchXML 強制(TDS 無効環境)、true=TDS 強制
            UseTdsEndpoint = builder.Configuration.GetValue<bool?>("Gateway:UseTdsEndpoint"),
        };
        Log.Information("読み取り経路: UseTdsEndpoint={Mode}",
            connection.UseTdsEndpoint?.ToString() ?? "auto");

        // スケールアウト(4.2.4): 追加アプリケーションユーザー(SP)を認証し、書き込み分散用ピアとして主接続に付与。
        var peers = ScaleOutPool.BuildPeers(builder.Configuration,
            connection.Client.ConnectedOrgUriActual?.ToString() ?? Environment.GetEnvironmentVariable("DATAVERSE_URL")!,
            bootstrapLog);
        if (peers.Count > 0)
        {
            connection.ScaleOutPeers = peers;
            Log.Information("スケールアウト有効: 主+{Peers} SP({Total} SP)で書き込みを分散", peers.Count, peers.Count + 1);
        }
    }
    else
    {
        Log.Information("起動時 DATAVERSE_URL 未設定。接続先はクライアント供給ヘッダ(x-dataverse-url 等)で受け付けます(4.2.2c B2)。");
    }

    // CLI: 検証用 N:N リレーションシップを作成して終了(dotnet run -- setup-nn)
    if (args.Contains("setup-nn"))
    {
        if (connection is null) throw new InvalidOperationException("setup-nn には DATAVERSE_URL が必要です。");
        var rel = NnRelationshipSetup.EnsureManyToMany(connection,
            bootstrapLog.CreateLogger("NnSetup"), "poc_gadget", "poc_widget", "poc_gadget_poc_widget", "poc");
        Log.Information("setup-nn 完了: {Rel}", rel);
        return 0;
    }

    // CLI: 検証用エラスティックテーブルを作成して終了(dotnet run -- setup-elastic)。4.2.3 DeleteMultiple 検証用。
    if (args.Contains("setup-elastic"))
    {
        if (connection is null) throw new InvalidOperationException("setup-elastic には DATAVERSE_URL が必要です。");
        var name = ElasticTableSetup.EnsureElastic(connection, bootstrapLog.CreateLogger("ElasticSetup"),
            "poc_sensordata", "poc_name");
        Log.Information("setup-elastic 完了: {Table}", name);
        return 0;
    }

    // CLI: 検証用に account へ代替キー(accountnumber)を作成して終了(dotnet run -- setup-altkey)
    if (args.Contains("setup-altkey"))
    {
        if (connection is null) throw new InvalidOperationException("setup-altkey には DATAVERSE_URL が必要です。");
        AltKeySetup.EnsureAlternateKey(connection, bootstrapLog.CreateLogger("AltKeySetup"),
            "account", "accountnumber", "poc_accountnumber_key");
        Log.Information("setup-altkey 完了");
        return 0;
    }

    // プロセス間認証トークン(生成・永続化)。強制は Gateway:RequireToken(既定 false)
    var token = GatewayToken.CreateAndPersist();
    var requireToken = builder.Configuration.GetValue<bool?>("Gateway:RequireToken") ?? false;

    // チェックポイントストア(F-11)。既定は %LOCALAPPDATA% 配下、Gateway:CheckpointDb で上書き可
    var checkpointPath = builder.Configuration.GetValue<string?>("Gateway:CheckpointDb")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DataverseGW", "checkpoints.db");
    var checkpointStore = new Gateway.Checkpoint.CheckpointStore(checkpointPath);

    var auditDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DataverseGW", "audit");

    // 既定コンテキスト(4.2.2 B2): 起動時接続があれば接続・キャッシュ・本番ガード・各エグゼキュータを束ねる。
    // 接続先(URL/認証)ごとの他コンテキストは DataverseContextRegistry が接続ヘッダを受けて動的生成する。
    DataverseContext? defaultContext = connection is not null
        ? new DataverseContext(connection, builder.Configuration, checkpointStore, bootstrapLog)
        : null;

    builder.Services.AddSingleton(checkpointStore);
    builder.Services.AddSingleton(sp => new AuditLog(auditDir, sp.GetRequiredService<ILoggerFactory>().CreateLogger<AuditLog>()));
    builder.Services.AddSingleton<SessionRegistry>(); // F-09/4.2.2b 接続(クライアント)単位のセッション
    builder.Services.AddSingleton<DryRunAnalyzer>(); // 3.1.1 実行前ドライラン
    builder.Services.AddSingleton<ProgressTracker>(); // 3.1.5 進捗トラッキング
    builder.Services.AddSingleton<StatementRegistry>();
    // 4.2.2c B2: 接続(クライアント供給の接続先)ごとに DataverseContext を生成・キャッシュ。既定は起動時接続。
    builder.Services.AddSingleton(new DataverseContextRegistry(
        builder.Configuration, checkpointStore, bootstrapLog, defaultContext));
    builder.Services.AddSingleton<ActivityTracker>();
    builder.Services.AddSingleton(token);
    builder.Services.AddHostedService<IdleShutdownService>();
    builder.Services.AddHostedService<SleepPreventer>(); // 実行中の OS スリープ抑止(D-9)

    builder.Services
        .AddGrpc(o =>
        {
            o.MaxReceiveMessageSize = null;
            o.MaxSendMessageSize = null;
            // 詳細エラーの外部返却は既定 OFF(M-4: 例外に接続文字列断片や secret が混入し得るため)。
            // 開発時のみ Gateway:EnableDetailedErrors=true で明示的に有効化する。
            o.EnableDetailedErrors = builder.Configuration.GetValue<bool?>("Gateway:EnableDetailedErrors") ?? false;
        })
        .AddFlightServer<FlightSqlService>();

    var app = builder.Build();

    // CLI: CSV/Excel 直接ローダー(dvload。3.1.3)。サーバーを起動せず DI から書き込みパイプラインを
    // 解決して実行し、終了する。本番ガード・チェックポイント・失敗レポート・ドライランを再利用。
    if (args.Contains("dvload"))
    {
        if (defaultContext is null) throw new InvalidOperationException("dvload には DATAVERSE_URL が必要です。");
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return await DvLoadCommand.RunAsync(
            DvLoadCommand.Parse(args),
            defaultContext.Inserter,
            app.Services.GetRequiredService<DryRunAnalyzer>(),
            defaultContext.Guard,
            app.Services.GetRequiredService<AuditLog>(),
            app.Services.GetRequiredService<ProgressTracker>(),
            Path.Combine(localAppData, "DataverseGW", "reports"),
            Path.Combine(localAppData, "DataverseGW", "dryrun"),
            bootstrapLog.CreateLogger("dvload"), CancellationToken.None,
            builder.Configuration.GetValue<int?>("Gateway:ReportRetentionDays") ?? 0,
            (long)(builder.Configuration.GetValue<int?>("Gateway:MaxInputFileMB") ?? 0) * 1024 * 1024);
    }

    // アイドル判定用のアクティビティ追跡＋(有効時)プロセストークン検証
    var tracker = app.Services.GetRequiredService<ActivityTracker>();
    app.Use(async (ctx, next) =>
    {
        tracker.Touch();
        if (requireToken && !token.Matches(ctx.Request.Headers[GatewayToken.HeaderName].ToString())) // L-3: 定数時間比較
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsync("gateway token required");
            return;
        }
        await next();
    });

    app.MapFlightEndpoint();

    Log.Information("Gateway listening on grpc://localhost:{Port} (Arrow Flight SQL)", port);
    Log.Information("JDBC URL: jdbc:arrow-flight-sql://localhost:{Port}/?useEncryption=false", port);
    Log.Information("プロセストークン: {Path} (強制={Require})", token.FilePath, requireToken);
    app.Run();
    guard?.Dispose();
    return 0;
}
catch (Exception ex) when (IsAddressInUse(ex))
{
    // ポート二重 bind(既に別インスタンスが稼働)。Windows は名前付き Mutex で事前に弾くが、
    // macOS/Linux では Mutex が効かずここに到達しうる。全 OS で友好的メッセージ＋正常終了に統一(3.2.1)。
    Log.Warning("ポートは既に使用中です。ゲートウェイは既に起動しているため二重起動を中止します。");
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "gateway terminated unexpectedly");
    return 1;
}
finally
{
    Log.CloseAndFlush();
}

// TLS 証明書を読み込む(4.2.2c)。CertPath(PFX)優先、無ければ Thumbprint(LocalMachine\My)。未設定/失敗は null(TLS 無効)。
static System.Security.Cryptography.X509Certificates.X509Certificate2? LoadTlsCertificate(
    Microsoft.Extensions.Configuration.IConfiguration config, Serilog.ILogger log)
{
    var path = config.GetValue<string>("Gateway:Tls:CertPath");
    var pw = config.GetValue<string>("Gateway:Tls:CertPassword");
    var thumb = config.GetValue<string>("Gateway:Tls:Thumbprint");
    try
    {
        if (!string.IsNullOrEmpty(path))
            return string.IsNullOrEmpty(pw)
                ? new System.Security.Cryptography.X509Certificates.X509Certificate2(path)
                : new System.Security.Cryptography.X509Certificates.X509Certificate2(path, pw);
        if (!string.IsNullOrEmpty(thumb))
        {
            using var store = new System.Security.Cryptography.X509Certificates.X509Store(
                System.Security.Cryptography.X509Certificates.StoreName.My,
                System.Security.Cryptography.X509Certificates.StoreLocation.LocalMachine);
            store.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadOnly);
            var found = store.Certificates.Find(
                System.Security.Cryptography.X509Certificates.X509FindType.FindByThumbprint, thumb.Replace(" ", ""), false);
            if (found.Count > 0) return found[0];
            log.Warning("TLS 証明書(拇印 {Thumb})が LocalMachine\\My に見つかりません", thumb);
        }
    }
    catch (Exception ex)
    {
        log.Warning(ex, "TLS 証明書の読み込みに失敗。TLS 無効で継続します");
    }
    return null;
}

// 例外チェーンに「アドレス使用中」が含まれるか(ポート二重 bind の判定)
static bool IsAddressInUse(Exception ex)
{
    for (var e = ex; e is not null; e = e.InnerException)
    {
        if (e is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.AddressAlreadyInUse })
            return true;
        if (e.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
            || e.Message.Contains("Failed to bind", StringComparison.OrdinalIgnoreCase))
            return true;
    }
    return false;
}
