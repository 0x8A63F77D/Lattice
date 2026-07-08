using Lattice.Boinc.GuiRpc;
using Lattice.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Lattice.Tests.Interleaving;

public class LifecycleTests
{
    private static HostConfig Config(string password = "pw") =>
        new(Guid.NewGuid(), "test", "localhost", 31416, password);

    [Fact]
    public async Task DoubleDisposeDoesNotThrow()
    {
        var fake = new FakeGuiRpcClient();
        var monitor = new HostMonitor(Config(), () => fake, new FakeTimeProvider(), 5);
        monitor.Start();
        await monitor.DisposeAsync();
        await monitor.DisposeAsync();   // was: ObjectDisposedException from _cts.Cancel()
    }

    [Fact]
    public async Task DisposeWithoutStartThenStartIsInert()
    {
        var fake = new FakeGuiRpcClient();
        var monitor = new HostMonitor(Config(), () => fake, new FakeTimeProvider(), 5);
        await monitor.DisposeAsync();
        monitor.Start();                // was: loop task faults reading _cts.Token
        await Task.Delay(50);           // give a would-be faulted task time to surface
        Assert.Equal(HostConnectionState.Disconnected, monitor.Status.State);
        await monitor.DisposeAsync();   // still idempotent afterwards
    }

    [Fact]
    public async Task ConcurrentStartAndDisposeSettleDisconnected()
    {
        var fake = new FakeGuiRpcClient();
        var monitor = new HostMonitor(Config(), () => fake, new FakeTimeProvider(), 5);
        var start = Task.Run(monitor.Start);
        var dispose = Task.Run(async () => await monitor.DisposeAsync());
        await Task.WhenAll(start, dispose);
        await monitor.DisposeAsync();   // idempotent regardless of the race outcome
        Assert.Equal(HostConnectionState.Disconnected, monitor.Status.State);
    }
}
