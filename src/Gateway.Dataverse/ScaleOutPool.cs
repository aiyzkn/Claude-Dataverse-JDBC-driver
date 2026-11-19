using Gateway.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Gateway.Dataverse;

/// <summary>
/// 複数アプリケーションユーザー(SP)によるスケールアウト(4.2.4)の構成読取とピア接続構築。
///
/// 各 SP は Dataverse のサービス保護制限(throttle)予算を個別に持つため、書き込みバッチを
/// 複数 SP に分散すると集約スループットが向上する(準拠: 制限回避の偽装ではなく、正規の
/// 複数アプリケーションユーザーによる水平分散)。接続先 URL は主接続と共有する。
///
/// 設定(`Gateway:ScaleOut:Principals` の配列。認証情報はシークレット等でありログ/永続化禁止):
///   Gateway:ScaleOut:Principals:0:ClientId / ClientSecret / CertThumbprint / TenantId / Auth
///   Gateway:ScaleOut:Principals:1:...
/// 対話型/デバイスコードは共有サーバーで不可のため ClientSecret / Certificate のみ。
/// </summary>
public static class ScaleOutPool
{
    /// <summary>スケールアウト SP の構成(接続先 URL は主接続と共有)。</summary>
    public sealed record PrincipalConfig(AuthKind Auth, string? TenantId, string? ClientId,
        string? ClientSecret, string? CertThumbprint)
    {
        // M-5: record の既定 ToString() が ClientSecret を平文で載せるのを防ぐ(secret は伏字化)。
        public override string ToString()
            => $"PrincipalConfig {{ Auth = {Auth}, TenantId = {TenantId}, ClientId = {ClientId}, "
             + $"ClientSecret = {(string.IsNullOrEmpty(ClientSecret) ? "(none)" : "***")}, CertThumbprint = {CertThumbprint} }}";
    }

    /// <summary>設定から追加 SP 構成を読み取る(未設定なら空)。</summary>
    public static IReadOnlyList<PrincipalConfig> ReadPrincipals(IConfiguration config)
    {
        var list = new List<PrincipalConfig>();
        foreach (var section in config.GetSection("Gateway:ScaleOut:Principals").GetChildren())
        {
            var authStr = section["Auth"];
            var auth = System.Enum.TryParse<AuthKind>(authStr, ignoreCase: true, out var k) ? k : AuthKind.ClientSecret;
            list.Add(new PrincipalConfig(auth, section["TenantId"], section["ClientId"],
                section["ClientSecret"], section["CertThumbprint"]));
        }
        return list;
    }

    /// <summary>
    /// 追加 SP を認証してピア接続を構築する。primaryUrl は主接続と同一環境。
    /// 対話型/デバイスコードは拒否。1 件でも認証失敗すればそのピアはスキップ(主接続は継続)。
    /// </summary>
    public static List<DataverseConnection> BuildPeers(IConfiguration config, string primaryUrl, ILoggerFactory lf)
    {
        var log = lf.CreateLogger("ScaleOutPool");
        var peers = new List<DataverseConnection>();
        var principals = ReadPrincipals(config);
        if (principals.Count == 0) return peers;

        var useTds = config.GetValue<bool?>("Gateway:UseTdsEndpoint");
        var auth = new DataverseAuthenticator(lf.CreateLogger<DataverseAuthenticator>());
        foreach (var (p, i) in principals.Select((p, i) => (p, i)))
        {
            if (p.Auth is AuthKind.Interactive or AuthKind.DeviceCode)
            {
                log.LogWarning("スケールアウト SP#{Index} は対話型/デバイスコードのためスキップ(ClientSecret/Certificate のみ対応)", i);
                continue;
            }
            try
            {
                var opts = new AuthOptions
                {
                    Kind = p.Auth, Url = primaryUrl, TenantId = p.TenantId, ClientId = p.ClientId,
                    ClientSecret = p.ClientSecret, CertificateThumbprint = p.CertThumbprint, TokenCacheDirectory = null,
                };
                opts.Validate();
                var client = auth.ConnectAsync(opts).GetAwaiter().GetResult();
                peers.Add(new DataverseConnection(client) { UseTdsEndpoint = useTds });
                log.LogInformation("スケールアウト SP#{Index}(ClientId={ClientId})を接続", i, p.ClientId);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "スケールアウト SP#{Index} の接続に失敗。このピアをスキップ", i);
            }
        }
        return peers;
    }
}
