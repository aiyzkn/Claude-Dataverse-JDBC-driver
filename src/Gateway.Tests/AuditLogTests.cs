using Gateway.Dataverse;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gateway.Tests;

/// <summary>監査ログ(NFR)の CSV/Markdown 出力とオプション要約。</summary>
public class AuditLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"audit_{Guid.NewGuid():N}");

    private static AuditRecord Sample(long total = 100, long written = 90, long skipped = 5, long failed = 5)
        => new(new DateTime(2026, 7, 7, 0, 0, 0, DateTimeKind.Utc),
               new DateTime(2026, 7, 7, 0, 0, 10, DateTimeKind.Utc),
               "poc_gadget", "Insert", "CreateMultiple", total, written, skipped, failed, "bypass=SyncPlugins", null);

    [Fact]
    public void Csv_appends_header_then_rows()
    {
        var audit = new AuditLog(_dir, NullLogger<AuditLog>.Instance);
        audit.Record(Sample(), "job1");
        audit.Record(Sample(), "job2");
        var csv = Directory.GetFiles(_dir, "audit_*.csv").Single();
        var lines = File.ReadAllLines(csv);
        Assert.StartsWith("start_utc,end_utc,duration_sec,table,operation,route", lines[0]);
        Assert.Equal(3, lines.Length); // ヘッダ + 2 行
        Assert.Contains("poc_gadget", lines[1]);
    }

    [Fact]
    public void Markdown_summary_written_per_job()
    {
        var audit = new AuditLog(_dir, NullLogger<AuditLog>.Instance);
        audit.Record(Sample(), "jobX");
        var md = File.ReadAllText(Path.Combine(_dir, "job_jobX.md"));
        Assert.Contains("移行ジョブ実施報告", md);
        Assert.Contains("| 新規書き込み | 90 |", md);
        Assert.Contains("| スキップ(再開/追跡) | 5 |", md);
        Assert.Contains("bypass=SyncPlugins", md);
    }

    [Fact]
    public void Duration_and_throughput_computed()
    {
        var r = Sample(written: 100);
        Assert.Equal(10, r.DurationSec);
        Assert.Equal(10, r.RowsPerSec); // 100 rows / 10s
    }

    [Fact]
    public void Options_description_summarizes_flags()
    {
        var o = new PreparedInsertExecutor.WriteOptions(BypassSyncPlugins: true, BypassFlows: true, JobId: "m1");
        var s = PreparedInsertExecutor.DescribeOptions(o);
        Assert.Contains("bypass=SyncPlugins/Flows", s);
        Assert.Contains("jobId=m1", s);
        Assert.Equal("(既定)", PreparedInsertExecutor.DescribeOptions(PreparedInsertExecutor.WriteOptions.None));
    }

    [Fact]
    public void Csv_formula_injection_is_neutralized()
    {
        // H-3: 監査 CSV も数式記号始まりの値を無害化する(table/options 等)。
        var audit = new AuditLog(_dir, NullLogger<AuditLog>.Instance);
        var r = new AuditRecord(new DateTime(2026, 7, 7, 0, 0, 0, DateTimeKind.Utc),
                                new DateTime(2026, 7, 7, 0, 0, 10, DateTimeKind.Utc),
                                "=danger", "Insert", "CreateMultiple", 1, 1, 0, 0, "@cmd", null);
        audit.Record(r, "job_fi");
        var csv = Directory.GetFiles(_dir, "audit_*.csv").Single();
        var text = File.ReadAllText(csv);
        Assert.Contains("'=danger", text); // table 列
        Assert.Contains("'@cmd", text);     // options 列
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) try { Directory.Delete(_dir, true); } catch { }
    }
}
