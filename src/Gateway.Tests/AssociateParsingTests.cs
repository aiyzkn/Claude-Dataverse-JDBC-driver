using Gateway.Dataverse;
using Xunit;

namespace Gateway.Tests;

/// <summary>AssociateExecutor.TryParse の N:N 構文解析仕様(D-13/F-07)。</summary>
public class AssociateParsingTests
{
    [Fact]
    public void Intersect_insert_is_associate()
    {
        var cmd = AssociateExecutor.TryParse(
            "INSERT INTO poc_gadget_poc_widget (record1id, record2id) VALUES (?, ?)");
        Assert.NotNull(cmd);
        Assert.Equal(AssociateExecutor.AssocVerb.Associate, cmd!.Verb);
        Assert.Equal("poc_gadget_poc_widget", cmd.Relationship);
    }

    [Fact]
    public void Associate_statement_parsed()
    {
        var cmd = AssociateExecutor.TryParse("ASSOCIATE poc_gadget_poc_widget (?, ?)");
        Assert.NotNull(cmd);
        Assert.Equal(AssociateExecutor.AssocVerb.Associate, cmd!.Verb);
    }

    [Fact]
    public void Disassociate_statement_parsed()
    {
        var cmd = AssociateExecutor.TryParse("DISASSOCIATE poc_gadget_poc_widget (?, ?)");
        Assert.NotNull(cmd);
        Assert.Equal(AssociateExecutor.AssocVerb.Disassociate, cmd!.Verb);
    }

    [Fact]
    public void Regular_insert_is_not_association()
    {
        // record1id/record2id でない通常 INSERT は N:N ではない(PreparedInsertExecutor が扱う)
        Assert.Null(AssociateExecutor.TryParse(
            "INSERT INTO poc_gadget (poc_name, poc_quantity) VALUES (?, ?)"));
    }

    [Fact]
    public void Intersect_insert_with_non_parameter_values_rejected()
        => Assert.Null(AssociateExecutor.TryParse(
            "INSERT INTO rel (record1id, record2id) VALUES (?, 'x')"));

    [Fact]
    public void Parameter_columns_are_record1id_record2id()
        => Assert.Equal(new[] { "record1id", "record2id" }, AssociateExecutor.ParameterColumns);
}
