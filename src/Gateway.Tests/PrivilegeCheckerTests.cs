using Gateway.Dataverse;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// PrivilegeChecker の特権事前検査(F-08)。IMPERSONATE 特権検査(M-1)と
/// 例外時 fail-closed(L-1)を、フェイクの IOrganizationService で単体検証する。
/// </summary>
public class PrivilegeCheckerTests
{
    private static PrivilegeChecker New(IOrganizationService org)
        => new(org, NullLogger<PrivilegeChecker>.Instance);

    // --- M-1: なりすまし特権検査 ---

    [Fact]
    public void EnsureCanImpersonate_特権あり_例外を投げない()
    {
        var checker = New(new FakeOrg(granted: PrivilegeChecker.ActOnBehalfPrivilege));
        checker.EnsureCanImpersonate(); // 例外なし
    }

    [Fact]
    public void EnsureCanImpersonate_特権なし_InvalidOperationException()
    {
        var checker = New(new FakeOrg()); // 何も付与しない
        var ex = Assert.Throws<InvalidOperationException>(() => checker.EnsureCanImpersonate());
        Assert.Contains(PrivilegeChecker.ActOnBehalfPrivilege, ex.Message);
    }

    [Fact]
    public void EnsureCanImpersonate_別特権のみ保有_不許可()
    {
        // 監査列上書き特権だけを持つユーザーは IMPERSONATE を通してはならない
        var checker = New(new FakeOrg(granted: PrivilegeChecker.OverrideCreatedByPrivilege));
        Assert.Throws<InvalidOperationException>(() => checker.EnsureCanImpersonate());
    }

    // --- 既存: 監査列上書き特権検査 ---

    [Fact]
    public void EnsureCanOverrideCreatedColumns_特権あり_例外を投げない()
    {
        var checker = New(new FakeOrg(granted: PrivilegeChecker.OverrideCreatedByPrivilege));
        checker.EnsureCanOverrideCreatedColumns();
    }

    [Fact]
    public void EnsureCanOverrideCreatedColumns_特権なし_例外()
        => Assert.Throws<InvalidOperationException>(
            () => New(new FakeOrg()).EnsureCanOverrideCreatedColumns());

    // --- L-1: 検査自体が失敗したら fail-closed(安全側=不許可) ---

    [Fact]
    public void 特権クエリが例外_fail_closed_で不許可()
    {
        var checker = New(new FakeOrg(throwOnQuery: true));
        // 検査自体が例外でも許可扱いにせず、実行前にブロックする
        Assert.Throws<InvalidOperationException>(() => checker.EnsureCanImpersonate());
        Assert.Throws<InvalidOperationException>(() => checker.EnsureCanOverrideCreatedColumns());
    }

    /// <summary>
    /// WhoAmI / privilege 検索 / RetrieveUserPrivileges を最小構成で模倣する IOrganizationService。
    /// granted に渡した特権名だけを保有ユーザーとして応答する。throwOnQuery=true で全問い合わせを失敗させる。
    /// </summary>
    private sealed class FakeOrg : IOrganizationService
    {
        private readonly Guid _userId = Guid.NewGuid();
        private readonly Dictionary<string, Guid> _privIds = new();
        private readonly HashSet<Guid> _granted = new();
        private readonly bool _throw;

        public FakeOrg(bool throwOnQuery = false, params string[] granted)
        {
            _throw = throwOnQuery;
            foreach (var name in new[] { PrivilegeChecker.OverrideCreatedByPrivilege, PrivilegeChecker.ActOnBehalfPrivilege })
                _privIds[name] = Guid.NewGuid();
            foreach (var g in granted) _granted.Add(_privIds[g]);
        }

        public OrganizationResponse Execute(OrganizationRequest request)
        {
            if (_throw) throw new InvalidOperationException("simulated query failure");
            switch (request)
            {
                case WhoAmIRequest:
                    return new WhoAmIResponse { Results = new ParameterCollection { { "UserId", _userId } } };
                case RetrieveUserPrivilegesRequest:
                    var rolePrivs = _granted.Select(id => new RolePrivilege { PrivilegeId = id }).ToArray();
                    return new RetrieveUserPrivilegesResponse { Results = new ParameterCollection { { "RolePrivileges", rolePrivs } } };
                default:
                    throw new NotImplementedException(request.RequestName);
            }
        }

        public EntityCollection RetrieveMultiple(QueryBase query)
        {
            if (_throw) throw new InvalidOperationException("simulated query failure");
            var name = (string)((QueryExpression)query).Criteria.Conditions[0].Values[0];
            var result = new EntityCollection();
            if (_privIds.TryGetValue(name, out var id))
                result.Entities.Add(new Entity("privilege") { Id = id });
            return result;
        }

        // 未使用のメンバー(本テストでは到達しない)
        public Guid Create(Entity entity) => throw new NotImplementedException();
        public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet) => throw new NotImplementedException();
        public void Update(Entity entity) => throw new NotImplementedException();
        public void Delete(string entityName, Guid id) => throw new NotImplementedException();
        public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) => throw new NotImplementedException();
        public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) => throw new NotImplementedException();
    }
}
