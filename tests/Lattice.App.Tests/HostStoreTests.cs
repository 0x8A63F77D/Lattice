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
        await Wait.UntilAsync(() => queue.Drain() > 0, "posts should arrive");
        await Wait.UntilAsync(() =>
        {
            queue.Drain();
            return store.Hosts[0].Status.State == HostConnectionState.Connected;
        }, "drained store should reach Connected");
    }
}
