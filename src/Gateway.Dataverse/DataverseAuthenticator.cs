using Gateway.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using Microsoft.PowerPlatform.Dataverse.Client;

namespace Gateway.Dataverse;

/// <summary>
/// F-01: MSAL の各フロー(クライアントシークレット/証明書/対話型/デバイスコード)で
/// ServiceClient を確立する。対話型/デバイスコードはトークンをキャッシュし、
/// 長時間ジョブが無断絶となるようにする。
///
/// PoC からの発展点: デバイスコードは ServiceClient のトークンプロバイダー経由で
/// MSAL PublicClientApplication を直接利用(接続文字列では表現できないため)。
/// </summary>
public sealed class DataverseAuthenticator
{
    // 対話型ログイン検証用に Microsoft が公開しているサンプル AppId(自前 AppId が無い対話利用の既定)
    private const string DefaultInteractiveAppId = "51f81489-12ee-4a9e-aaae-a2591f45987d";
    private const string DataverseDefaultScopeSuffix = "/.default";

    private readonly ILogger<DataverseAuthenticator> _log;

    public DataverseAuthenticator(ILogger<DataverseAuthenticator> log) => _log = log;

    public async Task<ServiceClient> ConnectAsync(AuthOptions options, CancellationToken ct = default)
    {
        options.Validate();
        if (options.IsInteractivePrincipal)
            _log.LogWarning("対話型/デバイスコード認証: 監査列(createdby 等)には実行ユーザーが記録されます。SP 実行との違いに注意してください。");

        var client = options.Kind switch
        {
            AuthKind.ClientSecret => new ServiceClient(BuildConnectionString(options,
                $"AuthType=ClientSecret;ClientId={options.ClientId};ClientSecret={options.ClientSecret}")),
            AuthKind.Certificate => new ServiceClient(BuildConnectionString(options,
                $"AuthType=Certificate;ClientId={options.ClientId};Thumbprint={options.CertificateThumbprint};StoreName=My")),
            AuthKind.Interactive => await ConnectPublicClientAsync(options, interactive: true, ct),
            AuthKind.DeviceCode => await ConnectPublicClientAsync(options, interactive: false, ct),
            _ => throw new NotSupportedException($"未対応の認証方式: {options.Kind}"),
        };

        if (!client.IsReady)
        {
            // M-4: LastError/LastException は接続文字列断片(ClientSecret 等)を含み得るため、
            // クライアントへは固定文言のみ返す。サーバーログにも secret を残さないよう、LastError と
            // 例外メッセージの双方を伏字化して記録する(生の例外オブジェクトは渡さない=メッセージが素通りしない)。
            var exType = client.LastException?.GetType().Name ?? "(none)";
            _log.LogError("Dataverse 接続に失敗: {Detail} / 例外={ExType}: {ExMsg}",
                Redact(client.LastError, options), exType, Redact(client.LastException?.Message, options));
            throw new InvalidOperationException(
                "Dataverse 接続に失敗しました。接続先 URL・認証情報・ネットワーク到達性を確認してください(詳細はサーバーログを参照)。");
        }

        // 並列スループット向上(要件定義書 3.2 / OBS-7)
        client.EnableAffinityCookie = false;
        _log.LogInformation("Dataverse 接続成功: {Org} / 認証={Kind} / 推奨並列度={Dop}",
            client.ConnectedOrgFriendlyName, options.Kind, client.RecommendedDegreesOfParallelism);
        return client;
    }

    private static string BuildConnectionString(AuthOptions o, string authPart)
        => $"Url={o.Url};{authPart};RequireNewInstance=true";

    /// <summary>サーバーログ用に、既知の secret(ClientSecret)を伏字化する(M-4)。</summary>
    private static string Redact(string? message, AuthOptions o)
    {
        if (string.IsNullOrEmpty(message)) return "(詳細なし)";
        return string.IsNullOrEmpty(o.ClientSecret) ? message : message.Replace(o.ClientSecret, "***");
    }

    /// <summary>
    /// 対話型フロー(対話型ログイン / デバイスコード)を MSAL PublicClientApplication で確立する。
    /// 両者は取得手段(ブラウザ対話 / デバイスコード)だけが異なり、**トークンキャッシュは共通の
    /// OS ネイティブ保護(Windows=DPAPI / macOS=Keychain / Linux=libsecret)で永続化**する(3.2.1)。
    /// これにより対話型も含めて Keychain 保護で一貫し、平文ファイルのキャッシュ経路を排除する。
    /// </summary>
    private async Task<ServiceClient> ConnectPublicClientAsync(AuthOptions o, bool interactive, CancellationToken ct)
    {
        var appId = string.IsNullOrWhiteSpace(o.ClientId) ? DefaultInteractiveAppId : o.ClientId!;
        var app = PublicClientApplicationBuilder
            .Create(appId)
            .WithAuthority(AzureCloudInstance.AzurePublic, o.TenantId)
            .WithRedirectUri("http://localhost")
            .Build();

        // トークンキャッシュの OS ネイティブ保護での永続化(指定時)。未指定ならプロセス内のみ。
        // 対話型/デバイスコードで同一キャッシュを共有(SSO)。macOS では Keychain に保存される。
        if (!string.IsNullOrWhiteSpace(o.TokenCacheDirectory))
            TokenCachePersistence.Enable(app.UserTokenCache, o.TokenCacheDirectory!, "msal_cache");

        var scope = new[] { new Uri(o.Url).GetLeftPart(UriPartial.Authority) + DataverseDefaultScopeSuffix };

        async Task<string> TokenProvider(string _)
        {
            var accounts = await app.GetAccountsAsync();
            try
            {
                var silent = await app.AcquireTokenSilent(scope, accounts.FirstOrDefault()).ExecuteAsync(ct);
                return silent.AccessToken;
            }
            catch (MsalUiRequiredException)
            {
                AuthenticationResult result;
                if (interactive)
                {
                    // 既定ブラウザで対話型サインイン(localhost リダイレクト)
                    result = await app.AcquireTokenInteractive(scope).ExecuteAsync(ct);
                }
                else
                {
                    result = await app.AcquireTokenWithDeviceCode(scope, dc =>
                    {
                        // ユーザーにコードと URL を提示(標準出力へ)
                        _log.LogInformation("デバイスコード認証: {Message}", dc.Message);
                        Console.WriteLine(dc.Message);
                        return Task.CompletedTask;
                    }).ExecuteAsync(ct);
                }
                return result.AccessToken;
            }
        }

        return new ServiceClient(new Uri(o.Url), TokenProvider, useUniqueInstance: true, logger: null);
    }
}
