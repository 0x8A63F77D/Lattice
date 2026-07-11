using System.Collections.Specialized;
using Lattice.App.Infrastructure;
using Lattice.App.Localization;
using Lattice.App.Tests.Fakes;
using Lattice.App.ViewModels;
using Lattice.Boinc.GuiRpc;
using Lattice.Core;
using Lattice.Tests;
using Xunit;

namespace Lattice.App.Tests;

/// <summary>
/// Fixture idiom copied verbatim from TasksViewModelTests: RoutingGuiRpcClient
/// hands out a per-host fake, LockingUiDispatcher serializes concurrent
/// monitor callbacks under a single lock (safe for tests running more than
/// one live monitor at a time).
/// </summary>
public class TransfersViewModelTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}.json");
    private readonly string _uiPath = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}-ui.json");
    private readonly Dictionary<string, FakeGuiRpcClient> _fakes = [];
    private HostRegistry _registry = null!;
    private HostMonitorManager _manager = null!;
    private HostStore _store = null!;
    private ManualUiClock _clock = null!;
    private UiStateStore _uiStore = null!;

    public ValueTask InitializeAsync()
    {
        _registry = new HostRegistry(new LatticeConfig(5, []), _path);
        _manager = new HostMonitorManager(_registry, () => new RoutingGuiRpcClient(_fakes), TimeProvider.System);
        _store = new HostStore(_registry, _manager, new LockingUiDispatcher());
        _clock = new ManualUiClock();
        _uiStore = new UiStateStore(_uiPath);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _manager.DisposeAsync();
        File.Delete(_path);
        File.Delete(_uiPath);
    }

    private HostConfig AddHost(string address, FakeGuiRpcClient fake)
    {
        var host = TestData.MakeHostConfig(name: address, address: address);
        _fakes[address] = fake;
        _registry.AddHost(host);
        return host;
    }

    private TransfersViewModel MakeVm() => new(_store, _clock, _uiStore);

    [Fact]
    public async Task Scoping_to_a_host_hides_the_other_hosts_rows()
    {
        var fakeA = new FakeGuiRpcClient
        {
            OnGetFileTransfers = () => Task.FromResult<IReadOnlyList<FileTransfer>>(
                [TestData.MakeTransfer(name: "file_a")]),
        };
        var fakeB = new FakeGuiRpcClient
        {
            OnGetFileTransfers = () => Task.FromResult<IReadOnlyList<FileTransfer>>(
                [TestData.MakeTransfer(name: "file_b")]),
        };
        var hostA = AddHost("host-a", fakeA);
        AddHost("host-b", fakeB);
        var vm = MakeVm();
        _manager.Start();
        await Wait.UntilAsync(() => vm.Rows.Count == 2);

        Assert.True(vm.IsAllHostsScope);
        Assert.Contains(vm.Rows, r => r.Data.Name == "file_a" && r.Data.Host == "host-a");
        Assert.Contains(vm.Rows, r => r.Data.Name == "file_b" && r.Data.Host == "host-b");

        vm.Scope = new ScopeSelection(hostA.Id);

        Assert.False(vm.IsAllHostsScope);
        Assert.Equal("file_a", Assert.Single(vm.Rows).Data.Name);
    }

    [Fact]
    public async Task Steady_state_poll_raises_no_events_and_holder_identity_survives_a_progress_change()
    {
        const double mb = 1024.0 * 1024.0;
        var t = TestData.MakeTransfer(name: "file_1", nbytes: 100 * mb, bytesXferred: 25 * mb);
        var fake = new FakeGuiRpcClient
        {
            OnGetFileTransfers = () => Task.FromResult<IReadOnlyList<FileTransfer>>([t]),
        };
        AddHost("host-a", fake);
        var vm = MakeVm();
        _manager.Start();
        await Wait.UntilAsync(() => vm.Rows.Count == 1);

        var holder = vm.Rows[0];
        var events = 0;
        vm.Rows.CollectionChanged += (_, _) => events++;

        // Steady-state ticks with identical data: zero CollectionChanged events.
        _clock.Now = _store.Hosts[0].Snapshot!.Timestamp;
        _clock.Advance(TimeSpan.FromSeconds(1));
        _clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(0, events);
        Assert.Same(holder, vm.Rows[0]);
        Assert.Equal("25 / 100 MB", holder.Data.ProgressText);

        // Progress change: same key, holder identity survives, Data updates in place.
        t = TestData.MakeTransfer(name: "file_1", nbytes: 100 * mb, bytesXferred: 75 * mb);
        _store.RequestRefresh(null);
        await Wait.UntilAsync(() => vm.Rows[0].Data.Fraction == 0.75);

        Assert.Same(holder, vm.Rows[0]);
        Assert.Equal("75 / 100 MB", holder.Data.ProgressText);
    }

    [Fact]
    public async Task Retrying_row_status_text_counts_down_as_the_manual_clock_advances()
    {
        var t = TestData.MakeTransfer(
            name: "file_retry", numRetries: 2, nextRequest: DateTimeOffset.UtcNow.AddMinutes(5));
        var fake = new FakeGuiRpcClient
        {
            OnGetFileTransfers = () => Task.FromResult<IReadOnlyList<FileTransfer>>([t]),
        };
        AddHost("host-a", fake);
        var vm = MakeVm();
        _manager.Start();
        await Wait.UntilAsync(() => vm.Rows.Count == 1);

        var nextRequestTime = _store.Hosts[0].Snapshot!.Transfers.Single().Transfer.NextRequestTime!.Value;

        // Sync the manual clock so the countdown reads exactly 10s remaining,
        // independent of real wall-clock timing (the state classification
        // itself was already fixed at poll time by SnapshotBuilder). Setting
        // Now alone does not trigger a Rebuild — only Advance fires Tick — so
        // land one second short first, then Advance onto the checkpoint.
        _clock.Now = nextRequestTime - TimeSpan.FromSeconds(11);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("Retry in 00:10 (attempt 2)", vm.Rows[0].Data.StatusText);

        _clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal("Retry in 00:09 (attempt 2)", vm.Rows[0].Data.StatusText);
    }

    [Fact]
    public async Task Completed_transfer_absent_from_the_next_poll_is_removed_without_a_reset()
    {
        IReadOnlyList<FileTransfer> transfers =
            [TestData.MakeTransfer(name: "file_1"), TestData.MakeTransfer(name: "file_2")];
        var fake = new FakeGuiRpcClient { OnGetFileTransfers = () => Task.FromResult(transfers) };
        AddHost("host-a", fake);
        var vm = MakeVm();
        _manager.Start();
        await Wait.UntilAsync(() => vm.Rows.Count == 2);

        var survivor = vm.Rows.Single(r => r.Data.Name == "file_2");
        var actions = new List<NotifyCollectionChangedAction>();
        vm.Rows.CollectionChanged += (_, e) => actions.Add(e.Action);

        transfers = [TestData.MakeTransfer(name: "file_2")];
        _store.RequestRefresh(null);
        await Wait.UntilAsync(() => vm.Rows.Count == 1);

        Assert.Same(survivor, vm.Rows[0]);
        Assert.Equal("file_2", vm.Rows[0].Data.Name);
        Assert.Equal([NotifyCollectionChangedAction.Remove], actions); // no Reset
    }

    [Fact]
    public async Task IsEmpty_is_true_with_connected_hosts_and_zero_transfers()
    {
        var fake = new FakeGuiRpcClient(); // OnGetFileTransfers defaults to empty
        AddHost("host-a", fake);
        var vm = MakeVm();

        Assert.True(vm.IsLoading);
        Assert.False(vm.IsEmpty);

        _manager.Start();
        await Wait.UntilAsync(() => !vm.IsLoading, "loading should clear once the first snapshot lands");

        Assert.False(vm.IsLoading);
        Assert.True(vm.IsEmpty);
        Assert.Empty(vm.Rows);
    }

    [Fact]
    public async Task CountsText_matches_a_crafted_mix()
    {
        var fake = new FakeGuiRpcClient
        {
            OnGetFileTransfers = () => Task.FromResult<IReadOnlyList<FileTransfer>>(
            [
                TestData.MakeTransfer(name: "u1", isUpload: true),
                TestData.MakeTransfer(name: "u2", isUpload: true),
                TestData.MakeTransfer(name: "d1", isUpload: false),
            ]),
        };
        AddHost("host-a", fake);
        var vm = MakeVm();
        _manager.Start();
        await Wait.UntilAsync(() => vm.Rows.Count == 3);

        // Literal pin (not indirected through Strings.TransfersCountsFmt): the
        // format string itself, not just the arg values, is under test here.
        Assert.Equal("3 transfers · 2 up · 1 down", vm.CountsText);
    }

    [Fact]
    public async Task CountsText_recomputes_when_a_hosts_transfers_drop_out()
    {
        // Mirrors TasksViewModelTests.Stale_snapshot_of_unreachable_host_leaves_rows_and_counts:
        // CountsText covers the reachable set, so it must recompute (not just
        // Rows) once a host's stale snapshot is dropped from the merge.
        var fakeA = new FakeGuiRpcClient
        {
            OnGetFileTransfers = () => Task.FromResult<IReadOnlyList<FileTransfer>>(
                [TestData.MakeTransfer(name: "a_down", isUpload: false)]),
        };
        var fakeB = new FakeGuiRpcClient
        {
            OnGetFileTransfers = () => Task.FromResult<IReadOnlyList<FileTransfer>>(
            [
                TestData.MakeTransfer(name: "b_up1", isUpload: true),
                TestData.MakeTransfer(name: "b_up2", isUpload: true),
            ]),
        };
        AddHost("host-a", fakeA);
        var hostB = AddHost("host-b", fakeB);
        var vm = MakeVm();
        _manager.Start();

        await Wait.UntilAsync(() => vm.Rows.Count == 3, "both hosts should contribute rows");
        Assert.Equal("3 transfers · 2 up · 1 down", vm.CountsText);

        // Kill B the same way the partial-bar tests do — poll failure, then auth
        // failure on reconnect — parking it in AuthFailed with its snapshot cached.
        fakeB.OnGetCcStatus = () => throw new BoincConnectionException("poll boom");
        fakeB.OnAuthorize = _ => Task.FromResult(false);
        _store.RequestRefresh(hostB.Id);
        await Wait.UntilAsync(() =>
            _store.Hosts.Single(h => h.Config.Id == hostB.Id).Status.State == HostConnectionState.Retrying);
        _store.RequestRefresh(hostB.Id); // skip the remaining backoff, retry now
        await Wait.UntilAsync(() =>
            _store.Hosts.Single(h => h.Config.Id == hostB.Id).Status.State == HostConnectionState.AuthFailed);

        await Wait.UntilAsync(() => vm.Rows.Count == 1, "the dead host's stale rows should drop out");
        Assert.Equal("1 transfers · 0 up · 1 down", vm.CountsText);
    }

    [Fact]
    public async Task Partial_bar_shows_dismisses_and_reappears_when_the_unreachable_set_changes()
    {
        // Mirrors TasksViewModelTests' identical-name fact: PartialBarState/
        // PartialBarPolicy is shared machinery (ViewSliceProjection), so the
        // Transfers VM must wire it the same way.
        var fakeA = new FakeGuiRpcClient { OnAuthorize = _ => Task.FromResult(false) };
        var fakeB = new FakeGuiRpcClient();
        var hostA = AddHost("host-a", fakeA);
        var hostB = AddHost("host-b", fakeB);
        var vm = MakeVm();
        _manager.Start();

        await Wait.UntilAsync(() =>
            _store.Hosts.Single(h => h.Config.Id == hostA.Id).Status.State == HostConnectionState.AuthFailed);
        await Wait.UntilAsync(() =>
            _store.Hosts.Single(h => h.Config.Id == hostB.Id).Snapshot is not null);

        await Wait.UntilAsync(
            () => vm.PartialBarText == string.Format(Strings.PartialFmt, 1, 2, 1),
            "partial bar should appear for the AuthFailed host with B's coverage counted");
        Assert.True(vm.ShowPartialBar);
        Assert.False(vm.IsUpdateStale, "AuthFailed is not a Retrying/Unreachable staleness signal");

        vm.DismissPartialCommand.Execute(null);
        Assert.False(vm.ShowPartialBar);

        // Break host B too: fail its next poll, then let it fail auth on
        // reconnect — the id-set grows from {A} to {A, B}.
        fakeB.OnGetCcStatus = () => throw new BoincConnectionException("poll boom");
        fakeB.OnAuthorize = _ => Task.FromResult(false);
        _store.RequestRefresh(hostB.Id);
        await Wait.UntilAsync(() =>
            _store.Hosts.Single(h => h.Config.Id == hostB.Id).Status.State == HostConnectionState.Retrying);
        _store.RequestRefresh(hostB.Id); // skip the remaining backoff, retry now
        await Wait.UntilAsync(() =>
            _store.Hosts.Single(h => h.Config.Id == hostB.Id).Status.State == HostConnectionState.AuthFailed);

        await Wait.UntilAsync(
            () => vm.PartialBarText == string.Format(Strings.PartialFmt, 2, 2, 0),
            "partial bar should reappear: the unreachable id-set changed");
        Assert.True(vm.ShowPartialBar);
    }

    [Fact]
    public async Task Partial_bar_reappears_when_a_dismissed_covered_host_drops_out()
    {
        // Mirrors TasksViewModelTests' identical-name fact (Codex P2, design doc:
        // InfoBar "reappears when the reachable set changes"): the dismissal
        // fingerprint must cover (unreachable set, covered set), not just the
        // unreachable id-set — a covered host (C) dropping below Connected must
        // reopen a dismissed bar even though the unreachable tier stays {A}.
        var fakeA = new FakeGuiRpcClient { OnAuthorize = _ => Task.FromResult(false) };
        var fakeB = new FakeGuiRpcClient
        {
            OnGetFileTransfers = () => Task.FromResult<IReadOnlyList<FileTransfer>>(
                [TestData.MakeTransfer(name: "b_task")]),
        };
        var fakeC = new FakeGuiRpcClient
        {
            OnGetFileTransfers = () => Task.FromResult<IReadOnlyList<FileTransfer>>(
                [TestData.MakeTransfer(name: "c_task")]),
        };
        AddHost("host-a", fakeA);
        AddHost("host-b", fakeB);
        var hostC = AddHost("host-c", fakeC);
        var vm = MakeVm();
        _manager.Start();

        await Wait.UntilAsync(() => vm.Rows.Count == 2, "B and C should contribute rows");
        await Wait.UntilAsync(
            () => vm.PartialBarText == string.Format(Strings.PartialFmt, 1, 3, 2),
            "A parks in AuthFailed while B and C feed the grid");
        Assert.True(vm.ShowPartialBar);

        vm.DismissPartialCommand.Execute(null);
        Assert.False(vm.ShowPartialBar);

        await Wait.UntilAsync(() => fakeC.Calls.Count(c => c == "get_cc_status") > 0,
            "C's first poll should be observed before the test breaks it");

        // Hold C in the Retrying tier (below the Unreachable threshold, so the
        // unreachable id-set never changes): its live poll AND every reconnect
        // attempt throw, so it can only cycle Retrying -> (instant connect
        // failure) -> Retrying, never Connected again.
        fakeC.OnGetCcStatus = () => throw new BoincConnectionException("poll boom");
        fakeC.OnConnect = (_, _) => throw new BoincConnectionException("still down");
        _store.RequestRefresh(hostC.Id);
        await Wait.UntilAsync(() =>
            _store.Hosts.Single(h => h.Config.Id == hostC.Id).Status.State == HostConnectionState.Retrying);
        Assert.NotNull(_store.Hosts.Single(h => h.Config.Id == hostC.Id).Snapshot);

        await Wait.UntilAsync(() => vm.Rows.Count == 1, "C's stale rows should drop out");

        // Unreachable tier is still just {A} — but the bar must reappear
        // because the covered set shrank from {B, C} to {B}.
        await Wait.UntilAsync(
            () => vm.ShowPartialBar
                  && vm.PartialBarText == string.Format(Strings.PartialFmt, 1, 3, 1),
            "the bar must reappear: the covered set shrank even though the unreachable set stayed {A}");
    }

    [Fact]
    public async Task Partial_bar_reappears_when_the_same_host_fails_after_recovering()
    {
        // Mirrors TasksViewModelTests' identical-name fact (Codex P2 round 2):
        // dismissal must not survive a full recovery — a fresh outage with the
        // same id-set as a dismissed, since-recovered outage must be reported.
        var fakeA = new FakeGuiRpcClient();
        var fakeB = new FakeGuiRpcClient();
        AddHost("host-a", fakeA);
        var hostB = AddHost("host-b", fakeB);
        var vm = MakeVm();
        _manager.Start();
        await Wait.UntilAsync(() =>
            _store.Hosts.Single(h => h.Config.Id == hostB.Id).Status.State == HostConnectionState.Connected);
        await Wait.UntilAsync(() => fakeB.Calls.Count(c => c == "get_cc_status") > 0,
            "B's first poll should be observed before the test breaks it");

        // Break B: poll failure, then auth failure on reconnect -> AuthFailed.
        fakeB.OnGetCcStatus = () => throw new BoincConnectionException("poll boom");
        fakeB.OnAuthorize = _ => Task.FromResult(false);
        _store.RequestRefresh(hostB.Id);
        await Wait.UntilAsync(() =>
            _store.Hosts.Single(h => h.Config.Id == hostB.Id).Status.State == HostConnectionState.AuthFailed);
        await Wait.UntilAsync(() => vm.ShowPartialBar, "first outage should raise the bar");

        vm.DismissPartialCommand.Execute(null);
        Assert.False(vm.ShowPartialBar);

        // Heal B. AuthFailed parks the monitor until a config change, so the
        // recovery goes through UpdateHost (same address — routing stays valid).
        var pollsBeforeHeal = fakeB.Calls.Count(c => c == "get_cc_status");
        fakeB.OnGetCcStatus = () => Task.FromResult(FakeGuiRpcClient.DefaultStatus);
        fakeB.OnAuthorize = _ => Task.FromResult(true);
        _registry.UpdateHost(hostB with { Name = "host-b-healed" });
        await Wait.UntilAsync(() =>
            _store.Hosts.Single(h => h.Config.Id == hostB.Id).Status.State == HostConnectionState.Connected,
            "B should recover after the config update");
        await Wait.UntilAsync(() => fakeB.Calls.Count(c => c == "get_cc_status") > pollsBeforeHeal,
            "B's first post-reconnect poll should be observed before the test re-breaks it");
        Assert.False(vm.ShowPartialBar, "no outage, no bar");

        // Break B again: the SAME id-set {B} as the dismissed outage, but this
        // is a NEW outage after a full recovery — it must be reported.
        fakeB.OnGetCcStatus = () => throw new BoincConnectionException("poll boom again");
        fakeB.OnAuthorize = _ => Task.FromResult(false);
        _store.RequestRefresh(hostB.Id);
        await Wait.UntilAsync(() =>
            _store.Hosts.Single(h => h.Config.Id == hostB.Id).Status.State == HostConnectionState.AuthFailed);

        await Wait.UntilAsync(() => vm.ShowPartialBar,
            "the bar must reappear for a new outage after full recovery");
    }

    [Fact]
    public async Task Partial_bar_coverage_counts_only_hosts_feeding_the_grid()
    {
        // Mirrors TasksViewModelTests' identical-name fact (Codex P2): the
        // covered count {2} in PartialFmt must describe the hosts whose
        // transfers are actually in the grid (Connected AND snapshotted — the
        // exact set Rows are built from), not "total minus unreachable-tier".
        var fakeA = new FakeGuiRpcClient { OnAuthorize = _ => Task.FromResult(false) };
        var fakeB = new FakeGuiRpcClient
        {
            OnGetFileTransfers = () => Task.FromResult<IReadOnlyList<FileTransfer>>(
                [TestData.MakeTransfer(name: "b_task")]),
        };
        var fakeC = new FakeGuiRpcClient
        {
            OnGetFileTransfers = () => Task.FromResult<IReadOnlyList<FileTransfer>>(
                [TestData.MakeTransfer(name: "c_task")]),
        };
        AddHost("host-a", fakeA);
        var hostB = AddHost("host-b", fakeB);
        AddHost("host-c", fakeC);
        var vm = MakeVm();
        _manager.Start();

        await Wait.UntilAsync(() => vm.Rows.Count == 2, "B and C should contribute rows");
        await Wait.UntilAsync(
            () => vm.PartialBarText == string.Format(Strings.PartialFmt, 1, 3, 2),
            "A parks in AuthFailed while B and C feed the grid");

        // Hold B in the Retrying tier deterministically: its live poll AND every
        // reconnect attempt throw, so it can only cycle Retrying -> (instant
        // connect failure) -> Retrying, never Connected again; its snapshot stays
        // cached, and the assertion lands within the first backoff tiers (well
        // before attempt 4 would promote it to the Unreachable tier).
        fakeB.OnGetCcStatus = () => throw new BoincConnectionException("poll boom");
        fakeB.OnConnect = (_, _) => throw new BoincConnectionException("still down");
        _store.RequestRefresh(hostB.Id);
        await Wait.UntilAsync(() =>
            _store.Hosts.Single(h => h.Config.Id == hostB.Id).Status.State == HostConnectionState.Retrying);
        Assert.NotNull(_store.Hosts.Single(h => h.Config.Id == hostB.Id).Snapshot);

        await Wait.UntilAsync(() => vm.Rows.Count == 1, "B's stale rows should drop out");
        Assert.Equal("c_task", Assert.Single(vm.Rows).Data.Name);

        // Unreachable tier is still just {A} (B is Retrying, below the tier),
        // but coverage must now count ONLY C — the sole host feeding the grid.
        await Wait.UntilAsync(
            () => vm.PartialBarText == string.Format(Strings.PartialFmt, 1, 3, 1),
            "covered count should shrink to the hosts actually in the grid");
    }
}
