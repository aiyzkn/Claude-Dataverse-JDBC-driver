using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace Gateway.Dataverse;

/// <summary>
/// 書き込みに関わる特権を実行前に検査する(F-08: 特権不足は実行前に明示エラー)。結果はキャッシュ。
///   - prvOverrideCreatedOnCreatedBy: 監査列(createdon/createdby)の上書き。
///   - prvActOnBehalfOfAnotherUser: なりすまし(IMPERSONATE / CallerId)。
/// </summary>
public sealed class PrivilegeChecker
{
    public const string OverrideCreatedByPrivilege = "prvOverrideCreatedOnCreatedBy";
    public const string ActOnBehalfPrivilege = "prvActOnBehalfOfAnotherUser";

    private readonly IOrganizationService _org;
    private readonly ILogger<PrivilegeChecker> _log;
    private readonly Lazy<bool> _hasOverride;
    private readonly Lazy<bool> _hasImpersonate;

    public PrivilegeChecker(DataverseConnection connection, ILogger<PrivilegeChecker> log)
        : this(connection.Client, log) { }

    // ServiceClient は IOrganizationService を実装する。本番は上の ctor 経由、
    // テストはフェイクの IOrganizationService を直接注入して検査ロジックを単体検証する。
    public PrivilegeChecker(IOrganizationService org, ILogger<PrivilegeChecker> log)
    {
        _org = org;
        _log = log;
        _hasOverride = new Lazy<bool>(() => HasPrivilege(OverrideCreatedByPrivilege), LazyThreadSafetyMode.ExecutionAndPublication);
        _hasImpersonate = new Lazy<bool>(() => HasPrivilege(ActOnBehalfPrivilege), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>監査列上書きの前提特権があるか。無ければ例外(実行前検査)。</summary>
    public void EnsureCanOverrideCreatedColumns()
    {
        if (!_hasOverride.Value)
            throw new InvalidOperationException(
                $"createdon/createdby の上書きには特権 {OverrideCreatedByPrivilege} が必要ですが、実行ユーザーに付与されていません。");
    }

    /// <summary>なりすまし(IMPERSONATE)の前提特権があるか。無ければ例外(実行前検査。M-1)。</summary>
    public void EnsureCanImpersonate()
    {
        if (!_hasImpersonate.Value)
            throw new InvalidOperationException(
                $"なりすまし(IMPERSONATE / CallerId)には特権 {ActOnBehalfPrivilege} が必要ですが、実行ユーザーに付与されていません。");
    }

    private bool HasPrivilege(string privilegeName)
    {
        try
        {
            var me = ((WhoAmIResponse)_org.Execute(new WhoAmIRequest())).UserId;

            var priv = _org.RetrieveMultiple(new QueryExpression("privilege")
            {
                ColumnSet = new ColumnSet("privilegeid"),
                Criteria = new FilterExpression { Conditions = { new ConditionExpression("name", ConditionOperator.Equal, privilegeName) } },
            }).Entities.FirstOrDefault();
            if (priv is null) { _log.LogWarning("特権 {Priv} が見つかりません", privilegeName); return false; }
            var privId = priv.Id;

            var resp = (RetrieveUserPrivilegesResponse)_org.Execute(new RetrieveUserPrivilegesRequest { UserId = me });
            var has = resp.RolePrivileges.Any(rp => rp.PrivilegeId == privId);
            _log.LogInformation("特権事前検査: {Priv} 保有 = {Has}", privilegeName, has);
            return has;
        }
        catch (Exception ex)
        {
            // 認可検査は fail-closed(L-1): 検査自体に失敗した場合は安全側=不許可に倒す。
            // (以前は許可扱いだったが、認可の穴になるため是正)
            _log.LogWarning(ex, "特権 {Priv} の事前検査に失敗。安全側で不許可にする", privilegeName);
            return false;
        }
    }
}
