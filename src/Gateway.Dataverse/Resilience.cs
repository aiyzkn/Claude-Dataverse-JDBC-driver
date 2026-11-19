using System.Net;
using System.Net.Sockets;
using System.ServiceModel;
using Microsoft.Extensions.Logging;

namespace Gateway.Dataverse;

/// <summary>
/// ネットワーク断などの一時障害に対する指数バックオフ再試行(D-9)。
/// ServiceClient は 429 等を内蔵リトライするが、接続断・DNS 一時障害はここで補う。
/// 総再試行時間は上限(既定 30 分)まで。
/// </summary>
public static class Resilience
{
    public static T Execute<T>(Func<T> action, ILogger log, int maxAttempts = 12,
        TimeSpan? maxTotal = null, Func<int, TimeSpan>? delayFor = null)
    {
        var budget = maxTotal ?? TimeSpan.FromMinutes(30);
        delayFor ??= BackoffDelay;
        var start = DateTime.UtcNow;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (Exception ex) when (attempt < maxAttempts && IsTransientNetwork(ex) && DateTime.UtcNow - start < budget)
            {
                var delay = delayFor(attempt);
                log.LogWarning("一時的ネットワーク障害({Type})。{Delay}秒後に再試行({Attempt}/{Max}): {Msg}",
                    ex.GetType().Name, delay.TotalSeconds, attempt, maxAttempts, ex.Message);
                Thread.Sleep(delay);
            }
        }
    }

    /// <summary>指数バックオフ(2^n 秒、上限 60 秒)。attempt は 1 始まり。</summary>
    public static TimeSpan BackoffDelay(int attempt)
        => TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(attempt, 6))));

    /// <summary>
    /// 接続断・DNS・タイムアウト等の一時的ネットワーク障害か。
    /// 注意: FaultException&lt;T&gt; は CommunicationException を継承するが、これは
    /// 「サーバーに到達し業務/検証エラーが返った」ことを意味し、ネットワーク障害ではない。
    /// 誤ってリトライすると検証エラー等を延々と再試行してしまうため明示的に除外する。
    /// </summary>
    public static bool IsTransientNetwork(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is FaultException) return false; // サーバー応答済みのフォールト = 非ネットワーク
            switch (e)
            {
                case SocketException:
                case TimeoutException:
                case HttpRequestException:
                case WebException:
                case EndpointNotFoundException:
                case CommunicationException: // WCF 通信断(FaultException は上で除外済み)
                    return true;
            }
        }
        return false;
    }
}
