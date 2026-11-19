using System.Net.Sockets;
using System.ServiceModel;
using Gateway.Dataverse;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gateway.Tests;

/// <summary>ネットワーク断の指数バックオフ再試行(D-9)。</summary>
public class ResilienceTests
{
    [Fact]
    public void Socket_and_timeout_and_comm_are_transient()
    {
        Assert.True(Resilience.IsTransientNetwork(new SocketException()));
        Assert.True(Resilience.IsTransientNetwork(new TimeoutException()));
        Assert.True(Resilience.IsTransientNetwork(new CommunicationException("channel faulted")));
        // 内包例外も辿る
        Assert.True(Resilience.IsTransientNetwork(new InvalidOperationException("wrap", new SocketException())));
    }

    [Fact]
    public void Business_exceptions_are_not_transient()
    {
        Assert.False(Resilience.IsTransientNetwork(new InvalidOperationException("attribute not found")));
        Assert.False(Resilience.IsTransientNetwork(new ArgumentException("bad")));
    }

    [Fact]
    public void Fault_exceptions_are_not_transient_even_though_they_derive_from_CommunicationException()
    {
        // 検証/業務フォールトはサーバー到達済み → ネットワーク障害として再試行してはならない
        var fault = new FaultException<Microsoft.Xrm.Sdk.OrganizationServiceFault>(
            new Microsoft.Xrm.Sdk.OrganizationServiceFault { Message = "length exceeded maximum" },
            "length exceeded maximum");
        Assert.IsAssignableFrom<CommunicationException>(fault); // 継承関係の確認
        Assert.False(Resilience.IsTransientNetwork(fault));     // だが非ネットワーク扱い
    }

    [Fact]
    public void Backoff_is_exponential_and_capped_at_60s()
    {
        Assert.Equal(2, Resilience.BackoffDelay(1).TotalSeconds);
        Assert.Equal(4, Resilience.BackoffDelay(2).TotalSeconds);
        Assert.Equal(64 > 60 ? 60 : 64, Resilience.BackoffDelay(6).TotalSeconds); // 2^6=64→上限60
        Assert.Equal(60, Resilience.BackoffDelay(10).TotalSeconds);
    }

    [Fact]
    public void Retries_transient_then_succeeds()
    {
        var attempts = 0;
        var result = Resilience.Execute(() =>
        {
            attempts++;
            if (attempts < 3) throw new TimeoutException();
            return "ok";
        }, NullLogger.Instance, maxAttempts: 5, delayFor: _ => TimeSpan.Zero);
        Assert.Equal("ok", result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public void Non_transient_is_thrown_immediately()
    {
        var attempts = 0;
        Assert.Throws<ArgumentException>(() => Resilience.Execute<string>(() =>
        {
            attempts++;
            throw new ArgumentException("permanent");
        }, NullLogger.Instance, delayFor: _ => TimeSpan.Zero));
        Assert.Equal(1, attempts); // リトライしない
    }
}
