using System.Reflection;
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
    public void Interval_change_with_no_hosts_does_not_raise_Changed()
    {
        var store = new HostStore(_registry, _manager, new ImmediateUiDispatcher());
        var changes = 0;
        store.Changed += (_, _) => changes++;

        _registry.SetPollingInterval(30);

        // Nothing is cached, so nothing on screen depends on the interval —
        // a Changed here would only trigger a redundant re-render.
        Assert.Equal(0, changes);
    }

    [Fact]
    public void Interval_change_with_hosts_raises_Changed_for_the_polling_status_text()
    {
        _registry.AddHost(TestData.MakeHostConfig());
        var store = new HostStore(_registry, _manager, new ImmediateUiDispatcher());
        var changes = 0;
        store.Changed += (_, _) => changes++;

        _registry.SetPollingInterval(30);

        Assert.Equal(1, changes);
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

    [Fact]
    public void MessagesReceived_fires_after_drain_with_the_host_id_and_batch()
    {
        var host = TestData.MakeHostConfig();
        _registry.AddHost(host);
        var queue = new QueueUiDispatcher();
        var store = new HostStore(_registry, _manager, queue);
        MessagesAddedEventArgs? received = null;
        store.MessagesReceived += (_, e) => received = e;

        var batch = new[] { TestData.MakeMessage(1), TestData.MakeMessage(2) };
        RaiseManagerMessagesAdded(new MessagesAddedEventArgs(host.Id, batch));

        // Marshaling contract: nothing is delivered until the UI queue runs.
        Assert.Null(received);
        queue.Drain();

        Assert.NotNull(received);
        Assert.Equal(host.Id, received!.HostId);
        Assert.Same(batch, received.Messages);   // forwarded verbatim
    }

    [Fact]
    public void Message_batch_does_not_raise_Changed()
    {
        var host = TestData.MakeHostConfig();
        _registry.AddHost(host);
        var queue = new QueueUiDispatcher();
        var store = new HostStore(_registry, _manager, queue);
        var changes = 0;
        store.Changed += (_, _) => changes++;
        var received = 0;
        store.MessagesReceived += (_, _) => received++;

        RaiseManagerMessagesAdded(new MessagesAddedEventArgs(host.Id, [TestData.MakeMessage(1)]));
        queue.Drain();

        // Batches arrive every poll tick; forwarding one must NOT rebuild the
        // snapshot-driven views — it rides its own channel, not Changed.
        Assert.Equal(0, changes);
        Assert.Equal(1, received);
    }

    [Fact]
    public void Disposed_store_drops_queued_message_batch()
    {
        var host = TestData.MakeHostConfig();
        _registry.AddHost(host);
        var queue = new QueueUiDispatcher();
        var store = new HostStore(_registry, _manager, queue);
        var received = 0;
        store.MessagesReceived += (_, _) => received++;

        RaiseManagerMessagesAdded(new MessagesAddedEventArgs(host.Id, [TestData.MakeMessage(1)]));
        store.Dispose();
        queue.Drain();

        Assert.Equal(0, received);
    }

    [Fact]
    public void Message_batch_for_a_host_not_in_the_store_is_dropped()
    {
        _registry.AddHost(TestData.MakeHostConfig());
        var queue = new QueueUiDispatcher();
        var store = new HostStore(_registry, _manager, queue);
        var received = 0;
        store.MessagesReceived += (_, _) => received++;

        // HostId absent from the store (as after a removal that raced the queued
        // batch). The Find guard drops it, same as the status/snapshot paths.
        RaiseManagerMessagesAdded(new MessagesAddedEventArgs(Guid.NewGuid(), [TestData.MakeMessage(1)]));
        queue.Drain();

        Assert.Equal(0, received);
    }

    // HostStore forwards HostMonitorManager.MessagesAdded as MessagesReceived. Real
    // polling couples that event with a SnapshotUpdated (which DOES raise Changed)
    // on every tick, so the forwarding contract — Find guard, disposed guard, and
    // the Changed decoupling — can only be exercised in isolation by raising the
    // manager's event on its own. The event is public but only its declaring type
    // may invoke it, so the test reaches the compiler's field-like backing delegate
    // by reflection. This keeps the four cases fully synchronous and deterministic.
    private void RaiseManagerMessagesAdded(MessagesAddedEventArgs args)
    {
        FieldInfo field = typeof(HostMonitorManager)
            .GetField(nameof(HostMonitorManager.MessagesAdded), BindingFlags.Instance | BindingFlags.NonPublic)!;
        var handler = (EventHandler<MessagesAddedEventArgs>?)field.GetValue(_manager);
        handler?.Invoke(_manager, args);
    }
}
