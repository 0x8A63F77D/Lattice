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

    [Fact]
    public async Task AbortedAttemptNeverDestroysMessageLog()
    {
        // Regression test for the pre-fix message-log clear-on-reconnect defect.
        // RunAttemptAsync used to run `_messages.Clear()` immediately after get_state
        // and BEFORE the BeforeAcceptGuard config check — destroying the user-visible
        // log inside a reconnect attempt that had not yet been accepted (and could
        // still be aborted by a config change). The fix keeps the retained log intact
        // until a new connection's first tick atomically replaces it.
        //
        // Freeze a reconnect attempt exactly at BeforeAcceptGuard (post get_state,
        // pre-accept — where the old code had already cleared) and assert the log is
        // still intact there. Then abort the frozen attempt with a second config change
        // and assert the log survives the abort and every later reconnect.
        var fake = new FakeGuiRpcClient
        {
            OnGetMessages = seqno => Task.FromResult<IReadOnlyList<Message>>(
                seqno == 0 ? [TestData.MakeMessage(1), TestData.MakeMessage(2)] : []),
            OnGetResults = _ => Task.FromResult<IReadOnlyList<Result>>([]),
            OnGetFileTransfers = () => Task.FromResult<IReadOnlyList<FileTransfer>>([]),
        };
        var controller = new ProbeController();
        var time = new FakeTimeProvider();
        HostConfig config = Config();
        await using var monitor = new HostMonitor(config, () => fake, time, 5);
        monitor.InterleaveProbe = controller.Probe;
        monitor.Start();

        // Connection #1: the first tick publishes the log [1,2]. BeforeAcceptGuard is
        // armed only AFTER this, so connection #1's own pass through that point is never
        // frozen — and steady-state polling never revisits BeforeAcceptGuard, so arming
        // here is race-free: the next (and only) pass is the reconnect attempt below.
        await Wait.UntilAsync(() => monitor.Snapshot is not null);
        Assert.Equal(2, monitor.Messages.Count);
        Assert.Equal(1, monitor.Messages[0].Seqno);
        Assert.Equal(2, monitor.Messages[1].Seqno);

        // Force a reconnect and catch the reconnect attempt at BeforeAcceptGuard.
        controller.FreezeAt(InterleavePoints.BeforeAcceptGuard);
        monitor.UpdateConfig(config with { Address = "newhost" });
        await controller.WaitForAsync(InterleavePoints.BeforeAcceptGuard);

        // The try/finally guarantees the frozen loop is always released, so a failing
        // assertion below surfaces as a clean test failure rather than a disposal hang.
        try
        {
            // LOAD-BEARING: post get_state, pre-accept, inside the reconnect attempt —
            // exactly where the pre-fix code had already run _messages.Clear(). The
            // retained log must still be intact. (Pre-fix: Count == 0 → assertion fails.)
            Assert.Equal(2, monitor.Messages.Count);
            Assert.Equal(1, monitor.Messages[0].Seqno);
            Assert.Equal(2, monitor.Messages[1].Seqno);

            // Abort THIS frozen attempt with a second config change: the accept guard
            // just past the freeze sees _configChanged and returns ConfigChanged, so this
            // attempt never reaches Connected or a tick.
            monitor.UpdateConfig(config with { Address = "newerhost" });
        }
        finally
        {
            controller.Release();
        }

        // The log survives the abort and the subsequent reconnect: it is only ever
        // atomically replaced by a new connection's first tick (with the same [1,2] the
        // daemon serves), never cleared to empty.
        await Wait.UntilAsync(() => monitor.Status.State == HostConnectionState.Connected);
        await Wait.UntilAsync(() => monitor.Snapshot is not null);
        Assert.Equal(2, monitor.Messages.Count);
        Assert.Equal(1, monitor.Messages[0].Seqno);
        Assert.Equal(2, monitor.Messages[1].Seqno);
    }

    [Fact]
    public async Task FirstTickReplacesLogAtomically()
    {
        // The retained log must survive a reconnect all the way until the new
        // connection's first tick REPLACES it — the pre-fix code instead cleared it
        // early, inside RunAttemptAsync, before the tick ran. Freeze the reconnect's
        // first tick at TickBeforeMsgPublish (get_messages has returned, log not yet
        // mutated) and assert the OLD log is still visible there.
        //
        // The restarted daemon's seqno counter has reset, so it serves a single message
        // with seqno 1 again: replace-then-count and (pre-fix) clear-then-append both
        // yield Count == 1 afterwards, so the distinguishing evidence is the surviving
        // OLD log observed in the pre-publish window while frozen.
        bool failStatus = false;
        var fake = new FakeGuiRpcClient
        {
            OnGetMessages = seqno => Task.FromResult<IReadOnlyList<Message>>(
                seqno == 0 ? [TestData.MakeMessage(1), TestData.MakeMessage(2)] : []),
            OnGetResults = _ => Task.FromResult<IReadOnlyList<Result>>([]),
            OnGetFileTransfers = () => Task.FromResult<IReadOnlyList<FileTransfer>>([]),
            OnGetCcStatus = () => failStatus
                ? throw new BoincConnectionException("daemon restarted")
                : Task.FromResult(FakeGuiRpcClient.DefaultStatus),
        };
        var controller = new ProbeController();
        var time = new FakeTimeProvider();
        HostConfig config = Config();
        var messagesAdded = new List<MessagesAddedEventArgs>();

        await using var monitor = new HostMonitor(config, () => fake, time, 5);
        monitor.MessagesAdded += (_, e) => { lock (messagesAdded) messagesAdded.Add(e); };
        monitor.InterleaveProbe = controller.Probe;
        monitor.Start();

        // Connection #1: log [1,2]. Its own first tick already passed
        // TickBeforeMsgPublish before this await returns, so nothing is armed yet.
        await Wait.UntilAsync(() => monitor.Snapshot is not null);
        Assert.Equal(2, monitor.Messages.Count);
        Assert.Equal(1, monitor.Messages[0].Seqno);
        Assert.Equal(2, monitor.Messages[1].Seqno);

        // Break the connection: the next tick throws, the host drops to Retrying and
        // parks in the (never-advanced) backoff wait — no attempt is in flight there.
        failStatus = true;
        monitor.RequestRefresh();
        await Wait.UntilAsync(() => monitor.Status.State == HostConnectionState.Retrying);

        // Parked in Retrying: safe to reconfigure the fake for the "restarted daemon"
        // whose seqno counter reset (one message, seqno 1) and to arm the reconnect's
        // tick freeze. Steady-state ticks — which would also pass TickBeforeMsgPublish —
        // cannot occur while parked, so arming here catches only the reconnect's first
        // tick.
        failStatus = false;
        fake.OnGetMessages = seqno => Task.FromResult<IReadOnlyList<Message>>(
            seqno == 0 ? [TestData.MakeMessage(1)] : []);
        controller.FreezeAt(InterleavePoints.TickBeforeMsgPublish);

        // Skip the backoff; the reconnect runs to its first tick and freezes just before
        // the log mutation.
        monitor.RequestRefresh();
        await controller.WaitForAsync(InterleavePoints.TickBeforeMsgPublish);

        // The try/finally guarantees the frozen loop is always released, so a failing
        // assertion below surfaces as a clean test failure rather than a disposal hang.
        try
        {
            // LOAD-BEARING: get_messages(0) has returned the restarted daemon's [1], but
            // the log has not been touched yet. The retained OLD log [1,2] must still be
            // visible. (Pre-fix: cleared inside RunAttemptAsync → empty → assertion fails.)
            Assert.Equal(2, monitor.Messages.Count);
            Assert.Equal(1, monitor.Messages[0].Seqno);
            Assert.Equal(2, monitor.Messages[1].Seqno);
        }
        finally
        {
            controller.Release();
        }

        // Let the first tick complete: the log is atomically replaced with the daemon's
        // current buffer — exactly the single seqno-1 message, not [1,2] with a seqno-1
        // message appended.
        await Wait.UntilAsync(() => monitor.Messages.Count == 1);

        Assert.Single(monitor.Messages);
        Assert.Equal(1, monitor.Messages[0].Seqno);

        // The reconnect's first tick raised MessagesAdded with exactly that single
        // message (connection #1's event carried [1,2] and does not match).
        lock (messagesAdded)
            Assert.Single(messagesAdded, e => e.Messages.Count == 1 && e.Messages[0].Seqno == 1);
    }
}
