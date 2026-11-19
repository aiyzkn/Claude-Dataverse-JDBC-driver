using Gateway.Core;
using Gateway.Dataverse;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gateway.Tests;

/// <summary>接続(クライアント供給の接続先)ごとのコンテキスト解決(4.2.2c B2)。</summary>
public class DataverseContextRegistryTests
{
    private static DataverseContextRegistry New()
        => new(new ConfigurationBuilder().Build(), checkpoint: null, NullLoggerFactory.Instance, defaultContext: null);

    private static DataverseContextRegistry.ConnParams P(
        string url = "https://a.crm.dynamics.com", AuthKind auth = AuthKind.ClientSecret,
        string? tenant = "t", string? client = "c", string? secret = "s", string? cert = null)
        => new(url, auth, tenant, client, secret, cert);

    [Fact]
    public void Same_params_produce_same_key()
        => Assert.Equal(P().Key(), P().Key());

    [Theory]
    [InlineData("https://b.crm.dynamics.com", AuthKind.ClientSecret, "t", "c", "s", null)] // URL 違い
    [InlineData("https://a.crm.dynamics.com", AuthKind.Certificate, "t", "c", "s", null)]  // 認証種別違い
    [InlineData("https://a.crm.dynamics.com", AuthKind.ClientSecret, "t2", "c", "s", null)] // テナント違い
    [InlineData("https://a.crm.dynamics.com", AuthKind.ClientSecret, "t", "c2", "s", null)] // クライアント違い
    [InlineData("https://a.crm.dynamics.com", AuthKind.ClientSecret, "t", "c", "s2", null)] // シークレット違い
    [InlineData("https://a.crm.dynamics.com", AuthKind.ClientSecret, "t", "c", "s", "TH")]  // 証明書違い
    public void Different_params_produce_different_key(
        string url, AuthKind auth, string tenant, string client, string secret, string? cert)
        => Assert.NotEqual(P().Key(), P(url, auth, tenant, client, secret, cert).Key());

    [Fact]
    public void Interactive_auth_is_rejected_for_shared_server()
        => Assert.Throws<InvalidOperationException>(() => New().Get(P(auth: AuthKind.Interactive)));

    [Fact]
    public void DeviceCode_auth_is_rejected_for_shared_server()
        => Assert.Throws<InvalidOperationException>(() => New().Get(P(auth: AuthKind.DeviceCode)));

    [Fact]
    public void Default_is_null_when_no_startup_connection()
        => Assert.Null(New().Default);
}
