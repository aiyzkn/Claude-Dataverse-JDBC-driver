namespace Gateway.Host.Lifecycle;

/// <summary>
/// 名前付きミューテックスで多重起動を防止する(ローカル PC サイドカーは 1 プロセスが前提)。
/// ポート番号ごとに区別し、取得できなければ既存プロセスが稼働中とみなす。
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private readonly bool _owned;

    private SingleInstanceGuard(Mutex mutex, bool owned)
    {
        _mutex = mutex;
        _owned = owned;
    }

    public bool IsPrimary => _owned;

    public static SingleInstanceGuard Acquire(int port)
    {
        // Local\ 名前空間 = ログオンセッション単位(ユーザーのローカルサイドカーに適合)
        var mutex = new Mutex(initiallyOwned: true, $"Local\\DataverseGW_{port}", out var owned);
        return new SingleInstanceGuard(mutex, owned);
    }

    public void Dispose()
    {
        if (_owned)
        {
            try { _mutex.ReleaseMutex(); } catch { /* 既に解放済みは無視 */ }
        }
        _mutex.Dispose();
    }
}
