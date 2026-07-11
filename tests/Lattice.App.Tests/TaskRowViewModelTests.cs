using System.Globalization;
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
        var hostId = Guid.NewGuid();
        var r = TestData.MakeResult() with
        {
            ActiveTask = new ActiveTask(1, 0.42, 10, 90),
            SuspendedViaGui = false,
        };
        var row = TaskRowViewModel.From(Snap(r), hostId, "office-pc");
        Assert.Equal(TaskStateKind.Running, row.StateKind);
        Assert.Equal(Strings.TaskStateRunning, row.StateText);
        Assert.Equal(0.42, row.Fraction);
        Assert.Equal("42%", row.PercentText);
        Assert.Equal("office-pc", row.Host);
    }

    [Fact]
    public void Suspended_beats_running()
    {
        var hostId = Guid.NewGuid();
        var r = TestData.MakeResult() with
        {
            ActiveTask = new ActiveTask(1, 0.42, 10, 90),
            SuspendedViaGui = true,
        };
        Assert.Equal(TaskStateKind.Suspended, TaskRowViewModel.From(Snap(r), hostId, "h").StateKind);
    }

    [Fact]
    public void Unknown_fraction_renders_dash_never_indeterminate()
    {
        var hostId = Guid.NewGuid();
        var r = TestData.MakeResult() with { ActiveTask = null };
        var row = TaskRowViewModel.From(Snap(r), hostId, "h");
        Assert.Null(row.Fraction);
        Assert.Equal("—", row.PercentText);
    }

    [Fact]
    public void Uploading_task_without_active_slot_shows_full_fraction()
    {
        var hostId = Guid.NewGuid();
        var r = TestData.MakeResult() with { ActiveTask = null, State = ResultState.FilesUploading };
        var row = TaskRowViewModel.From(Snap(r), hostId, "h");
        Assert.Equal(TaskStateKind.Uploading, row.StateKind);
        Assert.Equal(1.0, row.Fraction);
        Assert.Equal("100%", row.PercentText);
    }

    [Fact]
    public void Deadline_at_risk_flag_passes_through()
    {
        var hostId = Guid.NewGuid();
        var row = TaskRowViewModel.From(Snap(TestData.MakeResult(), atRisk: true), hostId, "h");
        Assert.True(row.IsDeadlineAtRisk);
    }

    public static TheoryData<ResultState, string> WaitingFamilyText => new()
    {
        { ResultState.ComputeError, Strings.TaskStateError },
        { ResultState.Aborted, Strings.TaskStateAborted },
        { ResultState.FilesDownloaded, Strings.TaskStateWaiting },
    };

    [Theory]
    [MemberData(nameof(WaitingFamilyText))]
    public void Waiting_family_text_reflects_underlying_state(ResultState state, string expected)
    {
        var hostId = Guid.NewGuid();
        var r = TestData.MakeResult() with { ActiveTask = null, State = state };
        var row = TaskRowViewModel.From(Snap(r), hostId, "h");
        Assert.Equal(TaskStateKind.Waiting, row.StateKind);
        Assert.Equal(expected, row.StateText);
    }

    [Fact]
    public void Remaining_shows_duration_when_estimated_else_dash()
    {
        var hostId = Guid.NewGuid();
        var estimated = TaskRowViewModel.From(Snap(TestData.MakeResult(estRemaining: 200)), hostId, "h");
        Assert.Equal("3m 20s", estimated.RemainingText);

        var none = TaskRowViewModel.From(Snap(TestData.MakeResult(estRemaining: 0)), hostId, "h");
        Assert.Equal("—", none.RemainingText);
    }

    [Fact]
    public void Deadline_renders_local_time_or_dash()
    {
        var hostId = Guid.NewGuid();
        var deadline = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
        var row = TaskRowViewModel.From(Snap(TestData.MakeResult(deadline: deadline)), hostId, "h");
        Assert.Equal(
            deadline.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture),
            row.DeadlineText);
        Assert.Equal(deadline, row.Deadline);

        var none = TaskRowViewModel.From(Snap(TestData.MakeResult(deadline: null)), hostId, "h");
        Assert.Equal("—", none.DeadlineText);
        Assert.Null(none.Deadline);
    }

    [Fact]
    public void Deadline_renders_gregorian_under_non_gregorian_culture()
    {
        var hostId = Guid.NewGuid();
        var original = CultureInfo.CurrentCulture;
        try
        {
            // ar-SA defaults to the Um Al-Qura (Hijri) calendar, whose month/day
            // differ from Gregorian. (th-TH's Buddhist calendar only shifts the
            // year, which "MM-dd HH:mm" never shows — it cannot catch this bug.)
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
            var deadline = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
            var row = TaskRowViewModel.From(Snap(TestData.MakeResult(deadline: deadline)), hostId, "h");
            Assert.Equal(
                deadline.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture),
                row.DeadlineText);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData(45, "45s")]
    [InlineData(3 * 60 + 20, "3m 20s")]
    [InlineData(2 * 3600 + 5 * 60, "2h 05m")]
    [InlineData(26 * 3600, "1d 02h")]
    public void Durations_format_per_design(double seconds, string expected) =>
        Assert.Equal(expected, TimeText.Duration(seconds));

    [Fact]
    public void From_carries_host_id_and_key()
    {
        var hostId = Guid.NewGuid();
        var row = TaskRowViewModel.From(Snap(TestData.MakeResult()), hostId, "host-a");

        Assert.Equal(hostId, row.HostId);
        Assert.Equal(new TaskRowKey(hostId, row.Name), row.Key);
    }

    [Fact]
    public void Key_equality_is_by_host_and_name()
    {
        var hostId = Guid.NewGuid();
        Assert.Equal(new TaskRowKey(hostId, "wu_1"), new TaskRowKey(hostId, "wu_1"));
        Assert.NotEqual(new TaskRowKey(hostId, "wu_1"), new TaskRowKey(Guid.NewGuid(), "wu_1"));
        Assert.NotEqual(new TaskRowKey(hostId, "wu_1"), new TaskRowKey(hostId, "wu_2"));
    }
}
