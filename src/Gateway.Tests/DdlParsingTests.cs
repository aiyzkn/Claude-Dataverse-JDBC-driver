using Gateway.Dataverse;
using Xunit;

namespace Gateway.Tests;

/// <summary>DDL サブセット(4.2.1): CREATE TABLE / ALTER TABLE ADD の解析。</summary>
public class DdlParsingTests
{
    [Fact]
    public void CreateTable_parses_columns_and_primary()
    {
        var cmd = DdlExecutor.TryParse(
            "CREATE TABLE poc_widget2 (poc_name string PRIMARY, poc_qty int, poc_price decimal(10,2))");
        Assert.NotNull(cmd);
        Assert.Equal(DdlExecutor.DdlKind.CreateTable, cmd!.Kind);
        Assert.Equal("poc_widget2", cmd.Table);
        Assert.Equal(3, cmd.Columns.Count);
        Assert.Equal("poc_name", cmd.Columns[0].Name);
        Assert.True(cmd.Columns[0].IsPrimary);
        Assert.Equal("int", cmd.Columns[1].SqlType);
        Assert.Equal("decimal(10,2)", cmd.Columns[2].SqlType); // 型の () 内カンマで列分割されない
    }

    [Fact]
    public void CreateTable_without_explicit_primary()
    {
        var cmd = DdlExecutor.TryParse("CREATE TABLE poc_foo (poc_title nvarchar(200), poc_num int)");
        Assert.NotNull(cmd);
        Assert.Equal(2, cmd!.Columns.Count);
        Assert.False(cmd.Columns[0].IsPrimary); // PRIMARY 明示なし(実行時に先頭文字列列を主名に)
    }

    [Fact]
    public void AlterTable_add_column()
    {
        var cmd = DdlExecutor.TryParse("ALTER TABLE poc_gadget ADD poc_note nvarchar(100)");
        Assert.NotNull(cmd);
        Assert.Equal(DdlExecutor.DdlKind.AlterTableAdd, cmd!.Kind);
        Assert.Equal("poc_gadget", cmd.Table);
        Assert.Single(cmd.Columns);
        Assert.Equal("poc_note", cmd.Columns[0].Name);
        Assert.Equal("nvarchar(100)", cmd.Columns[0].SqlType);
    }

    [Fact]
    public void Schema_qualified_and_bracketed_table_normalized()
    {
        var cmd = DdlExecutor.TryParse("CREATE TABLE [dbo].[poc_bar] (poc_name string PRIMARY)");
        Assert.NotNull(cmd);
        Assert.Equal("poc_bar", cmd!.Table);
    }

    [Fact]
    public void DropTable_parsed()
    {
        var cmd = DdlExecutor.TryParse("DROP TABLE poc_widget2");
        Assert.NotNull(cmd);
        Assert.Equal(DdlExecutor.DdlKind.DropTable, cmd!.Kind);
        Assert.Equal("poc_widget2", cmd.Table);
    }

    [Theory]
    [InlineData("SELECT * FROM t")]
    [InlineData("INSERT INTO t (a) VALUES (?)")]
    [InlineData("ALTER TABLE t DROP COLUMN c")] // DROP COLUMN 非対応(安全側)
    public void Non_ddl_or_unsupported_returns_null(string sql)
        => Assert.Null(DdlExecutor.TryParse(sql));
}
