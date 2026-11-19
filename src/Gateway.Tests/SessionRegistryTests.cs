using Gateway.Dataverse;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gateway.Tests;

/// <summary>接続(ピア)単位のセッション分離(4.2.2b サーバー配置)。</summary>
public class SessionRegistryTests
{
    private static SessionRegistry New(bool defaultAllowProduction = false)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:AllowProductionWrites"] = defaultAllowProduction ? "true" : "false",
            })
            .Build();
        return new SessionRegistry(config, NullLoggerFactory.Instance);
    }

    [Fact]
    public void Same_peer_returns_same_session()
    {
        var r = New();
        Assert.Same(r.Get("ipv4:127.0.0.1:5001"), r.Get("ipv4:127.0.0.1:5001"));
    }

    [Fact]
    public void Different_peers_are_isolated()
    {
        var r = New();
        var a = r.Get("ipv4:127.0.0.1:5001");
        var b = r.Get("ipv4:127.0.0.1:5002");
        Assert.NotSame(a, b);

        // ピア A の SET DRY_RUN はピア B に漏れない
        a.TryApply("SET DRY_RUN = ON");
        Assert.True(a.DryRun);
        Assert.False(b.DryRun);

        // 本番許可も接続ごとに独立
        a.TryApply("SET ALLOW_PRODUCTION_WRITES = ON");
        Assert.True(a.AllowProductionWrites);
        Assert.False(b.AllowProductionWrites);
    }

    [Fact]
    public void Default_allow_flows_to_new_sessions()
    {
        var r = New(defaultAllowProduction: true);
        Assert.True(r.Get("peerX").AllowProductionWrites);
    }
}
