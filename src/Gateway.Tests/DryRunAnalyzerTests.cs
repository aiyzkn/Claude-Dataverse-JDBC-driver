using Gateway.Dataverse;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using WKind = Gateway.Dataverse.PreparedInsertExecutor.WriteKind;
using WCmd = Gateway.Dataverse.PreparedInsertExecutor.WriteCommand;
using WOpt = Gateway.Dataverse.PreparedInsertExecutor.WriteOptions;

namespace Gateway.Tests;

/// <summary>実行前ドライラン(3.1.1): 件数・API・時間・必要特権の推定。</summary>
public class DryRunAnalyzerTests
{
    private static DryRunAnalyzer New(int batchSize = 200)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Gateway:BatchSize"] = batchSize.ToString() })
            .Build();
        return new DryRunAnalyzer(config, NullLogger<DryRunAnalyzer>.Instance);
    }

    private static WCmd Insert(params string[] cols)
        => new(WKind.Insert, "account", cols, System.Array.Empty<string>(), null, WOpt.None);

    [Fact]
    public void Insert_createmultiple_estimates_api_and_privileges()
    {
        var r = New(batchSize: 200).AnalyzeWrite(Insert("name"), rowCount: 1000, route: "CreateMultiple");
        Assert.Equal("CreateMultiple", r.Route);
        Assert.Equal(1000, r.RowCount);
        Assert.Equal(5, r.EstimatedApiRequests);           // ceil(1000/200)
        Assert.True(r.EstimatedSeconds > 0);
        Assert.Contains("Create (account)", r.RequiredPrivileges);
    }

    [Fact]
    public void Upsert_requires_create_and_write()
    {
        var cmd = new WCmd(WKind.Upsert, "account", new[] { "accountid", "name" },
            System.Array.Empty<string>(), "accountid", WOpt.None);
        var r = New().AnalyzeWrite(cmd, 10, "ExecuteMultiple(Upsert)");
        Assert.Contains("Create + Write (account)", r.RequiredPrivileges);
        Assert.Equal("ExecuteMultiple(Upsert)", r.Route);
    }

    [Fact]
    public void Audit_column_adds_override_privilege()
    {
        var r = New().AnalyzeWrite(Insert("name", "createdon"), 5, "CreateMultiple");
        Assert.Contains(r.RequiredPrivileges, p => p.Contains(PrivilegeChecker.OverrideCreatedByPrivilege));
    }

    [Fact]
    public void Impersonate_adds_act_on_behalf_privilege()
    {
        var cmd = new WCmd(WKind.Insert, "account", new[] { "name" }, System.Array.Empty<string>(), null,
            WOpt.None with { ImpersonateUserId = System.Guid.NewGuid() });
        var r = New().AnalyzeWrite(cmd, 5, "CreateMultiple");
        Assert.Contains(r.RequiredPrivileges, p => p.Contains("prvActOnBehalfOfAnotherUser"));
    }

    [Fact]
    public void Delete_defaults_to_bulkdelete_route_single_request()
    {
        var r = New().AnalyzeDelete("account", matchCount: 5000, isTruncate: false);
        Assert.StartsWith("BulkDelete", r.Route);
        Assert.Equal(1, r.EstimatedApiRequests);            // 非同期ジョブ発行 1 回
        Assert.Equal("Delete", r.Operation);
        Assert.Contains(r.RequiredPrivileges, p => p.Contains("BulkDelete"));
    }

    [Fact]
    public void Delete_sync_standard_route_estimates_by_1000_chunk_and_no_bulkdelete_privilege()
    {
        // 4.2.3: 標準テーブル同期経路(ExecuteMultiple)。API=ceil(2500/1000)+1(ID 引き当て)、BulkDelete 特権不要。
        var r = New().AnalyzeDelete("account", matchCount: 2500, isTruncate: false,
            route: "ExecuteMultiple(標準・同期・即時。5000件以下)");
        Assert.StartsWith("ExecuteMultiple", r.Route);
        Assert.Equal(4, r.EstimatedApiRequests);            // ceil(2500/1000)=3 +1
        Assert.DoesNotContain(r.RequiredPrivileges, p => p.Contains("BulkDelete"));
        Assert.Contains(r.RequiredPrivileges, p => p.Contains("Delete (account)"));
    }

    [Fact]
    public void Delete_elastic_route_estimates_by_100_chunk()
    {
        // 4.2.3: エラスティック DeleteMultiple。API=ceil(250/100)+1。
        var r = New().AnalyzeDelete("contoso_sensordata", matchCount: 250, isTruncate: false,
            route: "DeleteMultiple(エラスティック・同期・即時)");
        Assert.StartsWith("DeleteMultiple", r.Route);
        Assert.Equal(4, r.EstimatedApiRequests);            // ceil(250/100)=3 +1
        Assert.DoesNotContain(r.RequiredPrivileges, p => p.Contains("BulkDelete"));
    }

    [Fact]
    public void Associate_estimates_by_1000_chunk()
    {
        var cmd = new AssociateExecutor.AssociateCommand(AssociateExecutor.AssocVerb.Associate, "poc_rel");
        var r = New().AnalyzeAssociate(cmd, rowCount: 2500);
        Assert.Equal("Associate(EM)", r.Route);
        Assert.Equal(3, r.EstimatedApiRequests);            // ceil(2500/1000)
    }

    [Fact]
    public void Report_text_contains_key_fields()
    {
        var text = New().AnalyzeWrite(Insert("name"), 1000, "CreateMultiple").ToText();
        Assert.Contains("対象行数", text);
        Assert.Contains("推定所要時間", text);
        Assert.Contains("必要特権", text);
    }
}
