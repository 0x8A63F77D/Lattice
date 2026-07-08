using Lattice.Boinc.GuiRpc;
using Lattice.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Lattice.Tests.Interleaving;

public class LifecycleTests
{
    [Fact]
    public async Task DoubleDisposeDoesNotThrow()
    {
        var fake = new FakeGuiRpcClient();
        var monitor = new HostMonitor(TestData.MakeHostConfig(), () => fake, new FakeTimeProvider(), 5);
        monitor.Start();
        await monitor.DisposeAsync();
        await monitor.DisposeAsync();   // was: ObjectDisposedException from _cts.Cancel()
    }

    [Fact]
    public async Task DisposeWithoutStartThenStartIsInert()
    {
        var fake = new FakeGuiRpcClient();
        var monitor = new HostMonitor(TestData.MakeHostConfig(), () => fake, new FakeTimeProvider(), 5);
        await monitor.DisposeAsync();
        monitor.Start();                // was: loop task faults reading _cts.Token
        // Deterministic: a post-dispose Start must not spawn a loop at all — _loop
        // stays the completed sentinel (and thus can never fault later).
        Assert.True(monitor._loop.IsCompletedSuccessfully);
        Assert.False(monitor._loop.IsFaulted);
        Assert.Equal(HostConnectionState.Disconnected, monitor.Status.State);
        await monitor.DisposeAsync();   // still idempotent afterwards
    }

    [Fact]
    public async Task ConcurrentStartAndDisposeSettleDisconnected()
    {
        var fake = new FakeGuiRpcClient();
        var monitor = new HostMonitor(TestData.MakeHostConfig(), () => fake, new FakeTimeProvider(), 5);
        var start = Task.Run(monitor.Start);
        var dispose = Task.Run(async () => await monitor.DisposeAsync());
        await Task.WhenAll(start, dispose);
        await monitor.DisposeAsync();   // idempotent regardless of the race outcome
        Assert.Equal(HostConnectionState.Disconnected, monitor.Status.State);
    }
}
