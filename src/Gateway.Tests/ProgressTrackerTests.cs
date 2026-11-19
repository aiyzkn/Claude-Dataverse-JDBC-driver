using Gateway.Dataverse;
using Xunit;
using Entry = Gateway.Dataverse.ProgressTracker.Entry;

namespace Gateway.Tests;

/// <summary>進捗トラッキング(3.1.5): ETA/割合の算出とライフサイクル。</summary>
public class ProgressTrackerTests
{
    [Fact]
    public void Eta_and_percent_when_total_known()
    {
        var e = new Entry("id", "account", "Insert", Done: 50, Total: 100, ElapsedSec: 10.0, Status: "running");
        Assert.Equal(5.0, e.RowsPerSec, 3);
        Assert.Equal(10.0, e.EtaSec!.Value, 3); // 残り50行 ÷ 5行/秒
        Assert.Equal(50, e.Percent);
    }

    [Fact]
    public void Eta_zero_when_done_reaches_total()
    {
        var e = new Entry("id", "t", "Insert", Done: 100, Total: 100, ElapsedSec: 20.0, Status: "completed");
        Assert.Equal(0, e.EtaSec);
        Assert.Equal(100, e.Percent);
    }

    [Fact]
    public void Eta_and_percent_null_when_total_unknown()
    {
        var e = new Entry("id", "t", "Insert", Done: 500, Total: null, ElapsedSec: 5.0, Status: "running");
        Assert.Null(e.EtaSec);
        Assert.Null(e.Percent);
        Assert.Equal(100.0, e.RowsPerSec, 3);
    }

    [Fact]
    public void Eta_null_when_no_progress_yet()
    {
        var e = new Entry("id", "t", "Insert", Done: 0, Total: 100, ElapsedSec: 0.0, Status: "running");
        Assert.Null(e.EtaSec); // レート 0 → 予測不能
    }

    [Fact]
    public void Lifecycle_begin_report_complete_snapshot()
    {
        var t = new ProgressTracker();
        var id = t.Begin("account", "Insert", 1000);
        t.Report(id, 400);
        var running = t.Snapshot().Single(e => e.Id == id);
        Assert.Equal("running", running.Status);
        Assert.Equal(400, running.Done);
        Assert.Equal(1000, running.Total);

        t.Complete(id);
        Assert.Equal("completed", t.Snapshot().Single(e => e.Id == id).Status);
    }

    [Fact]
    public void Cancel_marks_status_cancelled()
    {
        var t = new ProgressTracker();
        var id = t.Begin("t", "Insert", null);
        t.Complete(id, "cancelled");
        Assert.Equal("cancelled", t.Snapshot().Single(e => e.Id == id).Status);
    }
}
