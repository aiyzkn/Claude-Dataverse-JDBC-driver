using Gateway.Core;

namespace Gateway.Host;

/// <summary>環境変数から AuthOptions を組み立てる。認証情報は環境変数/OS ストアのみ(コミット禁止)。</summary>
public static class AuthOptionsFactory
{
    public static AuthOptions FromEnvironment()
    {
        var kindStr = Environment.GetEnvironmentVariable("DATAVERSE_AUTH") ?? "ClientSecret";
        var kind = Enum.TryParse<AuthKind>(kindStr, ignoreCase: true, out var k) ? k : AuthKind.ClientSecret;

        return new AuthOptions
        {
            Kind = kind,
            Url = Environment.GetEnvironmentVariable("DATAVERSE_URL") ?? "",
            TenantId = Environment.GetEnvironmentVariable("DATAVERSE_TENANT_ID"),
            ClientId = Environment.GetEnvironmentVariable("DATAVERSE_CLIENT_ID"),
            ClientSecret = Environment.GetEnvironmentVariable("DATAVERSE_CLIENT_SECRET"),
            CertificateThumbprint = Environment.GetEnvironmentVariable("DATAVERSE_CERT_THUMBPRINT"),
            TokenCacheDirectory = Environment.GetEnvironmentVariable("DATAVERSE_TOKEN_CACHE_DIR")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DataverseGW", "tokencache"),
        };
    }
}
