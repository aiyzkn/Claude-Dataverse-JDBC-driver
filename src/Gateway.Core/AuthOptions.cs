namespace Gateway.Core;

/// <summary>認証方式(F-01: SP シークレット/証明書＋対話型/デバイスコード)。</summary>
public enum AuthKind
{
    ClientSecret,
    Certificate,
    Interactive,
    DeviceCode,
}

/// <summary>
/// Dataverse 接続・認証の構成。環境変数 / appsettings から束ねる。
/// 認証情報そのものは保持するが、ログ・永続化してはならない(シークレットは OS ストア/環境変数管理)。
/// </summary>
public sealed class AuthOptions
{
    public AuthKind Kind { get; init; } = AuthKind.ClientSecret;
    public string Url { get; init; } = "";
    public string? TenantId { get; init; }
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public string? CertificateThumbprint { get; init; }

    /// <summary>対話型/デバイスコードのトークンキャッシュ保存先(OS クレデンシャルストア相当のパス)。</summary>
    public string? TokenCacheDirectory { get; init; }

    /// <summary>実行主体が対話型ユーザーか(監査列に実行者が記録される旨の警告表示に使う)。</summary>
    public bool IsInteractivePrincipal => Kind is AuthKind.Interactive or AuthKind.DeviceCode;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Url))
            throw new InvalidOperationException("Dataverse URL が未設定です(DATAVERSE_URL)。");
        switch (Kind)
        {
            case AuthKind.ClientSecret when string.IsNullOrWhiteSpace(ClientId) || string.IsNullOrWhiteSpace(ClientSecret):
                throw new InvalidOperationException("ClientSecret 認証には ClientId と ClientSecret が必要です。");
            case AuthKind.Certificate when string.IsNullOrWhiteSpace(ClientId) || string.IsNullOrWhiteSpace(CertificateThumbprint):
                throw new InvalidOperationException("Certificate 認証には ClientId と CertificateThumbprint が必要です。");
            case AuthKind.Interactive or AuthKind.DeviceCode when string.IsNullOrWhiteSpace(TenantId):
                throw new InvalidOperationException("対話型/デバイスコード認証には TenantId が必要です。");
        }
    }
}
