using Gateway.Dataverse;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gateway.Tests;

/// <summary>セッション既定オプション(F-09): SET/RESET とヒント優先マージ。</summary>
public class SessionStateTests
{
    private static SessionState New() => new(NullLogger<SessionState>.Instance);

    [Fact]
    public void Set_on_enables_session_default()
    {
        var s = New();
        Assert.True(s.TryApply("SET BYPASS_PLUGINS = ON"));
        Assert.True(s.Defaults.BypassSyncPlugins);
        Assert.True(s.TryApply("SET BYPASS_FLOWS = ON"));
        Assert.True(s.Defaults.BypassFlows);
    }

    [Fact]
    public void Set_off_and_reset_clear()
    {
        var s = New();
        s.TryApply("SET BYPASS_PLUGINS = ON");
        s.TryApply("SET BYPASS_PLUGINS = OFF");
        Assert.False(s.Defaults.BypassSyncPlugins);

        s.TryApply("SET NO_BULK = ON");
        Assert.True(s.TryApply("RESET"));
        Assert.False(s.Defaults.ForceExecuteMultiple);
    }

    [Fact]
    public void Impersonate_and_job_id_set_and_clear()
    {
        var s = New();
        var g = System.Guid.NewGuid();
        s.TryApply($"SET IMPERSONATE = '{g}'");
        Assert.Equal(g, s.Defaults.ImpersonateUserId);
        s.TryApply("SET IMPERSONATE = OFF");
        Assert.Null(s.Defaults.ImpersonateUserId);

        s.TryApply("SET JOB_ID = 'migration-2026Q3'");
        Assert.Equal("migration-2026Q3", s.Defaults.JobId);
    }

    [Fact]
    public void Allow_production_writes_session_toggle()
    {
        var s = New();
        Assert.False(s.AllowProductionWrites);
        Assert.True(s.TryApply("SET ALLOW_PRODUCTION_WRITES = ON"));
        Assert.True(s.AllowProductionWrites);
        Assert.True(s.TryApply("SET ALLOW_PRODUCTION_WRITES = OFF"));
        Assert.False(s.AllowProductionWrites);
    }

    [Fact]
    public void Allow_production_defaults_and_reset()
    {
        var s = new SessionState(NullLogger<SessionState>.Instance, defaultAllowProduction: true);
        Assert.True(s.AllowProductionWrites);      // 起動既定
        s.TryApply("SET ALLOW_PRODUCTION_WRITES = OFF");
        Assert.False(s.AllowProductionWrites);
        s.TryApply("RESET");
        Assert.True(s.AllowProductionWrites);       // RESET は起動既定へ戻す
    }

    [Fact]
    public void Resolve_names_session_default_and_merge()
    {
        var s = New();
        Assert.True(s.TryApply("SET RESOLVE_NAMES = ON"));
        Assert.True(s.Defaults.ResolveNames);
        // セッション既定 ON は文にも波及(OR 合成)
        var stmt = PreparedInsertExecutor.ParseHints("INSERT INTO t (a) VALUES (?)");
        Assert.True(s.Effective(stmt).ResolveNames);
        s.TryApply("SET RESOLVE_NAMES = OFF");
        Assert.False(s.Defaults.ResolveNames);
    }

    [Fact]
    public void Effective_merges_session_and_statement_hint()
    {
        var s = New();
        s.TryApply("SET BYPASS_PLUGINS = ON");
        // 文ヒントは BYPASS_FLOWS のみ。セッションの BYPASS_PLUGINS と OR 合成される。
        var stmt = PreparedInsertExecutor.ParseHints("INSERT /*+ BYPASS_FLOWS */ INTO t (a) VALUES (?)");
        var eff = s.Effective(stmt);
        Assert.True(eff.BypassSyncPlugins); // セッション由来
        Assert.True(eff.BypassFlows);       // ヒント由来
    }

    [Fact]
    public void Statement_hint_wins_for_nullable_job_id()
    {
        var s = New();
        s.TryApply("SET JOB_ID = 'session-job'");
        var stmt = PreparedInsertExecutor.ParseHints("INSERT /*+ JOB_ID=stmt-job */ INTO t (a) VALUES (?)");
        Assert.Equal("stmt-job", s.Effective(stmt).JobId); // ヒント優先
    }

    [Theory]
    [InlineData("INSERT INTO t (a) VALUES (?)")]
    [InlineData("SELECT * FROM t")]
    [InlineData("TRUNCATE TABLE t")]
    public void Non_set_statements_are_not_applied(string sql)
        => Assert.False(New().TryApply(sql));

    [Fact]
    public void Unknown_set_key_throws()
        => Assert.Throws<System.InvalidOperationException>(() => New().TryApply("SET NONSENSE = ON"));
}
