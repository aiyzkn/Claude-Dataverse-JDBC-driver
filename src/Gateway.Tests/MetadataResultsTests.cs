using Gateway.Flight;
using Xunit;

namespace Gateway.Tests;

/// <summary>メタデータ/SqlInfo 応答のスキーマ・行数の仕様。</summary>
public class MetadataResultsTests
{
    [Fact]
    public void SqlInfo_empty_has_zero_rows_and_expected_schema()
    {
        var batch = SqlInfoResult.Empty();
        Assert.Equal(0, batch.Length);
        Assert.Equal("info_name", batch.Schema.FieldsList[0].Name);
        Assert.Equal("value", batch.Schema.FieldsList[1].Name);
    }

    [Fact]
    public void Tables_without_schema_has_four_columns()
    {
        var rows = new[] { new MetadataResults.TableRow("poc_gadget", "TABLE", null) };
        var batch = MetadataResults.Tables(rows, includeSchema: false);
        Assert.Equal(1, batch.Length);
        Assert.Equal(4, batch.Schema.FieldsList.Count);
        Assert.Equal("table_name", batch.Schema.FieldsList[2].Name);
    }

    [Fact]
    public void Tables_with_schema_adds_table_schema_column()
    {
        var rows = new[] { new MetadataResults.TableRow("poc_gadget", "TABLE", new byte[] { 1, 2, 3 }) };
        var batch = MetadataResults.Tables(rows, includeSchema: true);
        Assert.Equal(5, batch.Schema.FieldsList.Count);
        Assert.Equal("table_schema", batch.Schema.FieldsList[4].Name);
    }

    [Fact]
    public void TableTypes_has_single_table_row()
    {
        var batch = MetadataResults.TableTypes();
        Assert.Equal(1, batch.Length);
        Assert.Equal("table_type", batch.Schema.FieldsList[0].Name);
    }

    [Fact]
    public void SchemaSerializer_produces_nonempty_bytes()
    {
        var bytes = SchemaSerializer.Serialize(MetadataResults.TableTypesSchema);
        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
    }
}
