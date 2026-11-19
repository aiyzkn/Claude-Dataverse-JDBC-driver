using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gateway.Host.Lifecycle;

/// <summary>
/// アイドル状態が閾値を超えたらゲートウェイを自動終了する(D-9: ローカルサイドカーの資源節約)。
/// 起動直後は接続待ちのため猶予を設ける。閾値 0 以下で無効。
/// </summary>
public sealed class IdleShutdownService : BackgroundService
{
    private readonly ActivityTracker _tracker;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<IdleShutdownService> _log;
    private readonly TimeSpan _idleTimeout;

    public IdleShutdownService(ActivityTracker tracker, IHostApplicationLifetime lifetime,
        ILogger<IdleShutdownService> log, IConfiguration config)
    {
        _tracker = tracker;
        _lifetime = lifetime;
        _log = log;
        _idleTimeout = TimeSpan.FromMinutes(config.GetValue<double?>("Gateway:IdleShutdownMinutes") ?? 30);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_idleTimeout <= TimeSpan.Zero)
        {
            _log.LogInformation("アイドル自動終了は無効(IdleShutdownMinutes<=0)");
            return;
        }
        _log.LogInformation("アイドル自動終了: {Min} 分", _idleTimeout.TotalMinutes);

        var interval = TimeSpan.FromSeconds(Math.Min(60, _idleTimeout.TotalSeconds / 2));
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (_tracker.IdleFor >= _idleTimeout)
            {
                _log.LogInformation("アイドル {Min} 分を超過。ゲートウェイを終了します。", _idleTimeout.TotalMinutes);
                _lifetime.StopApplication();
                return;
            }
        }
    }
}
