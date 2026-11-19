using Gateway.Core;
using Gateway.Dataverse;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Gateway.Tests;

/// <summary>複数アプリケーションユーザー(SP)スケールアウトの設定読取(4.2.4)。</summary>
public class ScaleOutPoolTests
{
    private static IConfiguration Config(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void No_principals_configured_returns_empty()
        => Assert.Empty(ScaleOutPool.ReadPrincipals(Config(new())));

    [Fact]
    public void Reads_multiple_principals_in_order()
    {
        var config = Config(new()
        {
            ["Gateway:ScaleOut:Principals:0:ClientId"] = "app-0",
            ["Gateway:ScaleOut:Principals:0:ClientSecret"] = "sec-0",
            ["Gateway:ScaleOut:Principals:0:TenantId"] = "t",
            ["Gateway:ScaleOut:Principals:1:ClientId"] = "app-1",
            ["Gateway:ScaleOut:Principals:1:Auth"] = "Certificate",
            ["Gateway:ScaleOut:Principals:1:CertThumbprint"] = "AB12",
            ["Gateway:ScaleOut:Principals:1:TenantId"] = "t",
        });

        var principals = ScaleOutPool.ReadPrincipals(config);

        Assert.Equal(2, principals.Count);
        Assert.Equal("app-0", principals[0].ClientId);
        Assert.Equal(AuthKind.ClientSecret, principals[0].Auth);   // 既定
        Assert.Equal("sec-0", principals[0].ClientSecret);
        Assert.Equal("app-1", principals[1].ClientId);
        Assert.Equal(AuthKind.Certificate, principals[1].Auth);    // 明示
        Assert.Equal("AB12", principals[1].CertThumbprint);
    }

    [Fact]
    public void Unknown_auth_defaults_to_client_secret()
    {
        var config = Config(new()
        {
            ["Gateway:ScaleOut:Principals:0:ClientId"] = "app-x",
            ["Gateway:ScaleOut:Principals:0:Auth"] = "nonsense",
        });
        Assert.Equal(AuthKind.ClientSecret, ScaleOutPool.ReadPrincipals(config)[0].Auth);
    }
}
