using Lattice.Boinc.GuiRpc;
using Lattice.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Lattice.Tests.Interleaving;

/// <summary>
/// Pins (expected to PASS on current code): calling the monitor's own public API
/// synchronously from INSIDE one of its event dispatches must be safe — events fire
/// on the loop's context, so a reentrant call runs on the loop thread itself.
/// </summary>
public class ReentrancyTests
{
    [Fact]
    public async Task ReentrantUpdateConfigFromStatusChangedIsSafe()
    {
        // The subscriber calls UpdateConfig synchronously from inside the Connected
        // StatusChanged dispatch (one-shot). Generation stamping mirrors the sweep:
        // DaemonVersion minor / snapshot HostName / message Body carry the generation.
        Guid hostId = Guid.NewGuid();
        HostConfig config1 = TestData.MakeHostConfig(id: hostId, name: "gen1", port: 31417);
        HostConfig config2 = TestData.MakeHostConfig(id: hostId, name: "gen2", port: 31418);
        int currentGen = 1;
        Func<IGuiRpcClient> factory = () =>
        {
            int gen = Volatile.Read(ref currentGen);
            return new FakeGuiRpcClient
            {
                OnExchangeVersions = () => Task.FromResult(new VersionInfo(8, gen, 0)),
                OnGetMessages = seqno => Task.FromResult<IReadOnlyList<Message>>(
                    seqno == 0 ? [TestData.MakeMessage(1, $"gen{gen}")] : []),
            };
        };

        object gate = new();
        List<string> snapshotTags = [];
        List<string> messageTags = [];
        bool reentered = false;
        int snapshotMarker = 0, messageMarker = 0;

        await using var monitor = new HostMonitor(config1, factory, new FakeTimeProvider(), 5);
        monitor.SnapshotUpdated += (_, s) => { lock (gate) snapshotTags.Add(s.HostName); };
        monitor.MessagesAdded += (_, e) => { lock (gate) messageTags.Add(e.Messages[0].Body); };
        monitor.StatusChanged += (_, s) =>
        {
            if (s.State != HostConnectionState.Connected || reentered)
                return;
            reentered = true;
            // Reentrant: on the loop's own thread, from inside the Connected dispatch.
            Volatile.Write(ref currentGen, 2);
            monitor.UpdateConfig(config2);
            lock (gate)
            {
                snapshotMarker = snapshotTags.Count;
                messageMarker = messageTags.Count;
            }
        };
        monitor.Start();

        // No deadlock, no fault: the monitor converges to Connected on the new generation.
        await Wait.UntilAsync(
            () => monitor.Status is { State: HostConnectionState.Connected, DaemonVersion.Minor: 2 },
            "the reentrant config change must reconnect on the new generation");
        await Wait.UntilAsync(() => monitor.Snapshot?.HostName == "gen2");
        Assert.False(monitor._loop.IsFaulted);
        Assert.True(reentered);

        // No old-generation snapshot/message publish after the reentrant call returned.
        lock (gate)
        {
            Assert.DoesNotContain("gen1", snapshotTags.Skip(snapshotMarker));
            Assert.DoesNotContain("gen1", messageTags.Skip(messageMarker));
        }
    }

    [Fact]
    public async Task ReentrantRequestRefreshFromSnapshotUpdatedIsSafe()
    {
        // The subscriber calls RequestRefresh synchronously from inside the
        // SnapshotUpdated dispatch (one-shot). The wake must be consumed by the next
        // poll wait: an extra tick happens promptly with NO fake-time advance.
        var fake = new FakeGuiRpcClient();
        bool refreshed = false;
        await using var monitor = new HostMonitor(
            TestData.MakeHostConfig(), () => fake, new FakeTimeProvider(), 5);
        monitor.SnapshotUpdated += (_, _) =>
        {
            if (refreshed)
                return;
            refreshed = true;
            monitor.RequestRefresh();
        };
        monitor.Start();

        await Wait.UntilAsync(() => fake.Calls.Count(c => c == "get_cc_status") >= 2,
            "the reentrant wake must produce a second tick without advancing time");
        Assert.Equal(HostConnectionState.Connected, monitor.Status.State);
        Assert.False(monitor._loop.IsFaulted);

        // The loop keeps running: a later external refresh still produces another tick.
        monitor.RequestRefresh();
        await Wait.UntilAsync(() => fake.Calls.Count(c => c == "get_cc_status") >= 3,
            "the loop must still be alive after the reentrant call");
    }
}
