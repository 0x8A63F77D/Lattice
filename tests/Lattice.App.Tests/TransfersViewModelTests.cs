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
}
