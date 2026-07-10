using Lattice.App.Infrastructure;
using Lattice.App.Tests.Fakes;
using Lattice.Core;
using Lattice.Tests;
using Xunit;

namespace Lattice.App.Tests;

public class HostStoreTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}.json");
    private HostRegistry _registry = null!;
    private HostMonitorManager _manager = null!;

    public ValueTask InitializeAsync()
    {
        _registry = new HostRegistry(new LatticeConfig(5, []), _path);
        _manager = new HostMonitorManager(_registry, () => new FakeGuiRpcClient(), TimeProvider.System);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _manager.DisposeAsync();
        File.Delete(_path);
    }

    [Fact]
    public void Seeds_one_entry_per_registered_host_with_disconnected_status()
    {
        var host = TestData.MakeHostConfig();
        _registry.AddHost(host);
        var store = new HostStore(_registry, _manager, new ImmediateUiDispatcher());

        var entry = Assert.Single(store.Hosts);
        Assert.Equal(host.Id, entry.Config.Id);
        Assert.Equal(HostConnectionState.Disconnected, entry.Status.State);
        Assert.Null(entry.Snapshot);
    }

    [Fact]
    public async Task Status_and_snapshot_flow_from_a_running_monitor()
    {
        _registry.AddHost(TestData.MakeHostConfig());
        var store = new HostStore(_registry, _manager, new ImmediateUiDispatcher());
        var changes = 0;
        store.Changed += (_, _) => Interlocked.Increment(ref changes);

        _manager.Start();

        await Wait.UntilAsync(
            () => store.Hosts[0].Status.State == HostConnectionState.Connected,
            "store should reach Connected");
        await Wait.UntilAsync(() => store.Hosts[0].Snapshot is not null, "snapshot should arrive");
        Assert.True(Volatile.Read(ref changes) > 0);
    }

    [Fact]
    public void Registry_add_and_remove_reshape_the_entry_list()
    {
        var store = new HostStore(_registry, _manager, new ImmediateUiDispatcher());
        Assert.Empty(store.Hosts);

        var host = TestData.MakeHostConfig();
        _registry.AddHost(host);
        Assert.Single(store.Hosts);

        _registry.RemoveHost(host.Id);
        Assert.Empty(store.Hosts);
    }

    [Fact]
    public void Registry_update_swaps_the_config_in_place()
    {
        var host = TestData.MakeHostConfig(name: "old");
        _registry.AddHost(host);
        var store = new HostStore(_registry, _manager, new ImmediateUiDispatcher());

        _registry.UpdateHost(host with { Name = "new" });

        Assert.Equal("new", Assert.Single(store.Hosts).Config.Name);
    }

    [Fact]
    public async Task Events_are_marshaled_through_the_dispatcher()
    {
        _registry.AddHost(TestData.MakeHostConfig());
        var queue = new QueueUiDispatcher();
        var store = new HostStore(_registry, _manager, queue);

        _manager.Start();
        await Wait.UntilAsync(() => queue.Pending > 0, "posts should arrive");

        // The marshaling contract itself: state must stay untouched until the
        // UI queue runs — pending events gate every mutation.
        Assert.Equal(HostConnectionState.Disconnected, store.Hosts[0].Status.State);
        Assert.Null(store.Hosts[0].Snapshot);

        await Wait.UntilAsync(() =>
        {
            queue.Drain();
            return store.Hosts[0].Status.State == HostConnectionState.Connected;
        }, "drained store should reach Connected");
    }

    [Fact]
    public async Task Disposed_store_ignores_queued_events()
    {
        _registry.AddHost(TestData.MakeHostConfig());
        var queue = new QueueUiDispatcher();
        var store = new HostStore(_registry, _manager, queue);
        var changed = false;
        store.Changed += (_, _) => changed = true;

        _manager.Start();
        await Wait.UntilAsync(() => queue.Pending > 0, "posts should arrive");

        store.Dispose();
        queue.Drain();

        Assert.Equal(HostConnectionState.Disconnected, store.Hosts[0].Status.State);
        Assert.False(changed);
    }

    [Fact]
    public async Task RequestRefresh_wakes_the_scoped_monitor()
    {
        // Own registry/manager: the polling interval (60s) must sit far beyond
        // Wait's 5s ceiling, otherwise the monitor's NATURAL periodic tick lands
        // inside the wait window and the test passes with an empty RequestRefresh.
        var path = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}.json");
        var registry = new HostRegistry(new LatticeConfig(60, []), path);
        var fake = new FakeGuiRpcClient();
        var manager = new HostMonitorManager(registry, () => fake, TimeProvider.System);
        try
        {
            var host = TestData.MakeHostConfig();
            registry.AddHost(host);
            var store = new HostStore(registry, manager, new ImmediateUiDispatcher());
            manager.Start();
            // Snapshot ⇒ the first tick's get_results is done; the loop is now in
            // its 60s poll wait, so only RequestRefresh can trigger another one.
            await Wait.UntilAsync(() => store.Hosts[0].Snapshot is not null, "first poll should complete");
            var before = fake.Calls.Count(c => c == "get_results");

            store.RequestRefresh(host.Id);

            await Wait.UntilAsync(
                () => fake.Calls.Count(c => c == "get_results") > before,
                "an immediate poll should follow the refresh request");
        }
        finally
        {
            await manager.DisposeAsync();
            File.Delete(path);
        }
    }
}
