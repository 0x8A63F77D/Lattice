using Lattice.Boinc.GuiRpc;
using Lattice.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Lattice.Tests.Interleaving;

public class SweepTests
{
    private static HostConfig Config(string password = "pw") =>
        new(Guid.NewGuid(), "test", "localhost", 31416, password);

    [Fact]
    public async Task Probe_seam_freezes_and_releases_at_designated_point()
    {
        var fake = new FakeGuiRpcClient();
        var controller = new ProbeController();
        await using var monitor = new HostMonitor(Config(), () => fake, new FakeTimeProvider(), 5);

        monitor.InterleaveProbe = controller.Probe;
        controller.FreezeAt(InterleavePoints.BeforeAcceptGuard);
        monitor.Start();

        await controller.WaitForAsync(InterleavePoints.BeforeAcceptGuard);
        Assert.Equal(HostConnectionState.FetchingState, monitor.Status.State);

        controller.Release();
        await Wait.UntilAsync(() => monitor.Status.State == HostConnectionState.Connected);
    }

    [Fact]
    public async Task FailedAttemptDoesNotPollute_DaemonVersion()
    {
        // First connection is accepted with daemon version 8.0 and ticks fine; then a
        // later poll tick fails, forcing Retrying (event #1). The reconnect attempt
        // reaches exchange_versions returning 9.9 but FAILS at get_state — never
        // accepted. The SECOND Retrying event must still carry 8.0 (the last ACCEPTED
        // version), not 9.9 (the unaccepted attempt's version).
        bool failResults = false;
        int stateCalls = 0;
        var fake = new FakeGuiRpcClient
        {
            // One counter drives both: connection #1 sees 8.0 and a good get_state;
            // connection #2 sees 9.9 and a throwing get_state.
            OnExchangeVersions = () => Task.FromResult(stateCalls == 0
                ? new VersionInfo(8, 0, 0)
                : new VersionInfo(9, 9, 0)),
            OnGetState = () => ++stateCalls == 1
                ? Task.FromResult(FakeGuiRpcClient.EmptyState)
                : throw new BoincConnectionException("get_state boom"),
            OnGetResults = _ => failResults
                ? throw new BoincConnectionException("results boom")
                : Task.FromResult<IReadOnlyList<Result>>([]),
        };

        List<ConnectionStatus> statusEvents = [];
        var time = new FakeTimeProvider();
        await using var monitor = new HostMonitor(Config(), () => fake, time, 5);
        monitor.StatusChanged += (_, s) => { lock (statusEvents) statusEvents.Add(s); };
        monitor.Start();

        // Connection #1 accepted (first tick included) with daemon version 8.0.
        await Wait.UntilAsync(() => monitor.Status.State == HostConnectionState.Connected);

        // Break the next poll tick: a post-Connected failure publishes Retrying #1.
        failResults = true;
        monitor.RequestRefresh();
        await Wait.UntilAsync(() => monitor.Status.State == HostConnectionState.Retrying);

        // Skip the backoff. The reconnect attempt fetches 9.9 from exchange_versions
        // but dies at get_state, publishing Retrying #2 for the unaccepted attempt.
        monitor.RequestRefresh();
        await Wait.UntilAsync(() =>
        {
            lock (statusEvents)
                return statusEvents.Count(s => s.State == HostConnectionState.Retrying) >= 2;
        });

        ConnectionStatus secondRetrying;
        lock (statusEvents)
            secondRetrying = statusEvents.Where(s => s.State == HostConnectionState.Retrying).ElementAt(1);
        Assert.Equal(new VersionInfo(8, 0, 0), secondRetrying.DaemonVersion);
    }
}
