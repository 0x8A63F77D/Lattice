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
/// Composition root, dispatcher discipline and settle rules all come from the
/// shared <see cref="HostGraphFixture"/> — see its class doc. This suite owns
/// only what differs per suite: which VM it builds.
/// </summary>
public class TasksViewModelTests : IAsyncLifetime
{
    private HostGraphFixture _fx = null!;

    public ValueTask InitializeAsync()
    {
        _fx = new HostGraphFixture();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _fx.DisposeAsync();

    private HostConfig AddHost(string address, FakeGuiRpcClient fake) => _fx.AddHost(address, fake);

    private TasksViewModel MakeVm() => new(_fx.Store, _fx.Clock, _fx.UiState, _fx.Density);

    [Fact]
    public async Task AllHosts_merges_rows_from_both_hosts()
    {
        var fakeA = new FakeGuiRpcClient
        {
            OnGetResults = _ => Task.FromResult<IReadOnlyList<Result>>([TestData.MakeResult(name: "task_a")]),
        };
        var fakeB = new FakeGuiRpcClient
        {
            OnGetResults = _ => Task.FromResult<IReadOnlyList<Result>>([TestData.MakeResult(name: "task_b")]),
        };
        AddHost("host-a", fakeA);
        AddHost("host-b", fakeB);
        var vm = MakeVm();
        _fx.Start();

        await _fx.SettleAsync(() => vm.Rows.Count == 2);

        Assert.True(vm.IsAllHostsScope);
        Assert.Contains(vm.Rows, r => r.Data.Name == "task_a" && r.Data.Host == "host-a");
        Assert.Contains(vm.Rows, r => r.Data.Name == "task_b" && r.Data.Host == "host-b");
    }

    [Fact]
    public async Task Single_host_registry_is_not_aggregate_presentation()
    {
        // ScopeMachine: a one-host registry has no auto-pin, so Scope stays
        // AllHosts — but the aggregate PRESENTATION (Host column, partial bar)
        // must key on genuine multi-host, not on Scope.IsAllHosts, or a
        // single-host user wrongly sees multi-host chrome.
        var fake = new FakeGuiRpcClient
        {
            OnGetResults = _ => Task.FromResult<IReadOnlyList<Result>>([TestData.MakeResult(name: "solo")]),
        };
        AddHost("host-a", fake);
        var vm = MakeVm();
        _fx.Start();
        await _fx.SettleAsync(() => vm.Rows.Count == 1);

        Assert.True(vm.Scope.IsAllHosts);
        Assert.False(vm.IsAllHostsScope);
    }

    [Fact]
    public async Task Scoping_to_a_host_hides_the_other_hosts_rows()
    {
        var fakeA = new FakeGuiRpcClient
        {
            OnGetResults = _ => Task.FromResult<IReadOnlyList<Result>>([TestData.MakeResult(name: "task_a")]),
        };
        var fakeB = new FakeGuiRpcClient
        {
            OnGetResults = _ => Task.FromResult<IReadOnlyList<Result>>([TestData.MakeResult(name: "task_b")]),
        };
        var hostA = AddHost("host-a", fakeA);
        AddHost("host-b", fakeB);
        var vm = MakeVm();
        _fx.Start();
        await _fx.SettleAsync(() => vm.Rows.Count == 2);

        vm.Scope = new ScopeSelection(hostA.Id);

        Assert.False(vm.IsAllHostsScope);
        Assert.Equal("task_a", Assert.Single(vm.Rows).Data.Name);
    }

    [Fact]
    public async Task View_sorts_by_deadline_ascending_with_nulls_last()
    {
        var later = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var sooner = new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero);
        var fake = new FakeGuiRpcClient
        {
            OnGetResults = _ => Task.FromResult<IReadOnlyList<Result>>(
            [
                TestData.MakeResult(name: "no_deadline", deadline: null),
                TestData.MakeResult(name: "later", deadline: later),
                TestData.MakeResult(name: "sooner", deadline: sooner),
            ]),
        };
        AddHost("host-a", fake);
        var vm = MakeVm();
        _fx.Start();
        await _fx.SettleAsync(() => vm.Rows.Count == 3);

        // Display order is view-owned (issue #86): the grid binds RowsView, whose
        // default sort description carries the deadline order. The source Rows
        // stay in reconcile-friendly order and pin nothing about display.
        Assert.Equal(["sooner", "later", "no_deadline"], vm.RowsView.Cast<TaskRow>().Select(r => r.Data.Name));
    }

    // Issue #86 (Tasks leg): a poll that reorders SURVIVING rows — here two
    // tasks swapping deadlines — must not touch the source collection. The old
    // VM-ordered source replayed the reorder as Move→Remove+Insert, which cost
    // DataGrid selection; now Reconcile.alignToExisting keeps survivors in
    // their existing source slots (in-place Updates only) and the VIEW re-sorts
    // itself via the conditional order guard.
    [Fact]
    public async Task Deadline_swap_reorders_the_view_not_the_source_collection()
    {
        var sooner = new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero);
        var later = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        IReadOnlyList<Result> results =
        [
            TestData.MakeResult(name: "first", deadline: sooner),
            TestData.MakeResult(name: "second", deadline: later),
        ];
        var fake = new FakeGuiRpcClient { OnGetResults = _ => Task.FromResult(results) };
        AddHost("host-a", fake);
        var vm = MakeVm();
        _fx.Start();
        await _fx.SettleAsync(() => vm.Rows.Count == 2);

        Assert.Equal(["first", "second"], vm.RowsView.Cast<TaskRow>().Select(r => r.Data.Name));
        var first = vm.Rows.Single(r => r.Data.Name == "first");
        var second = vm.Rows.Single(r => r.Data.Name == "second");
        var sourceEvents = new List<NotifyCollectionChangedAction>();
        vm.Rows.CollectionChanged += (_, e) => sourceEvents.Add(e.Action);

        // Swapped deadlines AND swapped list positions: the daemon's result
        // order is not contractual, so the unaligned target would reorder the
        // source (a Move) even without any VM-side sort.
        results =
        [
            TestData.MakeResult(name: "second", deadline: sooner),
            TestData.MakeResult(name: "first", deadline: later),
        ];
        _fx.Store.RequestRefresh(null);
        await _fx.SettleAsync(() => second.Data.Deadline == sooner);

        // Source: same holders in the same slots, updated in place — no
        // collection event at all, so a bound grid never saw a remove.
        Assert.Empty(sourceEvents);
        Assert.Same(first, vm.Rows[0]);
        Assert.Same(second, vm.Rows[1]);
        // View: the default deadline order re-asserts itself over the new values.
        Assert.Equal(["second", "first"], vm.RowsView.Cast<TaskRow>().Select(r => r.Data.Name));
    }

    [Fact]
    public async Task FilterText_matches_project_name_case_insensitively()
    {
        const string seti = "https://seti.berkeley.edu/";
        var fake = new FakeGuiRpcClient
        {
            OnGetState = () => Task.FromResult(
                TestData.MakeState(projects: [TestData.MakeProject(url: seti, name: "SETI@home")])),
            OnGetResults = _ => Task.FromResult<IReadOnlyList<Result>>(
            [
                TestData.MakeResult(name: "alpha", projectUrl: seti),
                TestData.MakeResult(name: "beta", projectUrl: "https://example.org/"),
            ]),
        };
        AddHost("host-a", fake);
        var vm = MakeVm();
        _fx.Start();
        await _fx.SettleAsync(() => vm.Rows.Count == 2);

        vm.FilterText = "seti";

        Assert.Equal("alpha", Assert.Single(vm.Rows).Data.Name);
    }

    [Fact]
    public async Task Filter_hiding_every_row_is_not_the_empty_state()
    {
        var fake = new FakeGuiRpcClient
        {
            OnGetResults = _ => Task.FromResult<IReadOnlyList<Result>>([TestData.MakeResult(name: "alpha")]),
        };
        AddHost("host-a", fake);
        var vm = MakeVm();
        _fx.Start();
        await _fx.SettleAsync(() => vm.Rows.Count == 1);

        vm.FilterText = "matches-nothing";

        // The host answered WITH tasks; the filter merely hid them. "No tasks"
        // (IsEmpty) is a statement about the unfiltered set — a filter miss
        // must not flip the view into the empty state (Codex P2).
        Assert.Empty(vm.Rows);
        Assert.False(vm.IsEmpty, "a filter miss is not an empty task set");
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public async Task StateFilter_filters_by_task_state_kind()
    {
        var fake = new FakeGuiRpcClient
        {
            OnGetResults = _ => Task.FromResult<IReadOnlyList<Result>>(
            [
                TestData.MakeResult(name: "susp") with { SuspendedViaGui = true },
                TestData.MakeResult(name: "running") with { ActiveTask = new ActiveTask(1, 0.5, 10, 90) },
            ]),
        };
        AddHost("host-a", fake);
        var vm = MakeVm();
        _fx.Start();
        await _fx.SettleAsync(() => vm.Rows.Count == 2);

        vm.StateFilter = TaskStateKind.Suspended;

        Assert.Equal("susp", Assert.Single(vm.Rows).Data.Name);
    }

    [Fact]
    public async Task CountsText_matches_a_crafted_mix()
    {
        var fake = new FakeGuiRpcClient
        {
            OnGetResults = _ => Task.FromResult<IReadOnlyList<Result>>(
            [
                TestData.MakeResult(name: "r1") with { ActiveTask = new ActiveTask(1, 0.1, 10, 90) },
                TestData.MakeResult(name: "r2") with { ActiveTask = new ActiveTask(1, 0.1, 10, 90) },
                TestData.MakeResult(name: "u1") with { ActiveTask = null, State = ResultState.FilesUploading },
                TestData.MakeResult(name: "s1") with { SuspendedViaGui = true },
                TestData.MakeResult(name: "w1"),
            ]),
        };
        AddHost("host-a", fake);
        var vm = MakeVm();
        _fx.Start();
        await _fx.SettleAsync(() => vm.Rows.Count == 5);

        Assert.Equal(string.Format(Strings.CountsFmt, 5, 2, 1, 1), vm.CountsText);
    }

    [Fact]
    public async Task PollingText_reads_the_registry_interval()
    {
        AddHost("host-a", new FakeGuiRpcClient());
        var vm = MakeVm();
        _fx.Start();
        await _fx.SettleAsync(() => !vm.IsLoading, "first snapshot should land");

        Assert.Equal(string.Format(Strings.PollingFmt, 5), vm.PollingText);
    }

    [Fact]
    public async Task Partial_bar_shows_dismisses_and_reappears_when_the_unreachable_set_changes()
    {
        var fakeA = new FakeGuiRpcClient { OnAuthorize = _ => Task.FromResult(false) };
        var fakeB = new FakeGuiRpcClient();
        var hostA = AddHost("host-a", fakeA);
        var hostB = AddHost("host-b", fakeB);
        var vm = MakeVm();
        _fx.Start();

        await _fx.SettleAsync(() =>
            _fx.Store.Hosts.Single(h => h.Config.Id == hostA.Id).Status.State == HostConnectionState.AuthFailed);
        await _fx.SettleAsync(() =>
            _fx.Store.Hosts.Single(h => h.Config.Id == hostB.Id).Snapshot is not null);

        // Condition-driven: the store Waits above observe HostStore fields, which
        // are set BEFORE Changed fires — the VM lags the store by one Rebuild, so
        // a direct assert here can read the previous Rebuild's text.
        await _fx.SettleAsync(
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
        _fx.Store.RequestRefresh(hostB.Id);
        await _fx.SettleAsync(() =>
            _fx.Store.Hosts.Single(h => h.Config.Id == hostB.Id).Status.State == HostConnectionState.Retrying);
        _fx.Store.RequestRefresh(hostB.Id); // skip the remaining backoff, retry now
        await _fx.SettleAsync(() =>
            _fx.Store.Hosts.Single(h => h.Config.Id == hostB.Id).Status.State == HostConnectionState.AuthFailed);

        await _fx.SettleAsync(
            () => vm.PartialBarText == string.Format(Strings.PartialFmt, 2, 2, 0),
            "partial bar should reappear: the unreachable id-set changed");
        Assert.True(vm.ShowPartialBar);
    }

    [Fact]
    public async Task Partial_bar_reappears_when_a_dismissed_covered_host_drops_out()
    {
        // Codex P2 (design doc: InfoBar "reappears when the reachable set
        // changes"): the dismissal fingerprint must cover the whole
        // partial-state picture — (unreachable set, covered set) — not just
        // the unreachable id-set. Here A is AuthFailed for the whole test, so
        // the unreachable set stays {A} throughout; only a COVERED host (C)
        // drops out of Connected (into Retrying, below the Unreachable tier).
        // That shrinks the grid's coverage without touching the unreachable
        // tier at all — the old id-set-only fingerprint would keep the bar
        // hidden forever after the first dismissal.
        var fakeA = new FakeGuiRpcClient { OnAuthorize = _ => Task.FromResult(false) };
        var fakeB = new FakeGuiRpcClient
        {
            OnGetResults = _ => Task.FromResult<IReadOnlyList<Result>>([TestData.MakeResult(name: "b_task")]),
        };
        var fakeC = new FakeGuiRpcClient
        {
            OnGetResults = _ => Task.FromResult<IReadOnlyList<Result>>([TestData.MakeResult(name: "c_task")]),
        };
        AddHost("host-a", fakeA);
        AddHost("host-b", fakeB);
        var hostC = AddHost("host-c", fakeC);
        var vm = MakeVm();
        _fx.Start();

        await _fx.SettleAsync(() => vm.Rows.Count == 2, "B and C should contribute rows");
        await _fx.SettleAsync(
            () => vm.PartialBarText == string.Format(Strings.PartialFmt, 1, 3, 2),
            "A parks in AuthFailed while B and C feed the grid");
        Assert.True(vm.ShowPartialBar);

        vm.DismissPartialCommand.Execute(null);
        Assert.False(vm.ShowPartialBar);

        await _fx.SettleAsync(() => fakeC.Calls.Count(c => c == "get_cc_status") > 0,
            "C's first poll should be observed before the test breaks it");

        // Hold C in the Retrying tier (below the Unreachable threshold, so the
        // unreachable id-set never changes): its live poll AND every reconnect
        // attempt throw, so it can only cycle Retrying -> (instant connect
        // failure) -> Retrying, never Connected again.
        fakeC.OnGetCcStatus = () => throw new BoincConnectionException("poll boom");
        fakeC.OnConnect = (_, _) => throw new BoincConnectionException("still down");
        _fx.Store.RequestRefresh(hostC.Id);
        await _fx.SettleAsync(() =>
            _fx.Store.Hosts.Single(h => h.Config.Id == hostC.Id).Status.State == HostConnectionState.Retrying);
        Assert.NotNull(_fx.Store.Hosts.Single(h => h.Config.Id == hostC.Id).Snapshot);

        await _fx.SettleAsync(() => vm.Rows.Count == 1, "C's stale rows should drop out");

        // Unreachable tier is still just {A} — but the bar must reappear
        // because the covered set shrank from {B, C} to {B}.
        await _fx.SettleAsync(
            () => vm.ShowPartialBar
                  && vm.PartialBarText == string.Format(Strings.PartialFmt, 1, 3, 1),
            "the bar must reappear: the covered set shrank even though the unreachable set stayed {A}");
    }

    [Fact]
    public async Task Stale_snapshot_of_unreachable_host_leaves_rows_and_counts()
    {
        // HostEntry.Snapshot is never cleared when a host drops out of Connected,
        // so ONLY Rebuild's Connected-only filter keeps a dead host's stale cached
        // tasks out of Rows/CountsText. This test goes red if that filter is
        // replaced with a plain pass-through of the scoped hosts.
        var fakeA = new FakeGuiRpcClient
        {
            OnGetResults = _ => Task.FromResult<IReadOnlyList<Result>>(
            [
                TestData.MakeResult(name: "a_running") with { ActiveTask = new ActiveTask(1, 0.5, 10, 90) },
                TestData.MakeResult(name: "a_waiting"),
            ]),
        };
        var fakeB = new FakeGuiRpcClient
        {
            OnGetResults = _ => Task.FromResult<IReadOnlyList<Result>>([TestData.MakeResult(name: "b_task")]),
        };
        AddHost("host-a", fakeA);
        var hostB = AddHost("host-b", fakeB);
        var vm = MakeVm();
        _fx.Start();

        // Baseline: both hosts' rows merged.
        await _fx.SettleAsync(() => vm.Rows.Count == 3, "both hosts should contribute rows");
        Assert.Contains(vm.Rows, r => r.Data.Name == "b_task");

        // Kill B the same way the partial-bar test does — poll failure, then auth
        // failure on reconnect — parking it in AuthFailed with its snapshot cached.
        fakeB.OnGetCcStatus = () => throw new BoincConnectionException("poll boom");
        fakeB.OnAuthorize = _ => Task.FromResult(false);
        _fx.Store.RequestRefresh(hostB.Id);
        await _fx.SettleAsync(() =>
            _fx.Store.Hosts.Single(h => h.Config.Id == hostB.Id).Status.State == HostConnectionState.Retrying);
        _fx.Store.RequestRefresh(hostB.Id); // skip the remaining backoff, retry now
        await _fx.SettleAsync(() =>
            _fx.Store.Hosts.Single(h => h.Config.Id == hostB.Id).Status.State == HostConnectionState.AuthFailed);
        Assert.NotNull(_fx.Store.Hosts.Single(h => h.Config.Id == hostB.Id).Snapshot);

        // B's stale rows must vanish from the merge and from the counts.
        await _fx.SettleAsync(() => vm.Rows.Count == 2, "the dead host's stale rows should drop out");
        Assert.DoesNotContain(vm.Rows, r => r.Data.Name == "b_task");
        Assert.Equal(["a_running", "a_waiting"], vm.Rows.Select(r => r.Data.Name).Order());
        Assert.Equal(string.Format(Strings.CountsFmt, 2, 1, 0, 0), vm.CountsText);

        // And B now counts toward the partial-results bar (1 of 2 unreachable).
        // Condition-driven: the VM lags the store-state Waits by one Rebuild.
        await _fx.SettleAsync(
            () => vm.ShowPartialBar
                  && vm.PartialBarText == string.Format(Strings.PartialFmt, 1, 2, 1),
            "B should count toward the partial bar");
    }

    [Fact]
    public async Task Partial_bar_reappears_when_the_same_host_fails_after_recovering()
    {
        // Codex P2 round 2: dismissal must not survive a full recovery. If the
        // user dismisses {B}, B recovers (unreachable set -> empty), and B later
        // fails AGAIN (set -> {B}), the fresh set SetEquals the stale dismissed
        // set — without clearing it on recovery, the new outage is never shown.
        //
        // Each outage is driven by ADVANCING the monitor's virtual clock (the sibling
        // HostMonitorPollingTests idiom, fixture AdvanceUntilAsync), not by RequestRefresh
        // nudges. Advancing fires the steady-state poll wait (-> failing tick ->
        // Retrying) and then the backoff wait (-> reconnect -> rejected auth ->
        // AuthFailed), and is race-free by construction: a wait entered AFTER one
        // advance is caught by the next. Nudges are avoided deliberately — a single
        // RequestRefresh can be silently coalesced with the sticky wake UpdateHost
        // leaves behind on the heal below, stranding a broken B in Connected on the
        // frozen clock (the ~5 s timeout this fact used to flake with under load).
        var fakeA = new FakeGuiRpcClient();
        var fakeB = new FakeGuiRpcClient();
        AddHost("host-a", fakeA);
        var hostB = AddHost("host-b", fakeB);
        var vm = MakeVm();
        _fx.Start();
        await _fx.SettleAsync(() =>
            _fx.Store.Hosts.Single(h => h.Config.Id == hostB.Id).Status.State == HostConnectionState.Connected);

        // First outage: break B and advance until it settles in AuthFailed.
        await AdvanceUntilAuthFailedAsync(hostB, fakeB, "poll boom");
        await _fx.SettleAsync(() => vm.ShowPartialBar, "first outage should raise the bar");

        vm.DismissPartialCommand.Execute(null);
        Assert.False(vm.ShowPartialBar);

        // Heal B. AuthFailed parks the monitor until a config change, so the
        // recovery goes through UpdateHost (same address — routing stays valid).
        fakeB.OnGetCcStatus = () => Task.FromResult(FakeGuiRpcClient.DefaultStatus);
        fakeB.OnAuthorize = _ => Task.FromResult(true);
        _fx.Registry.UpdateHost(hostB with { Name = "host-b-healed" });
        await _fx.SettleAsync(() =>
            _fx.Store.Hosts.Single(h => h.Config.Id == hostB.Id).Status.State == HostConnectionState.Connected,
            "B should recover after the config update");
        Assert.False(vm.ShowPartialBar, "no outage, no bar");

        // Break B again: the SAME id-set {B} as the dismissed outage, but this
        // is a NEW outage after a full recovery — it must be reported. Same
        // clock-driven poll-fail -> backoff -> AuthFailed cascade as the first break.
        await AdvanceUntilAuthFailedAsync(hostB, fakeB, "poll boom again");
        await _fx.SettleAsync(() => vm.ShowPartialBar,
            "the bar must reappear for a new outage after full recovery");
    }

    // Installs a failing poll + rejected re-auth on B, then advances the monitor's
    // clock until it settles in AuthFailed. Advancing (rather than nudging) makes the
    // whole poll-fail -> Retrying -> backoff -> reconnect -> AuthFailed cascade
    // deterministic: virtual time, not a background thread's scheduling, drives it.
    private async Task AdvanceUntilAuthFailedAsync(HostConfig host, FakeGuiRpcClient fake, string error)
    {
        fake.OnGetCcStatus = () => throw new BoincConnectionException(error);
        fake.OnAuthorize = _ => Task.FromResult(false);
        await _fx.AdvanceUntilAsync(
            () => _fx.Store.Hosts.Single(h => h.Config.Id == host.Id).Status.State == HostConnectionState.AuthFailed,
            TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Partial_bar_coverage_counts_only_hosts_feeding_the_grid()
    {
        // Codex P2: the covered count {2} in PartialFmt must describe the hosts
        // whose tasks are actually in the grid (Connected AND snapshotted — the
        // exact set Rows are built from), not "total minus unreachable-tier".
        // With A=AuthFailed, B=Retrying (stale snapshot), C=Connected, the old
        // total-minus-unreachable arithmetic claimed coverage of 2 hosts while
        // only C's tasks were below.
        var fakeA = new FakeGuiRpcClient { OnAuthorize = _ => Task.FromResult(false) };
        var fakeB = new FakeGuiRpcClient
        {
            OnGetResults = _ => Task.FromResult<IReadOnlyList<Result>>([TestData.MakeResult(name: "b_task")]),
        };
        var fakeC = new FakeGuiRpcClient
        {
            OnGetResults = _ => Task.FromResult<IReadOnlyList<Result>>([TestData.MakeResult(name: "c_task")]),
        };
        AddHost("host-a", fakeA);
        var hostB = AddHost("host-b", fakeB);
        AddHost("host-c", fakeC);
        var vm = MakeVm();
        _fx.Start();

        // Baseline: A AuthFailed, B and C both feeding the grid. Covered = 2
        // under BOTH the old and the new semantics — pinning it here isolates
        // the assertion below to the B-drops-out transition.
        await _fx.SettleAsync(() => vm.Rows.Count == 2, "B and C should contribute rows");
        await _fx.SettleAsync(
            () => vm.PartialBarText == string.Format(Strings.PartialFmt, 1, 3, 2),
            "A parks in AuthFailed while B and C feed the grid");

        // Hold B in the Retrying tier deterministically: its live poll AND every
        // reconnect attempt throw, so it can only cycle Retrying -> (instant
        // connect failure) -> Retrying, never Connected again; its snapshot stays
        // cached, and the assertion lands within the first backoff tiers (well
        // before attempt 4 would promote it to the Unreachable tier).
        fakeB.OnGetCcStatus = () => throw new BoincConnectionException("poll boom");
        fakeB.OnConnect = (_, _) => throw new BoincConnectionException("still down");
        _fx.Store.RequestRefresh(hostB.Id);
        await _fx.SettleAsync(() =>
            _fx.Store.Hosts.Single(h => h.Config.Id == hostB.Id).Status.State == HostConnectionState.Retrying);
        Assert.NotNull(_fx.Store.Hosts.Single(h => h.Config.Id == hostB.Id).Snapshot);

        await _fx.SettleAsync(() => vm.Rows.Count == 1, "B's stale rows should drop out");
        Assert.Equal("c_task", Assert.Single(vm.Rows).Data.Name);

        // Unreachable tier is still just {A} (B is Retrying, below the tier),
        // but coverage must now count ONLY C — the sole host feeding the grid.
        await _fx.SettleAsync(
            () => vm.PartialBarText == string.Format(Strings.PartialFmt, 1, 3, 1),
            "covered count should shrink to the hosts actually in the grid");
    }

    [Fact]
    public async Task UpdatedText_ticks_with_the_manual_clock()
    {
        var fake = new FakeGuiRpcClient();
        AddHost("host-a", fake);
        var vm = MakeVm();
        _fx.Start();
        await _fx.SettleAsync(() => _fx.Store.Hosts[0].Snapshot is not null);

        _fx.Clock.Now = _fx.Store.Hosts[0].Snapshot!.Timestamp;
        _fx.Clock.Advance(TimeSpan.FromSeconds(3));

        Assert.Equal(string.Format(Strings.UpdatedSecondsFmt, 3), vm.UpdatedText);
    }

    [Fact]
    public async Task Clock_tick_does_not_churn_rows_when_task_data_is_unchanged()
    {
        // The 1s tick funnels into Rebuild() like every other trigger, but with
        // unchanged task data it must NOT replace the row collection: a DataGrid
        // bound to Rows would otherwise see a Reset + re-Add every second and a
        // future SelectedItem binding would lose selection every tick (rows are
        // value-equal records, so identity must be preserved when nothing changed).
        var fake = new FakeGuiRpcClient
        {
            OnGetResults = _ => Task.FromResult<IReadOnlyList<Result>>([TestData.MakeResult(name: "steady")]),
        };
        AddHost("host-a", fake);
        var vm = MakeVm();
        _fx.Start();
        await _fx.SettleAsync(() => vm.Rows.Count == 1);

        var firstRow = vm.Rows[0];
        _fx.Clock.Now = _fx.Store.Hosts[0].Snapshot!.Timestamp;
        var collectionChanges = 0;
        vm.Rows.CollectionChanged += (_, _) => collectionChanges++;
        // The VIEW must stay quiet too (issue #86): the post-reconcile order
        // guard only Refreshes on a genuine order violation, so a steady-state
        // tick must not Reset the grid's ItemsSource every second.
        var viewChanges = 0;
        ((INotifyCollectionChanged)vm.RowsView).CollectionChanged += (_, _) => viewChanges++;

        _fx.Clock.Advance(TimeSpan.FromSeconds(1));
        _fx.Clock.Advance(TimeSpan.FromSeconds(1));
        _fx.Clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(0, collectionChanges);
        Assert.Equal(0, viewChanges);
        Assert.Same(firstRow, vm.Rows[0]);
        // The tick path itself must stay alive: freshness text still advances.
        Assert.Equal(string.Format(Strings.UpdatedSecondsFmt, 3), vm.UpdatedText);
    }

    [Fact]
    public async Task Auth_failed_host_without_snapshot_leaves_loading_state()
    {
        // Codex P2 round 3: a host that parks in a TERMINAL state before its
        // first snapshot (canonical: wrong password -> AuthFailed; Snapshot
        // stays null forever) must not leave the first-fetch skeleton up
        // indefinitely. And it must NOT fall through to IsEmpty either — an
        // auth-failed scope is not "no tasks on this host"; the rail and the
        // partial bar tell that story.
        var fake = new FakeGuiRpcClient { OnAuthorize = _ => Task.FromResult(false) };
        AddHost("host-a", fake);
        var vm = MakeVm();
        _fx.Start();
        await _fx.SettleAsync(() =>
            _fx.Store.Hosts.Single().Status.State == HostConnectionState.AuthFailed);

        await _fx.SettleAsync(() => !vm.IsLoading,
            "the loading overlay must yield once every scoped host parks terminally");
        Assert.False(vm.IsEmpty, "an auth-failed scope is not 'no tasks on this host'");
    }

    [Fact]
    public async Task Stale_snapshot_does_not_suppress_loading_while_a_connected_host_fetches_first_data()
    {
        // Codex P2 round 6: a Retrying host's RETAINED stale snapshot must not
        // suppress the first-fetch loading overlay (nor let the view fall
        // through to the empty state) while a Connected host's first data is
        // still in flight. The Connected-without-snapshot window is real and
        // holdable: HostMachine publishes Connected right after get_state, but
        // the first snapshot only lands after the first tick's poll — so
        // parking A's get_cc_status on a never-completing TaskCompletionSource
        // pins A in Connected-with-null-Snapshot deterministically (the fake
        // awaits via WaitAsync(ct), so dispose still cancels it cleanly).
        var gate = new TaskCompletionSource<CcStatus>();
        var fakeA = new FakeGuiRpcClient { OnGetCcStatus = () => gate.Task };
        var fakeB = new FakeGuiRpcClient
        {
            OnGetResults = _ => Task.FromResult<IReadOnlyList<Result>>([TestData.MakeResult(name: "b_task")]),
        };
        var hostA = AddHost("host-a", fakeA);
        var hostB = AddHost("host-b", fakeB);
        var vm = MakeVm();
        _fx.Start();

        // Baseline: A parked Connected-without-snapshot, B contributing rows —
        // a contributing host suppresses loading regardless of A's pending fetch.
        await _fx.SettleAsync(() =>
            _fx.Store.Hosts.Single(h => h.Config.Id == hostA.Id).Status.State == HostConnectionState.Connected);
        await _fx.SettleAsync(() => vm.Rows.Count == 1, "B should contribute its row");
        Assert.Null(_fx.Store.Hosts.Single(h => h.Config.Id == hostA.Id).Snapshot);
        Assert.False(vm.IsLoading, "B's data is visible; the first-fetch skeleton must not cover it");
        Assert.False(vm.IsEmpty);

        // Hold B in the Retrying tier (same idiom as the coverage test): its
        // live poll AND every reconnect attempt throw, so it cycles Retrying ->
        // (instant connect failure) -> Retrying, never Connected again — while
        // its snapshot stays cached and hidden by the grid's Connected filter.
        fakeB.OnGetCcStatus = () => throw new BoincConnectionException("poll boom");
        fakeB.OnConnect = (_, _) => throw new BoincConnectionException("still down");
        _fx.Store.RequestRefresh(hostB.Id);
        await _fx.SettleAsync(() =>
            _fx.Store.Hosts.Single(h => h.Config.Id == hostB.Id).Status.State == HostConnectionState.Retrying);
        Assert.NotNull(_fx.Store.Hosts.Single(h => h.Config.Id == hostB.Id).Snapshot);
        await _fx.SettleAsync(() => vm.Rows.Count == 0, "B's stale rows should drop out");

        // A is still fetching its FIRST data; B's cached-but-hidden snapshot
        // counts for nothing. Loading must return — and the view must NOT fall
        // through to "no tasks" (A never answered; B's answer is stale).
        await _fx.SettleAsync(() => vm.IsLoading,
            "the first-fetch overlay must return: only A can produce visible data and it hasn't answered");
        Assert.False(vm.IsEmpty, "an unanswered first fetch is not an empty task set");
    }

    [Fact]
    public async Task IsLoading_until_the_first_snapshot_then_IsEmpty_with_zero_tasks()
    {
        var fake = new FakeGuiRpcClient();
        AddHost("host-a", fake);
        var vm = MakeVm();

        Assert.True(vm.IsLoading);
        Assert.False(vm.IsEmpty);

        _fx.Start();
        await _fx.SettleAsync(() => !vm.IsLoading, "loading should clear once the first snapshot lands");

        Assert.False(vm.IsLoading);
        Assert.True(vm.IsEmpty);
        Assert.Empty(vm.Rows);
    }

    [Fact]
    public void Density_and_column_preferences_round_trip_through_the_ui_state_store()
    {
        var vm1 = MakeVm();
        Assert.False(vm1.IsCompact);
        Assert.Null(vm1.GetColumnPreference("Elapsed"));

        vm1.IsCompact = true;
        vm1.SetColumnPreference("Elapsed", false);
        vm1.Dispose();

        // A brand-new VM over the same store sees the persisted choices; columns
        // the user never toggled stay null (breakpoints keep deciding for them).
        var vm2 = MakeVm();
        Assert.True(vm2.IsCompact);
        Assert.False(vm2.GetColumnPreference("Elapsed"));
        Assert.Null(vm2.GetColumnPreference("Project"));
        vm2.Dispose();
    }

    [Fact]
    public async Task SetColumnPreference_preserves_a_density_change_saved_by_another_view_model_after_construction()
    {
        // Codex P2 (PR #45), mirrors the identical-name Transfers fact: a
        // Transfers VM (sharing the same DensityPreference/UiStateStore, as
        // ShellViewModel wires them in production) toggles density AFTER `vm`
        // already cached its own ColumnVisibility snapshot — a stale
        // whole-snapshot Save from `vm` must not drop that foreign density
        // change. DensityPreference.Set and UiStateStore.Update both re-load
        // fresh before persisting, so this holds regardless of write order.
        AddHost("host-a", new FakeGuiRpcClient());
        var vm = MakeVm();
        _fx.Start();
        await _fx.SettleAsync(() => !vm.IsLoading);

        using var transfersVm = new TransfersViewModel(_fx.Store, _fx.Clock, _fx.Density);
        transfersVm.IsCompact = true;

        // Tasks only touches ColumnVisibility here, but its cached _uiState
        // predates Transfers' write.
        vm.SetColumnPreference("Elapsed", false);

        var reloaded = _fx.UiState.Load();
        Assert.True(reloaded.CompactDensity, "Transfers' density change must survive Tasks' column-preference save");
        Assert.True(reloaded.ColumnVisibility.TryGetValue("Elapsed", out var elapsedVisible),
            "Tasks' own column preference must be saved");
        Assert.False(elapsedVisible);
    }

    // Issue #24 acceptance (Tasks leg): keyed reconciliation must update a
    // surviving row's holder IN PLACE — no CollectionChanged event, no new
    // holder instance — so DataGrid selection survives a value-change poll.
    [Fact]
    public async Task Progress_change_updates_row_in_place_without_collection_events()
    {
        var activeTask = new ActiveTask(1, 0.25, 10, 90);
        var fake = new FakeGuiRpcClient
        {
            OnGetResults = _ => Task.FromResult<IReadOnlyList<Result>>(
                [TestData.MakeResult(name: "wu_1") with { ActiveTask = activeTask }]),
        };
        AddHost("host-a", fake);
        var vm = MakeVm();
        _fx.Start();
        await _fx.SettleAsync(() => vm.Rows.Count == 1);

        var holder = vm.Rows[0];
        var events = 0;
        vm.Rows.CollectionChanged += (_, _) => events++;

        activeTask = new ActiveTask(1, 0.75, 30, 90); // next poll reports progress
        _fx.Store.RequestRefresh(null);
        await _fx.SettleAsync(() => vm.Rows[0].Data.PercentText == "75%");

        Assert.Equal(0, events);              // no Reset, no Remove+Add — issue #24
        Assert.Same(holder, vm.Rows[0]);       // selection identity survives
        Assert.Equal(0.75, holder.Data.Fraction);
    }

    [Fact]
    public async Task Task_departure_removes_only_that_row()
    {
        IReadOnlyList<Result> results = [TestData.MakeResult(name: "wu_1"), TestData.MakeResult(name: "wu_2")];
        var fake = new FakeGuiRpcClient { OnGetResults = _ => Task.FromResult(results) };
        AddHost("host-a", fake);
        var vm = MakeVm();
        _fx.Start();
        await _fx.SettleAsync(() => vm.Rows.Count == 2);

        var survivor = vm.Rows.Single(r => r.Data.Name == "wu_2");
        var actions = new List<NotifyCollectionChangedAction>();
        vm.Rows.CollectionChanged += (_, e) => actions.Add(e.Action);

        results = [TestData.MakeResult(name: "wu_2")];
        _fx.Store.RequestRefresh(null);
        await _fx.SettleAsync(() => vm.Rows.Count == 1);

        Assert.Same(survivor, vm.Rows[0]);
        Assert.Equal("wu_2", vm.Rows[0].Data.Name);
        Assert.Equal([NotifyCollectionChangedAction.Remove], actions); // no Reset
    }
}
