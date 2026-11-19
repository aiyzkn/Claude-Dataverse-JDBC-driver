using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Gateway.Dataverse;

/// <summary>
/// 本番環境ガード(3.1.2)。接続先が本番と判定される場合、書き込み(INSERT/UPSERT/DELETE/
/// TRUNCATE/ASSOCIATE 等)を既定でブロックし、明示オプトインがある場合のみ許可する。
/// 読み取り(SELECT)は常に許可＝安全側既定の原則。
///
/// 本番判定(優先順, fail-closed):
///   1. 設定 `Gateway:IsProduction` (true/false) を最優先。
///   2. 未設定時は安全側=本番扱い。接続先がリモート Dataverse(*.dynamics.com)なら
///      dev/prod を URL で区別できないため「本番の可能性あり」としてブロック側に倒す。
///      ホスト名に prod / prd / production を含む場合も本番。
///      ローカル/その他ホスト(localhost 等)は非本番既定。
/// オプトイン:
///   - 起動時: `Gateway:AllowProductionWrites=true`。
///   - セッション中: `SET ALLOW_PRODUCTION_WRITES = ON`(単一ユーザー sidecar=プロセス1セッション)。
/// 非本番と分かっている接続は `Gateway:IsProduction=false` を明示して書き込みを解禁する。
/// </summary>
public sealed class ProductionGuard
{
    private static readonly Regex ProductionHost = new(
        @"(prod|prd|production)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ILogger _log;

    /// <summary>接続先が本番と判定されたか。</summary>
    public bool IsProduction { get; }

    /// <summary>判定に用いた接続先ホスト名。</summary>
    public string TargetHost { get; }

    /// <summary>新規セッションの本番書き込み既定許可(起動時 `Gateway:AllowProductionWrites`)。4.2.2b でセッション単位に保持。</summary>
    public bool DefaultAllow { get; }

    public ProductionGuard(bool isProduction, string targetHost, bool allowProductionWrites, ILogger log)
    {
        IsProduction = isProduction;
        TargetHost = targetHost;
        _log = log;
        DefaultAllow = allowProductionWrites;
    }

    /// <summary>接続先 URL・設定から本番ガードを構築し、判定結果をログに出す。</summary>
    public static ProductionGuard Create(string? connectedUrl, IConfiguration config, ILogger log)
    {
        var host = Uri.TryCreate(connectedUrl, UriKind.Absolute, out var u) ? u.Host : (connectedUrl ?? "");
        var explicitFlag = config.GetValue<bool?>("Gateway:IsProduction");
        // fail-closed: 明示設定が無ければ、本番トークンを含むホスト または リモート Dataverse を本番扱い。
        var isProd = explicitFlag ?? (LooksLikeProduction(host) || IsRemoteDataverse(host));
        var allow = config.GetValue<bool?>("Gateway:AllowProductionWrites") ?? false;
        var guard = new ProductionGuard(isProd, host, allow, log);

        if (isProd)
        {
            var source = explicitFlag.HasValue ? "設定"
                : LooksLikeProduction(host) ? "ホスト名"
                : "既定(リモートDataverse)";
            log.LogWarning(
                "本番環境ガード: 接続先 '{Host}' を本番と判定(判定元={Source})。書き込みは既定でブロックします。" +
                "許可するには SET ALLOW_PRODUCTION_WRITES=ON か起動時 Gateway:AllowProductionWrites=true。" +
                "非本番なら Gateway:IsProduction=false を明示してください。新規セッション既定許可={Allow}",
                host, source, guard.DefaultAllow);
        }
        else
            log.LogInformation("本番環境ガード: 接続先 '{Host}' は非本番と判定(書き込み許可)。", host);
        return guard;
    }

    /// <summary>ホスト名に prod / prd / production を含むか(大文字小文字無視)。</summary>
    public static bool LooksLikeProduction(string host) => ProductionHost.IsMatch(host ?? "");

    /// <summary>接続先がリモート Dataverse 組織(*.dynamics.com)か。dev/prod を URL で区別できないため本番既定(fail-closed)に用いる。</summary>
    public static bool IsRemoteDataverse(string host)
        => !string.IsNullOrEmpty(host) && host.EndsWith(".dynamics.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>本番かつセッション未許可なら書き込みを拒否する(安全側既定)。allow はセッションの許可状態(4.2.2b)。</summary>
    public void EnsureWriteAllowed(string operation, string target, bool sessionAllows)
    {
        if (IsProduction && !sessionAllows)
            throw new InvalidOperationException(
                $"本番環境ガード: 接続先 '{TargetHost}' は本番と判定されているため、書き込み({operation} {target})はブロックされています。" +
                "許可するにはセッションで `SET ALLOW_PRODUCTION_WRITES = ON` を実行するか、" +
                "起動時に `Gateway:AllowProductionWrites=true`(誤検知時は `Gateway:IsProduction=false`)を設定してください。");
    }
}
