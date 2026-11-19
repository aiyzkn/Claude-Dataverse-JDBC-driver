using Gateway.Dataverse;
using Xunit;

namespace Gateway.Tests;

/// <summary>PreparedInsertExecutor.TryParseWrite / BulkDeleteExecutor.TryParseTruncate の解析仕様。</summary>
public class WriteCommandParsingTests
{
    [Fact]
    public void Insert_parsed_with_columns()
    {
        var cmd = PreparedInsertExecutor.TryParseWrite(
            "INSERT INTO poc_gadget (poc_name, poc_quantity) VALUES (?, ?)");
        Assert.NotNull(cmd);
        Assert.Equal(PreparedInsertExecutor.WriteKind.Insert, cmd!.Kind);
        Assert.Equal("poc_gadget", cmd.Table);
        Assert.Equal(new[] { "poc_name", "poc_quantity" }, cmd.Columns);
        Assert.Empty(cmd.KeyColumns);
        Assert.Null(cmd.IdColumn);
    }

    [Theory]
    // DBeaver など JDBC クライアントは INSERT INTO dbo.poc_gadget ... の形で送る。
    // 実測: "INSERT INTO dbo.poc_gadget (poc_name,poc_quantity) \t VALUES (?,?)"
    [InlineData("INSERT INTO dbo.poc_gadget (poc_name,poc_quantity) \tVALUES (?,?)")]
    [InlineData("INSERT INTO [dbo].[poc_gadget] (poc_name, poc_quantity) VALUES (?, ?)")]
    [InlineData("INSERT INTO \"dbo\".\"poc_gadget\" (poc_name, poc_quantity) VALUES (?, ?)")]
    [InlineData("INSERT INTO [poc_gadget] (poc_name, poc_quantity) VALUES (?, ?)")]
    public void Insert_with_schema_qualified_or_quoted_table_normalizes_to_logical_name(string sql)
    {
        var cmd = PreparedInsertExecutor.TryParseWrite(sql);
        Assert.NotNull(cmd);
        Assert.Equal("poc_gadget", cmd!.Table);
        Assert.Equal(new[] { "poc_name", "poc_quantity" }, cmd.Columns);
    }

    [Theory]
    [InlineData("dbo.poc_gadget", "poc_gadget")]
    [InlineData("[dbo].[poc_gadget]", "poc_gadget")]
    [InlineData("\"dbo\".\"poc_gadget\"", "poc_gadget")]
    [InlineData("poc_gadget", "poc_gadget")]
    [InlineData(" [poc_gadget] ", "poc_gadget")]
    public void NormalizeTable_strips_schema_and_delimiters(string input, string expected)
        => Assert.Equal(expected, PreparedInsertExecutor.NormalizeTable(input));

    [Fact]
    public void Truncate_and_delete_accept_schema_qualified_table()
    {
        Assert.Equal("poc_gadget", BulkDeleteExecutor.TryParseTruncate("TRUNCATE TABLE dbo.poc_gadget"));
        var del = BulkDeleteExecutor.TryParseDelete("DELETE FROM [dbo].[poc_gadget] WHERE poc_name = 'x'");
        Assert.NotNull(del);
        Assert.Equal("poc_gadget", del!.Table);
    }

    [Fact]
    public void Upsert_with_on_key_captures_key_columns()
    {
        var cmd = PreparedInsertExecutor.TryParseWrite(
            "UPSERT INTO poc_widget (poc_externalid, poc_name) VALUES (?, ?) ON KEY (poc_externalid)");
        Assert.NotNull(cmd);
        Assert.Equal(PreparedInsertExecutor.WriteKind.Upsert, cmd!.Kind);
        Assert.Equal(new[] { "poc_externalid" }, cmd.KeyColumns);
        Assert.Null(cmd.IdColumn);
    }

    [Fact]
    public void Upsert_without_on_key_uses_primary_id_column()
    {
        var cmd = PreparedInsertExecutor.TryParseWrite(
            "UPSERT INTO poc_gadget (poc_gadgetid, poc_name) VALUES (?, ?)");
        Assert.NotNull(cmd);
        Assert.Equal(PreparedInsertExecutor.WriteKind.Upsert, cmd!.Kind);
        Assert.Equal("poc_gadgetid", cmd.IdColumn);
        Assert.Empty(cmd.KeyColumns);
    }

    [Fact]
    public void Upsert_without_key_or_id_column_is_rejected()
    {
        // ON KEY も主キー列も無い UPSERT は識別子が決まらないため対象外
        var cmd = PreparedInsertExecutor.TryParseWrite(
            "UPSERT INTO poc_gadget (poc_name, poc_quantity) VALUES (?, ?)");
        Assert.Null(cmd);
    }

    [Theory]
    [InlineData("INSERT INTO t (a, b) VALUES (?, 1)")]       // 非パラメータ値
    [InlineData("INSERT INTO t (a, b) VALUES (?)")]           // 列数と値数の不一致
    [InlineData("SELECT * FROM t")]                            // 書き込みでない
    [InlineData("UPSERT INTO t (a) VALUES (?) ON KEY (b)")]   // キー列が列一覧に無い
    public void Invalid_forms_return_null(string sql)
        => Assert.Null(PreparedInsertExecutor.TryParseWrite(sql));

    [Theory]
    [InlineData("TRUNCATE TABLE poc_gadget", "poc_gadget")]
    [InlineData("  truncate   table   Account ;", "Account")]
    public void Truncate_parsed(string sql, string expected)
        => Assert.Equal(expected, BulkDeleteExecutor.TryParseTruncate(sql));

    [Theory]
    [InlineData("DELETE FROM t WHERE x = 1")]
    [InlineData("TRUNCATE poc_gadget")] // TABLE キーワード必須
    public void Non_truncate_returns_null(string sql)
        => Assert.Null(BulkDeleteExecutor.TryParseTruncate(sql));

    [Fact]
    public void Hints_parsed_into_options()
    {
        var o = PreparedInsertExecutor.ParseHints(
            "INSERT /*+ BYPASS_PLUGINS BYPASS_FLOWS SUPPRESS_DUPLICATE_DETECTION */ INTO t (a) VALUES (?)");
        Assert.True(o.BypassSyncPlugins);
        Assert.True(o.BypassFlows);
        Assert.True(o.SuppressDuplicateDetection);
        Assert.False(o.BypassAsyncPlugins);
        Assert.True(o.Any);
    }

    [Fact]
    public void No_hints_means_no_bypass()
    {
        var o = PreparedInsertExecutor.ParseHints("INSERT INTO t (a) VALUES (?)");
        Assert.False(o.Any);
        Assert.Null(o.ImpersonateUserId);
    }

    [Fact]
    public void Impersonate_hint_parsed_to_user_id()
    {
        var g = Guid.NewGuid();
        var o = PreparedInsertExecutor.ParseHints($"INSERT /*+ IMPERSONATE={g} */ INTO t (a) VALUES (?)");
        Assert.Equal(g, o.ImpersonateUserId);
    }

    [Fact]
    public void As_user_hint_alias_parsed()
    {
        var g = Guid.NewGuid();
        var o = PreparedInsertExecutor.ParseHints($"INSERT /*+ AS_USER={g} BYPASS_PLUGINS */ INTO t (a) VALUES (?)");
        Assert.Equal(g, o.ImpersonateUserId);
        Assert.True(o.BypassSyncPlugins);
    }

    [Fact]
    public void Job_id_and_force_new_job_hints_parsed()
    {
        var o1 = PreparedInsertExecutor.ParseHints("INSERT /*+ JOB_ID=migration-2026Q3 */ INTO t (a) VALUES (?)");
        Assert.Equal("migration-2026Q3", o1.JobId);
        Assert.False(o1.ForceNewJob);

        var o2 = PreparedInsertExecutor.ParseHints("INSERT /*+ FORCE_NEW_JOB */ INTO t (a) VALUES (?)");
        Assert.True(o2.ForceNewJob);
        Assert.Null(o2.JobId);
    }

    [Fact]
    public void No_bulk_hint_forces_execute_multiple()
    {
        var o1 = PreparedInsertExecutor.ParseHints("INSERT /*+ NO_BULK */ INTO t (a) VALUES (?)");
        Assert.True(o1.ForceExecuteMultiple);
        var o2 = PreparedInsertExecutor.ParseHints("INSERT /*+ FORCE_EXECUTE_MULTIPLE */ INTO t (a) VALUES (?)");
        Assert.True(o2.ForceExecuteMultiple);
        Assert.False(PreparedInsertExecutor.ParseHints("INSERT INTO t (a) VALUES (?)").ForceExecuteMultiple);
        Assert.Contains("noBulk", PreparedInsertExecutor.DescribeOptions(o1));
    }

    [Fact]
    public void Hint_is_stripped_before_parsing_and_options_attached()
    {
        var cmd = PreparedInsertExecutor.TryParseWrite(
            "INSERT /*+ BYPASS_PLUGINS */ INTO poc_gadget (poc_name, poc_quantity) VALUES (?, ?)");
        Assert.NotNull(cmd);
        Assert.Equal("poc_gadget", cmd!.Table); // ヒントがあっても正しく解析
        Assert.Equal(2, cmd.Columns.Count);
        Assert.True(cmd.Options.BypassSyncPlugins);
    }

    [Fact]
    public void ApplyOptions_sets_bypass_request_parameters()
    {
        var req = new Microsoft.Xrm.Sdk.Messages.CreateRequest();
        PreparedInsertExecutor.ApplyOptions(req, new PreparedInsertExecutor.WriteOptions(
            BypassSyncPlugins: true, BypassFlows: true, SuppressDuplicateDetection: true));
        Assert.Equal("CustomSync", req["BypassBusinessLogicExecution"]);
        Assert.Equal(true, req["SuppressCallbackRegistrationExpanderJob"]);
        Assert.Equal(true, req["SuppressDuplicateDetection"]);
    }

    [Theory]
    [InlineData("createdon", "overriddencreatedon")]
    [InlineData("CreatedOn", "overriddencreatedon")]
    [InlineData("poc_name", "poc_name")]
    [InlineData("ownerid", "ownerid")]
    public void MapWriteColumn_maps_createdon_to_override(string input, string expected)
        => Assert.Equal(expected, PreparedInsertExecutor.MapWriteColumn(input));

    [Fact]
    public void ApplyOptions_noop_when_no_options()
    {
        var req = new Microsoft.Xrm.Sdk.Messages.CreateRequest();
        PreparedInsertExecutor.ApplyOptions(req, PreparedInsertExecutor.WriteOptions.None);
        Assert.False(req.Parameters.ContainsKey("BypassBusinessLogicExecution"));
    }
}
