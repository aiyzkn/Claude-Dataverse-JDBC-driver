using System.Diagnostics;

namespace Gateway.Host.Lifecycle;

/// <summary>直近のリクエスト時刻を保持し、アイドル自動終了の判定に使う。</summary>
public sealed class ActivityTracker
{
    private long _lastTicks = Stopwatch.GetTimestamp();

    public void Touch() => Interlocked.Exchange(ref _lastTicks, Stopwatch.GetTimestamp());

    public TimeSpan IdleFor => Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastTicks));
}
