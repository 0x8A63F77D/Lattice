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
/// Wraps the FakeGuiRpcClient dictionary and hands out a distinct fake per
/// host address, resolved on ConnectAsync — the only call that carries the
/// host identity through the shared, host-agnostic IGuiRpcClient factory.
/// </summary>
internal sealed class RoutingGuiRpcClient(IReadOnlyDictionary<string, FakeGuiRpcClient> fakes) : IGuiRpcClient
{
    private FakeGuiRpcClient? _target;

    public Task ConnectAsync(string host, int port = 31416, CancellationToken ct = default)
    {
        _target = fakes[host];
        return _target.ConnectAsync(host, port, ct);
    }

    public Task<bool> AuthorizeAsync(string password, CancellationToken ct = default) =>
        _target!.AuthorizeAsync(password, ct);

    public Task<VersionInfo> ExchangeVersionsAsync(CancellationToken ct = default) =>
        _target!.ExchangeVersionsAsync(ct);

    public Task<CcState> GetStateAsync(CancellationToken ct = default) =>
        _target!.GetStateAsync(ct);

    public Task<CcStatus> GetCcStatusAsync(CancellationToken ct = default) =>
        _target!.GetCcStatusAsync(ct);

    public Task<IReadOnlyList<Result>> GetResultsAsync(bool activeOnly = false, CancellationToken ct = default) =>
        _target!.GetResultsAsync(activeOnly, ct);

    public Task<IReadOnlyList<Message>> GetMessagesAsync(int seqno = 0, CancellationToken ct = default) =>
        _target!.GetMessagesAsync(seqno, ct);

    public Task<IReadOnlyList<FileTransfer>> GetFileTransfersAsync(CancellationToken ct = default) =>
        _target!.GetFileTransfersAsync(ct);

    public ValueTask DisposeAsync() => _target?.DisposeAsync() ?? ValueTask.CompletedTask;
}

/// <summary>
/// Runs posted work inline (no deferral, so Wait.UntilAsync polling still
/// works) but serialized under a lock. Plain ImmediateUiDispatcher runs each
/// post on whatever thread called it; with two ACTUALLY RUNNING monitors
/// (unlike every other fixture using it, which never starts more than one
/// live monitor at a time) that means two background threads can both be
/// inside HostStore.Changed -> TasksViewModel.Rebuild() at once, racing the
/// unsynchronized ObservableCollection Clear()+Add(). Production never hits
/// this: the real IUiDispatcher marshals every post onto one UI thread.
/// </summary>
internal sealed class LockingUiDispatcher : IUiDispatcher
{
    private readonly object _gate = new();
    public bool CheckAccess() => true;
    public void Post(Action action) { lock (_gate) action(); }
}

public class TasksViewModelTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}.json");
    private readonly Dictionary<string, FakeGuiRpcClient> _fakes = [];
    private HostRegistry _registry = null!;
    private HostMonitorManager _manager = null!;
    private HostStore _store = null!;
    private ManualUiClock _clock = null!;

    public ValueTask InitializeAsync()
    {
        _registry = new HostRegistry(new LatticeConfig(5, []), _path);
        _manager = new HostMonitorManager(_registry, () => new RoutingGuiRpcClient(_fakes), TimeProvider.System);
        _store = new HostStore(_registry, _manager, new LockingUiDispatcher());
        _clock = new ManualUiClock();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _manager.DisposeAsync();
        File.Delete(_path);
    }

    private HostConfig AddHost(string address, FakeGuiRpcClient fake)
    {
        var host = TestData.MakeHostConfig(name: address, address: address);
        _fakes[address] = fake;
        _registry.AddHost(host);
        return host;
    }

    private TasksViewModel MakeVm() => new(_store, _clock);

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
        _manager.Start();

        await Wait.UntilAsync(() => vm.Rows.Count == 2);

        Assert.True(vm.IsAllHostsScope);
        Assert.Contains(vm.Rows, r => r.Name == "task_a" && r.Host == "host-a");
        Assert.Contains(vm.Rows, r => r.Name == "task_b" && r.Host == "host-b");
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
        _manager.Start();
        await Wait.UntilAsync(() => vm.Rows.Count == 2);

        vm.Scope = new ScopeSelection(hostA.Id);

        Assert.False(vm.IsAllHostsScope);
        Assert.Equal("task_a", Assert.Single(vm.Rows).Name);
    }

    [Fact]
    public async Task Rows_sort_by_deadline_ascending_with_nulls_last()
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
        _manager.Start();
        await Wait.UntilAsync(() => vm.Rows.Count == 3);

        Assert.Equal(["sooner", "later", "no_deadline"], vm.Rows.Select(r => r.Name));
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
        _manager.Start();
        await Wait.UntilAsync(() => vm.Rows.Count == 2);

        vm.FilterText = "seti";

        Assert.Equal("alpha", Assert.Single(vm.Rows).Name);
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
        _manager.Start();
        await Wait.UntilAsync(() => vm.Rows.Count == 2);

        vm.StateFilter = TaskStateKind.Suspended;

        Assert.Equal("susp", Assert.Single(vm.Rows).Name);
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
        _manager.Start();
        await Wait.UntilAsync(() => vm.Rows.Count == 5);

        Assert.Equal(string.Format(Strings.CountsFmt, 5, 2, 1, 1), vm.CountsText);
    }

    [Fact]
    public async Task PollingText_reads_the_registry_interval()
    {
        AddHost("host-a", new FakeGuiRpcClient());
        var vm = MakeVm();
        _manager.Start();
        await Wait.UntilAsync(() => !vm.IsLoading, "first snapshot should land");

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
        _manager.Start();

        await Wait.UntilAsync(() =>
            _store.Hosts.Single(h => h.Config.Id == hostA.Id).Status.State == HostConnectionState.AuthFailed);
        await Wait.UntilAsync(() =>
            _store.Hosts.Single(h => h.Config.Id == hostB.Id).Snapshot is not null);

        await Wait.UntilAsync(() => vm.ShowPartialBar, "partial bar should appear for the AuthFailed host");
        Assert.Equal(string.Format(Strings.PartialFmt, 1, 2, 1), vm.PartialBarText);
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

        await Wait.UntilAsync(() => vm.ShowPartialBar, "partial bar should reappear: the unreachable id-set changed");
        Assert.Equal(string.Format(Strings.PartialFmt, 2, 2, 0), vm.PartialBarText);
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
        _manager.Start();

        // Baseline: both hosts' rows merged.
        await Wait.UntilAsync(() => vm.Rows.Count == 3, "both hosts should contribute rows");
        Assert.Contains(vm.Rows, r => r.Name == "b_task");

        // Kill B the same way the partial-bar test does — poll failure, then auth
        // failure on reconnect — parking it in AuthFailed with its snapshot cached.
        fakeB.OnGetCcStatus = () => throw new BoincConnectionException("poll boom");
        fakeB.OnAuthorize = _ => Task.FromResult(false);
        _store.RequestRefresh(hostB.Id);
        await Wait.UntilAsync(() =>
            _store.Hosts.Single(h => h.Config.Id == hostB.Id).Status.State == HostConnectionState.Retrying);
        _store.RequestRefresh(hostB.Id); // skip the remaining backoff, retry now
        await Wait.UntilAsync(() =>
            _store.Hosts.Single(h => h.Config.Id == hostB.Id).Status.State == HostConnectionState.AuthFailed);
        Assert.NotNull(_store.Hosts.Single(h => h.Config.Id == hostB.Id).Snapshot);

        // B's stale rows must vanish from the merge and from the counts.
        await Wait.UntilAsync(() => vm.Rows.Count == 2, "the dead host's stale rows should drop out");
        Assert.DoesNotContain(vm.Rows, r => r.Name == "b_task");
        Assert.Equal(["a_running", "a_waiting"], vm.Rows.Select(r => r.Name).Order());
        Assert.Equal(string.Format(Strings.CountsFmt, 2, 1, 0, 0), vm.CountsText);

        // And B now counts toward the partial-results bar (1 of 2 unreachable).
        Assert.True(vm.ShowPartialBar);
        Assert.Equal(string.Format(Strings.PartialFmt, 1, 2, 1), vm.PartialBarText);
    }

    [Fact]
    public async Task UpdatedText_ticks_with_the_manual_clock()
    {
        var fake = new FakeGuiRpcClient();
        AddHost("host-a", fake);
        var vm = MakeVm();
        _manager.Start();
        await Wait.UntilAsync(() => _store.Hosts[0].Snapshot is not null);

        _clock.Now = _store.Hosts[0].Snapshot!.Timestamp;
        _clock.Advance(TimeSpan.FromSeconds(3));

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
        _manager.Start();
        await Wait.UntilAsync(() => vm.Rows.Count == 1);

        var firstRow = vm.Rows[0];
        _clock.Now = _store.Hosts[0].Snapshot!.Timestamp;
        var collectionChanges = 0;
        vm.Rows.CollectionChanged += (_, _) => collectionChanges++;

        _clock.Advance(TimeSpan.FromSeconds(1));
        _clock.Advance(TimeSpan.FromSeconds(1));
        _clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(0, collectionChanges);
        Assert.Same(firstRow, vm.Rows[0]);
        // The tick path itself must stay alive: freshness text still advances.
        Assert.Equal(string.Format(Strings.UpdatedSecondsFmt, 3), vm.UpdatedText);
    }

    [Fact]
    public async Task IsLoading_until_the_first_snapshot_then_IsEmpty_with_zero_tasks()
    {
        var fake = new FakeGuiRpcClient();
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
}
