using Gateway.Dataverse;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gateway.Tests;

/// <summary>本番環境ガード(3.1.2): 検出・既定ブロック・セッション許可(4.2.2b)。</summary>
public class ProductionGuardTests
{
    private static ProductionGuard Prod(bool allow = false)
        => new(isProduction: true, targetHost: "org-prod.crm.dynamics.com", allowProductionWrites: allow,
               NullLogger<ProductionGuard>.Instance);

    private static ProductionGuard NonProd()
        => new(isProduction: false, targetHost: "org12345.crm.dynamics.com", allowProductionWrites: false,
               NullLogger<ProductionGuard>.Instance);

    [Theory]
    [InlineData("org-prod.crm.dynamics.com", true)]
    [InlineData("orgprd.crm7.dynamics.com", true)]
    [InlineData("myproduction.crm.dynamics.com", true)]
    [InlineData("org12345.crm.dynamics.com", false)]
    [InlineData("contoso-dev.crm.dynamics.com", false)]
    [InlineData("", false)]
    public void LooksLikeProduction_detects_prod_tokens(string host, bool expected)
        => Assert.Equal(expected, ProductionGuard.LooksLikeProduction(host));

    [Fact]
    public void Non_production_allows_writes_regardless_of_session()
    {
        var g = NonProd();
        Assert.False(g.IsProduction);
        g.EnsureWriteAllowed("INSERT", "account", sessionAllows: false); // 非本番は常に許可(例外にならない)
    }

    [Fact]
    public void Production_blocks_writes_when_session_not_allowed()
    {
        var g = Prod();
        Assert.True(g.IsProduction);
        Assert.False(g.DefaultAllow);
        Assert.Throws<System.InvalidOperationException>(() => g.EnsureWriteAllowed("INSERT", "account", sessionAllows: false));
    }

    [Fact]
    public void Production_allows_writes_when_session_allowed()
    {
        var g = Prod();
        g.EnsureWriteAllowed("DELETE", "account", sessionAllows: true); // セッションが許可すれば通る
    }

    [Fact]
    public void Startup_optin_becomes_default_allow()
    {
        Assert.True(Prod(allow: true).DefaultAllow);   // 起動時オプトイン → 新規セッション既定許可
        Assert.False(Prod(allow: false).DefaultAllow);
    }

    [Fact]
    public void Create_explicit_flag_overrides_host_autodetect()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Gateway:IsProduction"] = "false" })
            .Build();
        var g = ProductionGuard.Create("https://org-prod.crm.dynamics.com/", config, NullLogger<ProductionGuard>.Instance);
        Assert.False(g.IsProduction);
        g.EnsureWriteAllowed("INSERT", "account", sessionAllows: false); // 非本番扱いで通る
    }

    [Fact]
    public void Create_autodetects_production_from_host()
    {
        var config = new ConfigurationBuilder().Build();
        var g = ProductionGuard.Create("https://contoso-prod.crm.dynamics.com/", config, NullLogger<ProductionGuard>.Instance);
        Assert.True(g.IsProduction);
        Assert.Throws<System.InvalidOperationException>(() => g.EnsureWriteAllowed("INSERT", "account", sessionAllows: false));
    }

    [Fact]
    public void Create_defaults_remote_dataverse_to_production_when_flag_unset()
    {
        // fail-closed(H-1): 明示設定が無く prod トークンも含まないリモート Dataverse は
        // dev/prod を URL で区別できないため本番扱い(ブロック)に倒す。
        var config = new ConfigurationBuilder().Build();
        var g = ProductionGuard.Create("https://org12345.crm.dynamics.com/", config, NullLogger<ProductionGuard>.Instance);
        Assert.True(g.IsProduction);
        Assert.Throws<System.InvalidOperationException>(() => g.EnsureWriteAllowed("INSERT", "account", sessionAllows: false));
    }

    [Fact]
    public void Create_defaults_local_host_to_non_production()
    {
        // ローカル/その他ホストは非本番既定(検証・ローカルプロキシ用途を妨げない)。
        var config = new ConfigurationBuilder().Build();
        var g = ProductionGuard.Create("http://localhost:12345/", config, NullLogger<ProductionGuard>.Instance);
        Assert.False(g.IsProduction);
        g.EnsureWriteAllowed("INSERT", "account", sessionAllows: false); // 通る(例外にならない)
    }

    [Theory]
    [InlineData("org12345.crm.dynamics.com", true)]
    [InlineData("contoso.crm4.dynamics.com", true)]
    [InlineData("localhost", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("", false)]
    public void IsRemoteDataverse_detects_dynamics_hosts(string host, bool expected)
        => Assert.Equal(expected, ProductionGuard.IsRemoteDataverse(host));
}
