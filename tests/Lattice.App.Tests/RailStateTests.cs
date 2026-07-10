using Lattice.App.Infrastructure;
using Lattice.App.Localization;
using Lattice.App.Tests.Fakes;
using Lattice.App.ViewModels;
using Lattice.Core;
using Lattice.Tests;
using Xunit;

namespace Lattice.App.Tests;

public class RailStateTests
{
    private static ConnectionStatus Status(
        HostConnectionState state, int attempt = 0, DateTimeOffset? nextAt = null, string? error = null) =>
        new(Guid.NewGuid(), state, attempt, nextAt, error, null);

    [Theory]
    [InlineData(HostConnectionState.Disconnected, 0, RailState.Connecting)]
    [InlineData(HostConnectionState.Connecting, 0, RailState.Connecting)]
    [InlineData(HostConnectionState.Authorizing, 0, RailState.Connecting)]
    [InlineData(HostConnectionState.FetchingState, 0, RailState.Connecting)]
    [InlineData(HostConnectionState.Connected, 0, RailState.Connected)]
    [InlineData(HostConnectionState.Retrying, 1, RailState.Retrying)]
    [InlineData(HostConnectionState.Retrying, 3, RailState.Retrying)]
    [InlineData(HostConnectionState.Retrying, 4, RailState.Unreachable)]
    [InlineData(HostConnectionState.Retrying, 12, RailState.Unreachable)]
    [InlineData(HostConnectionState.AuthFailed, 1, RailState.AuthFailed)]
    public void Projection_covers_all_core_states(HostConnectionState state, int attempt, RailState expected) =>
        Assert.Equal(expected, RailStateProjection.From(Status(state, attempt)));

    [Fact]
    public void Projection_is_total_over_core_states()
    {
        foreach (var state in Enum.GetValues<HostConnectionState>())
            _ = RailStateProjection.From(Status(state, attempt: 1));
    }

    [Fact]
    public void Connected_item_shows_task_count_subtext()
    {
        var entry = MakeEntry(Status(HostConnectionState.Connected));
        entry.Snapshot = SnapshotWithTasks(entry.Config.Id, taskCount: 3);
        var vm = new HostRailItemViewModel(entry, new ManualUiClock());
        vm.Refresh();
        Assert.Equal(string.Format(Strings.RailConnectedFmt, 3), vm.StateText);
    }

    [Fact]
    public void Retrying_item_counts_down_on_clock_tick()
    {
        var clock = new ManualUiClock();
        var entry = MakeEntry(Status(
            HostConnectionState.Retrying, attempt: 3, nextAt: clock.Now.AddSeconds(12), error: "boom"));
        var vm = new HostRailItemViewModel(entry, clock);
        vm.Refresh();
        Assert.Equal(string.Format(Strings.RailRetryingFmt, 12, 3), vm.StateText);
        Assert.Equal($"office-pc — {string.Format(Strings.RailRetryingFmt, 12, 3)}\nboom", vm.Tooltip);

        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(string.Format(Strings.RailRetryingFmt, 7, 3), vm.StateText);
    }

    // The compact rail hides the text entirely, so the tooltip is the only way
    // to tell identical state icons apart — it must carry name + subtext in
    // EVERY state, not just the error states (Codex P2).
    [Fact]
    public void Tooltip_identifies_the_host_in_every_state()
    {
        var connected = MakeEntry(Status(HostConnectionState.Connected));
        connected.Snapshot = SnapshotWithTasks(connected.Config.Id, taskCount: 3);
        var connectedVm = new HostRailItemViewModel(connected, new ManualUiClock());
        connectedVm.Refresh();
        Assert.Equal($"office-pc — {string.Format(Strings.RailConnectedFmt, 3)}", connectedVm.Tooltip);

        var connecting = new HostRailItemViewModel(
            MakeEntry(Status(HostConnectionState.Connecting)), new ManualUiClock());
        connecting.Refresh();
        Assert.Equal($"office-pc — {Strings.RailConnecting}", connecting.Tooltip);

        var authFailed = new HostRailItemViewModel(
            MakeEntry(Status(HostConnectionState.AuthFailed, 1)), new ManualUiClock());
        authFailed.Refresh();
        Assert.Equal($"office-pc — {Strings.RailAuthFailed}", authFailed.Tooltip);

        var unreachable = new HostRailItemViewModel(
            MakeEntry(Status(HostConnectionState.Retrying, attempt: 5, error: "no route")), new ManualUiClock());
        unreachable.Refresh();
        Assert.Equal($"office-pc — {Strings.RailUnreachable}\nno route", unreachable.Tooltip);
    }

    [Fact]
    public void AuthFailed_item_says_wrong_password()
    {
        var vm = new HostRailItemViewModel(MakeEntry(Status(HostConnectionState.AuthFailed, 1)), new ManualUiClock());
        vm.Refresh();
        Assert.Equal(RailState.AuthFailed, vm.State);
        Assert.Equal(Strings.RailAuthFailed, vm.StateText);
    }

    private static HostEntry MakeEntry(ConnectionStatus status) =>
        new(TestData.MakeHostConfig(id: status.HostId, name: "office-pc"), status);

    private static HostSnapshot SnapshotWithTasks(Guid hostId, int taskCount)
    {
        // Signature verified against src/Lattice.Boinc.GuiRpc/Models/Result.cs;
        // only the task COUNT matters to the assertion.
        var result = new Lattice.Boinc.GuiRpc.Result(
            Name: "task_0", WorkunitName: "wu_0", ProjectUrl: "http://proj.example/",
            State: Lattice.Boinc.GuiRpc.ResultState.FilesDownloaded,
            ReportDeadline: DateTimeOffset.Now.AddDays(1), ReadyToReport: false,
            SuspendedViaGui: false, FinalCpuTime: 0, FinalElapsedTime: 0,
            EstimatedCpuTimeRemaining: 100, VersionNum: 1, PlanClass: "",
            ExitStatus: 0, ActiveTask: null);
        var tasks = Enumerable.Range(0, taskCount)
            .Select(_ => new TaskSnapshot(result, "P", "A", 100, false))
            .ToList();
        return new HostSnapshot(hostId, "office-pc", DateTimeOffset.Now,
            FakeGuiRpcClient.DefaultStatus, tasks, [], []);
    }
}
