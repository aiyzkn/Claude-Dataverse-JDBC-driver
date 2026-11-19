using Gateway.Dataverse;
using Xunit;

namespace Gateway.Tests;

/// <summary>失敗行レポート(F-14)の仕様。</summary>
public class FailureReportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"fr_{Guid.NewGuid():N}");

    [Fact]
    public void Empty_report_writes_nothing()
    {
        var r = new FailureReport();
        Assert.Null(r.WriteCsv(_dir, "job"));
        Assert.Equal(0, r.Count);
    }

    [Fact]
    public void Rows_are_ordered_by_index_and_csv_written()
    {
        var r = new FailureReport();
        r.Add(5, "poc_gadget", "STRING_TOO_LONG");
        r.Add(2, "poc_gadget", "value, with comma");
        var path = r.WriteCsv(_dir, "job1");
        Assert.NotNull(path);
        var lines = File.ReadAllLines(path!);
        Assert.Equal("row_index,table,column,error", lines[0]);
        Assert.StartsWith("2,poc_gadget,,", lines[1]);      // 行番号順・列名は空
        Assert.Contains("\"value, with comma\"", lines[1]); // カンマはクオート
        Assert.StartsWith("5,poc_gadget,,", lines[2]);
    }

    [Fact]
    public void Column_is_recorded_and_written()
    {
        var r = new FailureReport();
        r.Add(7, "poc_gadget", "poc_name", "STRING_TOO_LONG for poc_name");
        var path = r.WriteCsv(_dir, "job2");
        var lines = File.ReadAllLines(path!);
        Assert.Equal("row_index,table,column,error", lines[0]);
        Assert.StartsWith("7,poc_gadget,poc_name,", lines[1]); // 原因列が入る
        Assert.Equal("poc_name", r.Rows[0].Column);
    }

    [Fact]
    public void Csv_formula_injection_is_neutralized()
    {
        // H-3: 数式記号で始まる値(移行元データ由来の値/エラー文)は先頭に ' を付与し Excel での数式評価を防ぐ。
        var r = new FailureReport();
        r.Add(1, "poc_gadget", "col", "=cmd|'/c calc'!A1");
        var path = r.WriteCsv(_dir, "job_fi");
        var lines = File.ReadAllLines(path!);
        Assert.Contains("'=cmd", lines[1]);       // ' 前置で無害化
        Assert.DoesNotContain(",=cmd", lines[1]); // 生の =cmd では出力されない
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) try { Directory.Delete(_dir, true); } catch { }
    }
}
