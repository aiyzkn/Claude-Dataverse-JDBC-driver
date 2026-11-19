using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Opt = Gateway.Dataverse.PreparedInsertExecutor.WriteOptions;

namespace Gateway.Dataverse;

/// <summary>
/// セッション既定の書き込みオプション(F-09 セッション層)。`SET ... = ON/OFF` で
/// セッションの既定バイパス・ドライラン・本番書き込み許可等を設定し、以降の書き込みに適用する。
/// 4.2.2b で接続(クライアント)単位に分離(SessionRegistry がピア別に本オブジェクトを保持)。
/// 文単位ヒントはセッション既定より優先する。
/// </summary>
public sealed class SessionState
{
    private static readonly Regex SetPattern = new(
        @"^\s*SET\s+(?<key>[A-Za-z_]+)\s*=\s*(?<val>'[^']*'|[A-Za-z0-9\-]+)\s*;?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ResetPattern = new(
        @"^\s*RESET(\s+ALL)?\s*;?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ILogger<SessionState> _log;
    private readonly object _gate = new();
    private readonly bool _defaultAllowProduction;
    private Opt _defaults = Opt.None;
    private bool _dryRun;
    private bool _allowProduction;

    public SessionState(ILogger<SessionState> log, bool defaultAllowProduction = false)
    {
        _log = log;
        _defaultAllowProduction = defaultAllowProduction;
        _allowProduction = defaultAllowProduction;
    }

    /// <summary>現在のセッション既定オプション。</summary>
    public Opt Defaults { get { lock (_gate) return _defaults; } }

    /// <summary>ドライラン(3.1.1)が有効か。有効時は書き込みを実行せずレポートのみ返す。</summary>
    public bool DryRun { get { lock (_gate) return _dryRun; } }

    /// <summary>本番書き込みをこのセッションで許可しているか(本番ガード 3.1.2 と組み合わせて判定。4.2.2b)。</summary>
    public bool AllowProductionWrites { get { lock (_gate) return _allowProduction; } }

    /// <summary>セッション既定を文単位ヒントに重ねる(ヒント優先: bool は OR、null 許容は合体)。</summary>
    public Opt Effective(Opt statement)
    {
        var d = Defaults;
        return new Opt(
            BypassSyncPlugins: d.BypassSyncPlugins || statement.BypassSyncPlugins,
            BypassAsyncPlugins: d.BypassAsyncPlugins || statement.BypassAsyncPlugins,
            BypassFlows: d.BypassFlows || statement.BypassFlows,
            SuppressDuplicateDetection: d.SuppressDuplicateDetection || statement.SuppressDuplicateDetection,
            ImpersonateUserId: statement.ImpersonateUserId ?? d.ImpersonateUserId,
            JobId: statement.JobId ?? d.JobId,
            ForceNewJob: d.ForceNewJob || statement.ForceNewJob,
            ForceExecuteMultiple: d.ForceExecuteMultiple || statement.ForceExecuteMultiple,
            ResolveNames: d.ResolveNames || statement.ResolveNames);
    }

    /// <summary>SET/RESET 文なら適用して true。それ以外は false(通常の文として処理させる)。</summary>
    public bool TryApply(string sql)
    {
        if (ResetPattern.IsMatch(sql))
        {
            lock (_gate) { _defaults = Opt.None; _dryRun = false; _allowProduction = _defaultAllowProduction; }
            _log.LogInformation("セッション既定をリセット");
            return true;
        }

        var m = SetPattern.Match(sql);
        if (!m.Success) return false;

        var key = m.Groups["key"].Value.ToUpperInvariant();
        var raw = m.Groups["val"].Value.Trim('\'');
        var on = raw.Equals("ON", StringComparison.OrdinalIgnoreCase)
              || raw.Equals("TRUE", StringComparison.OrdinalIgnoreCase)
              || raw == "1";
        var off = raw.Equals("OFF", StringComparison.OrdinalIgnoreCase)
               || raw.Equals("FALSE", StringComparison.OrdinalIgnoreCase)
               || raw == "0";

        // ドライラン(3.1.1)は WriteOptions ではなくセッション状態として保持
        if (key == "DRY_RUN")
        {
            lock (_gate) _dryRun = on;
            _log.LogInformation("セッション既定を更新: DRY_RUN={Val}", raw);
            return true;
        }
        // 本番書き込み許可(3.1.2/4.2.2b)もセッション状態として保持
        if (key == "ALLOW_PRODUCTION_WRITES")
        {
            lock (_gate) _allowProduction = on;
            _log.LogWarning("セッションの本番書き込み許可を更新: ALLOW_PRODUCTION_WRITES={Val}", raw);
            return true;
        }

        lock (_gate)
        {
            _defaults = key switch
            {
                "BYPASS_PLUGINS"               => _defaults with { BypassSyncPlugins = on },
                "BYPASS_ASYNC_PLUGINS"         => _defaults with { BypassAsyncPlugins = on },
                "BYPASS_FLOWS"                 => _defaults with { BypassFlows = on },
                "SUPPRESS_DUPLICATE_DETECTION" => _defaults with { SuppressDuplicateDetection = on },
                "NO_BULK" or "FORCE_EXECUTE_MULTIPLE" => _defaults with { ForceExecuteMultiple = on },
                "RESOLVE_NAMES"                => _defaults with { ResolveNames = on },
                "IMPERSONATE" or "AS_USER"     => _defaults with { ImpersonateUserId = (off ? null : ParseGuid(raw)) },
                "JOB_ID"                       => _defaults with { JobId = (off ? null : raw) },
                _ => throw new InvalidOperationException($"未知のセッション設定キー: {key}"),
            };
        }
        _log.LogInformation("セッション既定を更新: {Key}={Val}", key, raw);
        return true;
    }

    private static Guid? ParseGuid(string s) => Guid.TryParse(s, out var g) ? g : null;
}
