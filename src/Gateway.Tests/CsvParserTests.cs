using Gateway.Dataverse;
using Xunit;

namespace Gateway.Tests;

/// <summary>dvload CSV パーサ(3.1.3): 引用符・エスケープ・埋め込み改行・欠損セル。</summary>
public class CsvParserTests
{
    [Fact]
    public void Basic_header_and_rows()
    {
        var (h, rows) = CsvParser.Read("poc_name,poc_quantity\nA,1\nB,2\n");
        Assert.Equal(new[] { "poc_name", "poc_quantity" }, h);
        Assert.Equal(2, rows.Count);
        Assert.Equal(new string?[] { "A", "1" }, rows[0]);
        Assert.Equal(new string?[] { "B", "2" }, rows[1]);
    }

    [Fact]
    public void Quoted_field_with_embedded_comma()
    {
        var (_, rows) = CsvParser.Read("name,note\n\"Contoso, Inc.\",hello\n");
        Assert.Equal("Contoso, Inc.", rows[0][0]);
        Assert.Equal("hello", rows[0][1]);
    }

    [Fact]
    public void Escaped_double_quote()
    {
        var (_, rows) = CsvParser.Read("name\n\"say \"\"hi\"\"\"\n");
        Assert.Equal("say \"hi\"", rows[0][0]);
    }

    [Fact]
    public void Embedded_newline_in_quotes()
    {
        var (_, rows) = CsvParser.Read("name,memo\nX,\"line1\nline2\"\n");
        Assert.Single(rows);
        Assert.Equal("line1\nline2", rows[0][1]);
    }

    [Fact]
    public void Empty_cell_becomes_null()
    {
        var (_, rows) = CsvParser.Read("a,b,c\n1,,3\n");
        Assert.Null(rows[0][1]);
        Assert.Equal("1", rows[0][0]);
        Assert.Equal("3", rows[0][2]);
    }

    [Fact]
    public void Last_row_without_trailing_newline()
    {
        var (_, rows) = CsvParser.Read("a\n1\n2");
        Assert.Equal(2, rows.Count);
        Assert.Equal("2", rows[1][0]);
    }

    [Fact]
    public void Short_row_is_padded_with_null()
    {
        var (_, rows) = CsvParser.Read("a,b,c\n1,2\n");
        Assert.Equal(new string?[] { "1", "2", null }, rows[0]);
    }

    [Fact]
    public void Crlf_line_endings()
    {
        var (h, rows) = CsvParser.Read("a,b\r\n1,2\r\n");
        Assert.Equal(new[] { "a", "b" }, h);
        Assert.Equal(new string?[] { "1", "2" }, rows[0]);
    }

    [Fact]
    public void Custom_delimiter()
    {
        var (h, rows) = CsvParser.Read("a\tb\n1\t2\n", delimiter: '\t');
        Assert.Equal(new[] { "a", "b" }, h);
        Assert.Equal(new string?[] { "1", "2" }, rows[0]);
    }

    [Fact]
    public void Empty_text_returns_empty()
    {
        var (h, rows) = CsvParser.Read("");
        Assert.Empty(h);
        Assert.Empty(rows);
    }
}
