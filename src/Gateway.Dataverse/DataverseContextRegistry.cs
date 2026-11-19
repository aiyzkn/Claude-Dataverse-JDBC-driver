using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Gateway.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Gateway.Dataverse;

/// <summary>
/// 接続(クライアント供給の接続先)ごとに <see cref="DataverseContext"/> を生成・キャッシュする(4.2.2 B2)。
/// クライアントが gRPC ヘッダで接続先 URL・認証を供給し、ゲートウェイは環境ごとに接続を確立して分離する。
/// 起動時 `DATAVERSE_URL` が設定されていればそれを既定コンテキスト(無ヘッダ時のフォールバック)とする。
/// B2 の共有サーバーではブラウザ対話ができないため **ClientSecret / Certificate のみ**対応。
/// </summary>
public sealed class DataverseContextRegistry
{
    /// <summary>クライアント供給の接続パラメータ(gRPC ヘッダ由来)。</summary>
    public sealed record ConnParams(string Url, AuthKind Auth, string? TenantId, string? ClientId,
        string? ClientSecret, string? CertThumbprint)
    {
        /// <summary>キャッシュキー(秘密を含めてハッシュ化。平文は保持しない)。</summary>
        public string Key()
            => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{Url}|{Auth}|{TenantId}|{ClientId}|{CertThumbprint}|{ClientSecret}")));

        // M-5: record の既定 ToString() は全プロパティを列挙し ClientSecret を平文で載せる。
        // 誤って文字列化された場合の漏洩を防ぐため、secret を出さない表現へ明示オーバーライド。
        public override string ToString()
            => $"ConnParams {{ Url = {Url}, Auth = {Auth}, TenantId = {TenantId}, ClientId = {ClientId}, "
             + $"CertThumbprint = {CertThumbprint}, ClientSecret = {(string.IsNullOrEmpty(ClientSecret) ? "(none)" : "***")} }}";
    }

    private readonly IConfiguration _config;
    private readonly Gateway.Checkpoint.CheckpointStore? _checkpoint;
    private readonly ILoggerFactory _lf;
    private readonly ILogger<DataverseContextRegistry> _log;
    private readonly ConcurrentDictionary<string, Lazy<DataverseContext>> _ctx = new();

    public DataverseContextRegistry(IConfiguration config, Gateway.Checkpoint.CheckpointStore? checkpoint,
        ILoggerFactory lf, DataverseContext? defaultContext)
    {
        _config = config;
        _checkpoint = checkpoint;
        _lf = lf;
        _log = lf.CreateLogger<DataverseContextRegistry>();
        Default = defaultContext;
    }

    /// <summary>既定コンテキスト(起動時 DATAVERSE_URL)。未設定なら null。</summary>
    public DataverseContext? Default { get; }

    /// <summary>接続パラメータからコンテキストを取得(無ければ認証して生成・キャッシュ)。</summary>
    public DataverseContext Get(ConnParams p)
        => _ctx.GetOrAdd(p.Key(),
            _ => new Lazy<DataverseContext>(() => Build(p), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    private DataverseContext Build(ConnParams p)
    {
        if (p.Auth is AuthKind.Interactive or AuthKind.DeviceCode)
            throw new InvalidOperationException(
                "B2(クライアント供給接続)では対話型/デバイスコード認証は非対応です。ClientSecret か Certificate を指定してください。");

        var opts = new AuthOptions
        {
            Kind = p.Auth, Url = p.Url, TenantId = p.TenantId, ClientId = p.ClientId,
            ClientSecret = p.ClientSecret, CertificateThumbprint = p.CertThumbprint, TokenCacheDirectory = null,
        };
        opts.Validate();
        _log.LogInformation("接続コンテキストを生成: {Url} (認証={Auth})", p.Url, p.Auth);
        var auth = new DataverseAuthenticator(_lf.CreateLogger<DataverseAuthenticator>());
        var client = auth.ConnectAsync(opts).GetAwaiter().GetResult();
        var conn = new DataverseConnection(client) { UseTdsEndpoint = _config.GetValue<bool?>("Gateway:UseTdsEndpoint") };
        return new DataverseContext(conn, _config, _checkpoint, _lf);
    }
}
