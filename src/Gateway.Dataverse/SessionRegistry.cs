using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Gateway.Dataverse;

/// <summary>
/// 接続(クライアント)単位の <see cref="SessionState"/> を管理する(4.2.2b サーバー配置)。
/// gRPC のピア(ServerCallContext.Peer)をキーに、SET DRY_RUN / バイパス既定 / 名前解決 /
/// 本番書き込み許可 等をクライアントごとに分離する。従来はプロセス全体で 1 セッションだったため、
/// 共有サーバーとして複数クライアントが接続すると設定が他クライアントへ漏れていた(それを解消)。
/// アイドルセッションはプルーニングして無制限増加を防ぐ。
/// </summary>
public sealed class SessionRegistry
{
    private sealed class Slot { public required SessionState Session; public long LastTicks; }

    private const int MaxRetained = 500;
    private readonly ConcurrentDictionary<string, Slot> _sessions = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly bool _defaultAllowProduction;

    // B2: 接続先(コンテキスト)は接続ごとに変わるため、本番書き込みの既定許可はプロセス設定
    // Gateway:AllowProductionWrites(既定 false=安全側)から取得する。本番判定自体は各コンテキストの
    // ProductionGuard が接続先 URL ごとに行い、EnsureWriteAllowed(sessionAllows) で本値と突き合わせる。
    public SessionRegistry(IConfiguration config, ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        _defaultAllowProduction = config.GetValue<bool?>("Gateway:AllowProductionWrites") ?? false;
    }

    /// <summary>ピア(接続)に対応する SessionState を取得(無ければ作成)。空ピアは単一キーに集約。</summary>
    public SessionState Get(string? peer)
    {
        var key = string.IsNullOrEmpty(peer) ? "(local)" : peer;
        var slot = _sessions.GetOrAdd(key, _ => new Slot
        {
            Session = new SessionState(_loggerFactory.CreateLogger<SessionState>(), _defaultAllowProduction),
            LastTicks = DateTime.UtcNow.Ticks,
        });
        slot.LastTicks = DateTime.UtcNow.Ticks;
        Prune();
        return slot.Session;
    }

    /// <summary>保持中のセッション数(監視/テスト用)。</summary>
    public int Count => _sessions.Count;

    private void Prune()
    {
        if (_sessions.Count <= MaxRetained) return;
        foreach (var kv in _sessions.OrderBy(k => k.Value.LastTicks).Take(_sessions.Count - MaxRetained))
            _sessions.TryRemove(kv.Key, out _);
    }
}
