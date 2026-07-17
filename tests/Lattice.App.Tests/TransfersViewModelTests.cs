using System.Collections.Specialized;
using Lattice.App.Infrastructure;
using Lattice.App.Localization;
using Lattice.App.Tests.Fakes;
using Lattice.App.ViewModels;
using Lattice.Boinc.GuiRpc;
using Lattice.Core;
using Lattice.Tests;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Lattice.App.Tests;

/// <summary>
/// Composition root, dispatcher discipline and settle rules all come from the
/// shared <see cref="HostGraphFixture"/> — see its class doc. This suite owns
/// only what differs per suite: which VM it builds.
/// </summary>
public class TransfersViewModelTests : IAsyncLifetime
{
    private HostGraphFixture _fx = null!;

    public ValueTask InitializeAsync()
    {
        _fx = new HostGraphFixture();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _fx.DisposeAsync();

    private HostConfig AddHost(string address, FakeGuiRpcClient fake) => _fx.AddHost(address, fake);

    private TransfersViewModel MakeVm() => new(_fx.Store, _fx.Clock, _fx.Density);

    [Fact]
    public async Task Single_host_registry_is_not_aggregate_presentation()
    {
        // ScopeMachine: a one-host registry has no auto-pin, so Scope stays
        // AllHosts — but the aggregate PRESENTATION (Host column, partial bar)
        // must key on genuine multi-host, not on Scope.IsAllHosts, or a
        // single-host user wrongly sees multi-host chrome.
        var fake = new FakeGuiRpcClient
        {
            OnGetFileTransfers = () => Task.FromResult<IReadOnlyList<FileTransfer>>(
                [TestData.MakeTransfer(name: "file_a")]),
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
        _fx.Start();
        await _fx.SettleAsync(() => vm.Rows.Count == 2);

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
        _fx.Start();
        await _fx.SettleAsync(() => vm.Rows.Count == 1);

        var holder = vm.Rows[0];
        var events = 0;
        vm.Rows.CollectionChanged += (_, _) => events++;

        // Steady-state ticks with identical data: zero CollectionChanged events.
        _fx.Clock.Now = _fx.Store.Hosts[0].Snapshot!.Timestamp;
        _fx.Clock.Advance(TimeSpan.FromSeconds(1));
        _fx.Clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(0, events);
        Assert.Same(holder, vm.Rows[0]);
        Assert.Equal("25 / 100 MB", holder.Data.ProgressText);

        // Progress change: same key, holder identity survives, Data updates in place.
        t = TestData.MakeTransfer(name: "file_1", nbytes: 100 * mb, bytesXferred: 75 * mb);
        _fx.Store.RequestRefresh(null);
        await _fx.SettleAsync(() => vm.Rows[0].Data.Fraction == 0.75);

        Assert.Same(holder, vm.Rows[0]);
        Assert.Equal("75 / 100 MB", holder.Data.ProgressText);
    }

    // Display order is view-owned (issue #86): the grid binds RowsView, whose
    // default sort description carries the project-then-name ordinal order
    // formerly imposed on the source by Rebuild.
    [Fact]
    public async Task View_sorts_by_project_then_name_ordinal()
    {
        var fake = new FakeGuiRpcClient
        {
            OnGetFileTransfers = () => Task.FromResult<IReadOnlyList<FileTransfer>>(
            [
                TestData.MakeTransfer(name: "z_file", projectUrl: "https://a.example/", projectName: "Alpha"),
                TestData.MakeTransfer(name: "b_file", projectUrl: "https://m.example/", projectName: "Mu"),
                TestData.MakeTransfer(name: "a_file", projectUrl: "https://m.example/", projectName: "Mu"),
            ]),
        };
        AddHost("host-a", fake);
        var vm = MakeVm();
        _fx.Start();
        await _fx.SettleAsync(() => vm.Rows.Count == 3);

        Assert.Equal(
            ["z_file", "a_file", "b_file"],
            vm.RowsView.Cast<TransferRow>().Select(r => r.Data.Name));
    }

    // Issue #86 (Transfers leg): the narrow real trigger — a tie on the
    // (project, name) sort key (an upload and a download of the same file)
    // whose snapshot order flips between polls. The old VM-ordered source
    // replayed that flip as Move→Remove+Insert, costing DataGrid selection;
    // now survivors keep their source slots (Reconcile.alignToExisting), the
    // view's tie order stays first-seen, and neither collection raises any
    // reorder event.
    [Fact]
    public async Task Tie_order_flip_between_polls_moves_nothing_and_raises_no_events()
    {
        const double mb = 1024.0 * 1024.0;
        IReadOnlyList<FileTransfer> transfers =
        [
            TestData.MakeTransfer(name: "file.dat", isUpload: true, nbytes: 100 * mb),
            TestData.MakeTransfer(name: "file.dat", isUpload: false, nbytes: 100 * mb),
        ];
        var fake = new FakeGuiRpcClient { OnGetFileTransfers = () => Task.FromResult(transfers) };
        AddHost("host-a", fake);
        var vm = MakeVm();
        _fx.Start();
        await _fx.SettleAsync(() => vm.Rows.Count == 2);

        var upload = vm.Rows.Single(r => r.Key.IsUpload);
        var download = vm.Rows.Single(r => !r.Key.IsUpload);
        Assert.Equal([upload.Key, download.Key], vm.RowsView.Cast<TransferRow>().Select(r => r.Key));
        var sourceEvents = new List<NotifyCollectionChangedAction>();
        vm.Rows.CollectionChanged += (_, e) => sourceEvents.Add(e.Action);
        var viewEvents = new List<NotifyCollectionChangedAction>();
        ((INotifyCollectionChanged)vm.RowsView).CollectionChanged += (_, e) => viewEvents.Add(e.Action);

        // Same two transfers, flipped list order; a progress change on the
        // download gives the poll an observable settle signal.
        transfers =
        [
            TestData.MakeTransfer(name: "file.dat", isUpload: false, nbytes: 100 * mb, bytesXferred: 25 * mb),
            TestData.MakeTransfer(name: "file.dat", isUpload: true, nbytes: 100 * mb),
        ];
        _fx.Store.RequestRefresh(null);
        await _fx.SettleAsync(() => download.Data.Fraction == 0.25);

        // In-place updates only: no source event, no view event, same holders,
        // and the tie keeps its first-seen display order.
        Assert.Empty(sourceEvents);
        Assert.Empty(viewEvents);
        Assert.Same(upload, vm.Rows[0]);
        Assert.Same(download, vm.Rows[1]);
        Assert.Equal([upload.Key, download.Key], vm.RowsView.Cast<TransferRow>().Select(r => r.Key));
    }

    // Issue #86: the post-reconcile order guard must also serve a HEADER sort.
    // Installing a FromPath description the way the grid's own ProcessSort does
    // (clear + add on RowsView.SortDescriptions), an in-place progress update
    // that inverts the sorted order must re-sort the view — the collection view
    // never re-compares survivors on its own.
    [Fact]
    public async Task Header_sort_stays_live_across_in_place_updates()
    {
        const double mb = 1024.0 * 1024.0;
        IReadOnlyList<FileTransfer> transfers =
        [
            TestData.MakeTransfer(name: "small", nbytes: 100 * mb, bytesXferred: 10 * mb),
            TestData.MakeTransfer(name: "large", nbytes: 100 * mb, bytesXferred: 50 * mb),
        ];
        var fake = new FakeGuiRpcClient { OnGetFileTransfers = () => Task.FromResult(transfers) };
        AddHost("host-a", fake);
        var vm = MakeVm();
        _fx.Start();
        await _fx.SettleAsync(() => vm.Rows.Count == 2);

        vm.RowsView.SortDescriptions.Clear();
        vm.RowsView.SortDescriptions.Add(
            Avalonia.Collections.DataGridSortDescription.FromPath("Data.Fraction"));
        Assert.Equal(["small", "large"], vm.RowsView.Cast<TransferRow>().Select(r => r.Data.Name));

        // The next poll inverts the fractions — in-place Updates only.
        transfers =
        [
            TestData.MakeTransfer(name: "small", nbytes: 100 * mb, bytesXferred: 75 * mb),
            TestData.MakeTransfer(name: "large", nbytes: 100 * mb, bytesXferred: 50 * mb),
        ];
        _fx.Store.RequestRefresh(null);
        await _fx.SettleAsync(() => vm.Rows.Single(r => r.Data.Name == "small").Data.Fraction == 0.75);

        Assert.Equal(["large", "small"], vm.RowsView.Cast<TransferRow>().Select(r => r.Data.Name));
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
        _fx.Start();
        await _fx.SettleAsync(() => vm.Rows.Count == 1);

        var nextRequestTime = _fx.Store.Hosts[0].Snapshot!.Transfers.Single().Transfer.NextRequestTime!.Value;

        // Sync the manual clock so the countdown reads exactly 10s remaining,
        // independent of real wall-clock timing (the state classification
        // itself was already fixed at poll time by SnapshotBuilder). Setting
        // Now alone does not trigger a Rebuild — only Advance fires Tick — so
        // land one second short first, then Advance onto the checkpoint.
        _fx.Clock.Now = nextRequestTime - TimeSpan.FromSeconds(11);
        _fx.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("Retry in 00:10 (attempt 2)", vm.Rows[0].Data.StatusText);

        _fx.Clock.Advance(TimeSpan.FromSeconds(1));

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
        _fx.Start();
        await _fx.SettleAsync(() => vm.Rows.Count == 2);

        var survivor = vm.Rows.Single(r => r.Data.Name == "file_2");
        var actions = new List<NotifyCollectionChangedAction>();
        vm.Rows.CollectionChanged += (_, e) => actions.Add(e.Action);

        transfers = [TestData.MakeTransfer(name: "file_2")];
        _fx.Store.RequestRefresh(null);
        await _fx.SettleAsync(() => vm.Rows.Count == 1);

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

        _fx.Start();
        await _fx.SettleAsync(() => !vm.IsLoading, "loading should clear once the first snapshot lands");

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
        _fx.Start();
        await _fx.SettleAsync(() => vm.Rows.Count == 3);

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
        _fx.Start();

        await _fx.SettleAsync(() => vm.Rows.Count == 3, "both hosts should contribute rows");
        Assert.Equal("3 transfers · 2 up · 1 down", vm.CountsText);

        // Kill B the same way the partial-bar tests do — poll failure, then auth
        // failure on reconnect — parking it in AuthFailed with its snapshot cached.
        fakeB.OnGetCcStatus = () => throw new BoincConnectionException("poll boom");
        fakeB.OnAuthorize = _ => Task.FromResult(false);
        _fx.Store.RequestRefresh(hostB.Id);
        await _fx.SettleAsync(() =>
            _fx.Store.Hosts.Single(h => h.Config.Id == hostB.Id).Status.State == HostConnectionState.Retrying);
        _fx.Store.RequestRefresh(hostB.Id); // skip the remaining backoff, retry now
        await _fx.SettleAsync(() =>
            _fx.Store.Hosts.Single(h => h.Config.Id == hostB.Id).Status.State == HostConnectionState.AuthFailed);

        await _fx.SettleAsync(() => vm.Rows.Count == 1, "the dead host's stale rows should drop out");
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
        _fx.Start();

        await _fx.SettleAsync(() =>
            _fx.Store.Hosts.Single(h => h.Config.Id == hostA.Id).Status.State == HostConnectionState.AuthFailed);
        await _fx.SettleAsync(() =>
            _fx.Store.Hosts.Single(h => h.Config.Id == hostB.Id).Snapshot is not null);

        await _fx.SettleAsync(
            () => vm.PartialBarText == string.Format(Strings.TransfersPartialFmt, 1, 2, 1),
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
            () => vm.PartialBarText == string.Format(Strings.TransfersPartialFmt, 2, 2, 0),
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
        _fx.Start();

        await _fx.SettleAsync(() => vm.Rows.Count == 2, "B and C should contribute rows");
        await _fx.SettleAsync(
            () => vm.PartialBarText == string.Format(Strings.TransfersPartialFmt, 1, 3, 2),
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
                  && vm.PartialBarText == string.Format(Strings.TransfersPartialFmt, 1, 3, 1),
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
        _fx.Start();
        await _fx.SettleAsync(() =>
            _fx.Store.Hosts.Single(h => h.Config.Id == hostB.Id).Status.State == HostConnectionState.Connected);

        // Break B and drive it to AuthFailed by advancing virtual time, exactly as the
        // sibling HostMonitorPollingTests reconnect facts do (fixture AdvanceUntilAsync).
        // Repeated advances fire the steady-state poll wait (-> failing tick -> Retrying)
        // and then the backoff wait (-> reconnect -> rejected auth -> AuthFailed). This is
        // race-free by construction: a wait the monitor enters AFTER one advance is caught
        // by the next. It deliberately does NOT use RequestRefresh nudges — a single nudge
        // can be silently coalesced with the sticky wake UpdateHost leaves behind on the
        // heal below, stranding a broken B in Connected on the frozen clock (the ~5 s
        // timeout this fact used to flake with under load).
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
    public async Task IsCompact_toggle_preserves_a_column_preference_saved_by_another_view_model_after_construction()
    {
        // Codex P2 (PR #45): both TasksViewModel and TransfersViewModel load a
        // UiState snapshot once at construction and, pre-fix, wrote the WHOLE
        // cached snapshot back on every change. Here a Tasks VM (sharing the
        // same UiStateStore/path, as ShellViewModel wires them in production)
        // saves a column preference AFTER `vm` already cached its own snapshot
        // — a stale whole-snapshot Save from `vm` must not drop it.
        // DensityPreference.Set and UiStateStore.Update both re-load fresh
        // before persisting, so this holds regardless of write order.
        AddHost("host-a", new FakeGuiRpcClient());
        var vm = MakeVm();
        _fx.Start();
        await _fx.SettleAsync(() => !vm.IsLoading);

        using var tasksVm = new TasksViewModel(_fx.Store, _fx.Clock, _fx.UiState, _fx.Density);
        tasksVm.SetColumnPreference("Elapsed", false);

        // Transfers only ever touches CompactDensity (via the shared
        // DensityPreference), predating Tasks' ColumnVisibility write.
        vm.IsCompact = true;

        var reloaded = _fx.UiState.Load();
        Assert.True(reloaded.CompactDensity, "Transfers' own density change must be saved");
        Assert.True(reloaded.ColumnVisibility.TryGetValue("Elapsed", out var elapsedVisible),
            "Tasks' column preference must survive Transfers' later save");
        Assert.False(elapsedVisible);
    }

    [Fact]
    public async Task Partial_bar_coverage_counts_only_hosts_feeding_the_grid()
    {
        // Mirrors TasksViewModelTests' identical-name fact (Codex P2): the
        // covered count {2} in TransfersPartialFmt must describe the hosts whose
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
        _fx.Start();

        await _fx.SettleAsync(() => vm.Rows.Count == 2, "B and C should contribute rows");
        await _fx.SettleAsync(
            () => vm.PartialBarText == string.Format(Strings.TransfersPartialFmt, 1, 3, 2),
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
            () => vm.PartialBarText == string.Format(Strings.TransfersPartialFmt, 1, 3, 1),
            "covered count should shrink to the hosts actually in the grid");
    }
}
