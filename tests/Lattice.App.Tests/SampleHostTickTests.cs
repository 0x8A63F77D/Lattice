#if DEBUG
using System;
using System.Linq;
using Lattice.App.Infrastructure;
using Lattice.App.Localization;
using Lattice.App.ViewModels;
using Lattice.Boinc.GuiRpc;
using Xunit;

namespace Lattice.App.Tests;

// The opt-in DEBUG live-progress aid (LATTICE_SAMPLE_TICK): the pure per-poll advance helpers
// that make the canned fleet's ACTIVE work climb so the Wave-3 progress-width motion is
// eyeball-able on the live app. Only active items move; the loop-at-top keeps a full bar animating
// instead of pinning. Pure functions → unit-tested directly, no env/monitor needed.
public class SampleHostTickTests
{
    private static FileTransfer Transfer(double fraction, bool xferActive)
    {
        const double nbytes = 100.0;
        return new FileTransfer(
            "f", "https://p/", "P", Nbytes: nbytes, Status: 0, IsUpload: false, NumRetries: 0,
            FirstRequestTime: null, NextRequestTime: null, TimeSoFar: 0,
            BytesXferred: nbytes * fraction, FileOffset: nbytes * fraction,
            XferSpeed: 0, ProjectBackoff: 0, PersXferActive: xferActive, XferActive: xferActive);
    }

    private static Result Task(ActiveTask? active) =>
        new("t", "wu", "https://p/", ResultState.FilesDownloaded, null, false, false,
            0, 0, 0, 1, "", 0, active);

    [Fact]
    public void AdvanceTransfer_climbs_an_active_transfer()
    {
        var next = SampleHost.AdvanceTransfer(Transfer(0.30, xferActive: true));
        Assert.True(next.BytesXferred > 30.0, "an active transfer's progress should climb each poll");
        Assert.Equal(next.BytesXferred, next.FileOffset); // offset tracks the bytes
    }

    [Fact]
    public void AdvanceTransfer_loops_instead_of_pinning_full()
    {
        var next = SampleHost.AdvanceTransfer(Transfer(0.98, xferActive: true));
        Assert.True(next.BytesXferred < 98.0, "a near-full active transfer should loop, not pin at 100%");
    }

    [Fact]
    public void AdvanceTransfer_leaves_a_non_active_transfer_untouched()
    {
        var queued = Transfer(0.0, xferActive: false);
        Assert.Same(queued, SampleHost.AdvanceTransfer(queued));
    }

    [Fact]
    public void AdvanceResult_climbs_a_running_task_and_leaves_idle_ones()
    {
        var running = SampleHost.AdvanceResult(Task(new ActiveTask(1, 0.40, 0, 0)));
        Assert.True(running.ActiveTask!.FractionDone > 0.40);

        var idle = Task(active: null);
        Assert.Same(idle, SampleHost.AdvanceResult(idle));
    }

    // The canned "Running" rows must render the fine "Running" status, not "Ready to
    // start": TaskStatusPolicy keys that off scheduler_state, so the sample data has to
    // report SCHEDULED (as a real daemon does) — regression pin for Codex R3 P3.
    [Fact]
    public void Sample_running_tasks_render_running_status()
    {
        var alpha = SampleHost.BuildHosts(DateTimeOffset.UnixEpoch)[0];
        var running = alpha.Results.First(r => r.ActiveTask?.ActiveTaskState == 1);
        Assert.Equal(SchedulerState.Scheduled, running.ActiveTask!.SchedulerState);
        Assert.Equal(Strings.TaskStateRunning, TaskStatusPolicy.Text(running, alpha.Status));
    }

    // Default (no LATTICE_SAMPLE_TICK) is unchanged from PR F: the live client replays the canned
    // lists every poll, so a run without the aid is byte-for-byte the static fleet it always was.
    [Fact]
    public async Task Without_the_tick_flag_the_live_client_replays_canned_data_unchanged()
    {
        Assert.False(SampleHost.Ticking, "this guard assumes LATTICE_SAMPLE_TICK is unset in the test process");
        var data = SampleHost.BuildHosts(DateTimeOffset.UnixEpoch)[0];
        var client = new SampleGuiRpcClient(data);

        var first = await client.GetFileTransfersAsync();
        var second = await client.GetFileTransfersAsync();

        Assert.Same(data.Transfers, first);
        Assert.Same(data.Transfers, second);
    }
}
#endif
