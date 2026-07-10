using Lattice.App.Infrastructure;
using Lattice.App.Localization;
using Lattice.App.ViewModels;
using Lattice.Boinc.GuiRpc;
using Lattice.Core;
using Lattice.Tests;
using Xunit;

namespace Lattice.App.Tests;

public class TaskRowViewModelTests
{
    private static TaskSnapshot Snap(Result r, bool atRisk = false, double elapsed = 90) =>
        new(r, "SETI", "astropulse", elapsed, atRisk);

    [Fact]
    public void Running_task_maps_to_running_kind_with_progress()
    {
        var r = TestData.MakeResult() with
        {
            ActiveTask = new ActiveTask(1, 0.42, 10, 90),
            SuspendedViaGui = false,
        };
        var row = TaskRowViewModel.From(Snap(r), "office-pc");
        Assert.Equal(TaskStateKind.Running, row.StateKind);
        Assert.Equal(Strings.TaskStateRunning, row.StateText);
        Assert.Equal(0.42, row.Fraction);
        Assert.Equal("42%", row.PercentText);
        Assert.Equal("office-pc", row.Host);
    }

    [Fact]
    public void Suspended_beats_running()
    {
        var r = TestData.MakeResult() with
        {
            ActiveTask = new ActiveTask(1, 0.42, 10, 90),
            SuspendedViaGui = true,
        };
        Assert.Equal(TaskStateKind.Suspended, TaskRowViewModel.From(Snap(r), "h").StateKind);
    }

    [Fact]
    public void Unknown_fraction_renders_dash_never_indeterminate()
    {
        var r = TestData.MakeResult() with { ActiveTask = null };
        var row = TaskRowViewModel.From(Snap(r), "h");
        Assert.Null(row.Fraction);
        Assert.Equal("—", row.PercentText);
    }

    [Fact]
    public void Uploading_task_without_active_slot_shows_full_fraction()
    {
        var r = TestData.MakeResult() with { ActiveTask = null, State = ResultState.FilesUploading };
        var row = TaskRowViewModel.From(Snap(r), "h");
        Assert.Equal(TaskStateKind.Uploading, row.StateKind);
        Assert.Equal(1.0, row.Fraction);
        Assert.Equal("100%", row.PercentText);
    }

    [Fact]
    public void Deadline_at_risk_flag_passes_through()
    {
        var row = TaskRowViewModel.From(Snap(TestData.MakeResult(), atRisk: true), "h");
        Assert.True(row.IsDeadlineAtRisk);
    }

    [Theory]
    [InlineData(45, "45s")]
    [InlineData(3 * 60 + 20, "3m 20s")]
    [InlineData(2 * 3600 + 5 * 60, "2h 05m")]
    [InlineData(26 * 3600, "1d 02h")]
    public void Durations_format_per_design(double seconds, string expected) =>
        Assert.Equal(expected, TimeText.Duration(seconds));
}
