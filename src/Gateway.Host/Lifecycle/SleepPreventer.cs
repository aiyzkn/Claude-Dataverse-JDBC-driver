using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gateway.Host.Lifecycle;

/// <summary>
/// 実行中の OS スリープ/画面オフを抑止する(D-9: ローカル PC での長時間移行が中断しないように)。
/// Windows は SetThreadExecutionState を専用スレッドで保持。非 Windows は no-op(Phase 2)。
/// </summary>
public sealed class SleepPreventer : IHostedService
{
    [Flags]
    private enum ExecutionState : uint
    {
        Continuous = 0x80000000,
        SystemRequired = 0x00000001,
        AwayModeRequired = 0x00000040,
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(ExecutionState esFlags);

    private readonly ILogger<SleepPreventer> _log;
    private readonly ManualResetEventSlim _stop = new(false);
    private Thread? _thread;

    public SleepPreventer(ILogger<SleepPreventer> log) => _log = log;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            _log.LogInformation("スリープ抑止は Windows のみ対応(現環境では無効)");
            return Task.CompletedTask;
        }

        // ES_CONTINUOUS は設定したスレッドに紐づくため、寿命を持つ専用スレッドで保持する
        _thread = new Thread(() =>
        {
            var r = SetThreadExecutionState(ExecutionState.Continuous | ExecutionState.SystemRequired | ExecutionState.AwayModeRequired);
            _log.LogInformation("OS スリープ抑止を有効化(SetThreadExecutionState={Result})", r);
            _stop.Wait();
            SetThreadExecutionState(ExecutionState.Continuous); // 抑止解除
            _log.LogInformation("OS スリープ抑止を解除");
        })
        { IsBackground = true, Name = "SleepPreventer" };
        _thread.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stop.Set();
        _thread?.Join(TimeSpan.FromSeconds(2));
        return Task.CompletedTask;
    }
}
