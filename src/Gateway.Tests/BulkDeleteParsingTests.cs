using Gateway.Dataverse;
using Microsoft.Xrm.Sdk.Query;
using Xunit;

namespace Gateway.Tests;

/// <summary>TRUNCATE / 条件付き DELETE の解析(F-10)。削除は安全側: 解釈不能な WHERE は null。</summary>
public class BulkDeleteParsingTests
{
    [Theory]
    [InlineData("TRUNCATE TABLE poc_widget", "poc_widget")]
    [InlineData("  truncate  table  Account ;", "Account")]
    public void Truncate_parsed(string sql, string expected)
        => Assert.Equal(expected, BulkDeleteExecutor.TryParseTruncate(sql));

    [Fact]
    public void Delete_without_where_is_all_rows()
    {
        var cmd = BulkDeleteExecutor.TryParseDelete("DELETE FROM poc_widget");
        Assert.NotNull(cmd);
        Assert.Equal("poc_widget", cmd!.Table);
        Assert.Empty(cmd.Criteria.Conditions);
    }

    [Fact]
    public void Delete_with_single_condition()
    {
        var cmd = BulkDeleteExecutor.TryParseDelete("DELETE FROM poc_widget WHERE statecode = 1");
        Assert.NotNull(cmd);
        var c = Assert.Single(cmd!.Criteria.Conditions);
        Assert.Equal("statecode", c.AttributeName);
        Assert.Equal(ConditionOperator.Equal, c.Operator);
        Assert.Equal(1L, c.Values[0]);
    }

    [Fact]
    public void Delete_with_multiple_and_conditions()
    {
        var cmd = BulkDeleteExecutor.TryParseDelete(
            "DELETE FROM poc_widget WHERE poc_name = 'abc' AND poc_quantity >= 5 AND createdon < '2020-01-01'");
        Assert.NotNull(cmd);
        Assert.Equal(3, cmd!.Criteria.Conditions.Count);
        Assert.Equal(ConditionOperator.Equal, cmd.Criteria.Conditions[0].Operator);
        Assert.Equal("abc", cmd.Criteria.Conditions[0].Values[0]);
        Assert.Equal(ConditionOperator.GreaterEqual, cmd.Criteria.Conditions[1].Operator);
        Assert.Equal(5L, cmd.Criteria.Conditions[1].Values[0]);
        Assert.Equal(ConditionOperator.LessThan, cmd.Criteria.Conditions[2].Operator);
    }

    [Theory]
    [InlineData("DELETE FROM t WHERE poc_name IS NULL", ConditionOperator.Null)]
    [InlineData("DELETE FROM t WHERE poc_name IS NOT NULL", ConditionOperator.NotNull)]
    [InlineData("DELETE FROM t WHERE poc_name LIKE 'A%'", ConditionOperator.Like)]
    [InlineData("DELETE FROM t WHERE poc_quantity <> 3", ConditionOperator.NotEqual)]
    public void Delete_operators(string sql, ConditionOperator op)
    {
        var cmd = BulkDeleteExecutor.TryParseDelete(sql);
        Assert.NotNull(cmd);
        Assert.Equal(op, cmd!.Criteria.Conditions[0].Operator);
    }

    [Fact]
    public void Delete_guid_value_is_typed()
    {
        var g = System.Guid.NewGuid();
        var cmd = BulkDeleteExecutor.TryParseDelete($"DELETE FROM poc_widget WHERE poc_accountid = {g}");
        Assert.NotNull(cmd);
        Assert.Equal(g, cmd!.Criteria.Conditions[0].Values[0]);
    }

    [Fact]
    public void String_value_with_and_is_not_split()
    {
        var cmd = BulkDeleteExecutor.TryParseDelete("DELETE FROM t WHERE poc_name = 'black and white'");
        Assert.NotNull(cmd);
        var c = Assert.Single(cmd!.Criteria.Conditions);
        Assert.Equal("black and white", c.Values[0]);
    }

    [Theory]
    [InlineData("DELETE FROM t WHERE a = 1 OR b = 2")]      // OR は安全側で非対応
    [InlineData("DELETE FROM t WHERE lower(a) = 'x'")]       // 関数は非対応
    [InlineData("DELETE FROM t WHERE (a = 1 AND b = 2)")]    // 括弧は非対応
    [InlineData("UPDATE t SET a = 1")]                        // DELETE でない
    [InlineData("SELECT * FROM t")]
    public void Unparseable_or_unsafe_where_returns_null(string sql)
        => Assert.Null(BulkDeleteExecutor.TryParseDelete(sql));
}
