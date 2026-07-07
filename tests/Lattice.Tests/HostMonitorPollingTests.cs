using Lattice.Boinc.GuiRpc;
using Lattice.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Lattice.Tests;

public class HostMonitorPollingTests
{
    private static HostConfig Config() => new(Guid.NewGuid(), "test", "localhost", 31416, "pw");

    [Fact]
    public void Message_log_caps_at_capacity()
    {
        var log = new MessageLog(5000);
        log.Append([.. Enumerable.Range(1, 5001).Select(i => TestData.MakeMessage(i))]);
        IReadOnlyList<Message> messages = log.Snapshot();
        Assert.Equal(5000, messages.Count);
        Assert.Equal(2, messages[0].Seqno);
        Assert.Equal(5001, messages[^1].Seqno);
    }

    [Fact]
    public async Task First_tick_publishes_snapshot_and_messages()
    {
        var fake = new FakeGuiRpcClient
        {
            OnGetResults = _ => Task.FromResult<IReadOnlyList<Result>>([TestData.MakeResult()]),
            OnGetFileTransfers = () => Task.FromResult<IReadOnlyList<FileTransfer>>([TestData.MakeTransfer()]),
            OnGetMessages = seqno => Task.FromResult<IReadOnlyList<Message>>(
                seqno == 0 ? [TestData.MakeMessage(1), TestData.MakeMessage(2)] : []),
        };
        List<MessagesAddedEventArgs> added = [];
        List<HostSnapshot> snapshots = [];
        await using var monitor = new HostMonitor(Config(), () => fake, new FakeTimeProvider(), 5);
        monitor.MessagesAdded += (_, e) => { lock (added) added.Add(e); };
        monitor.SnapshotUpdated += (_, s) => { lock (snapshots) snapshots.Add(s); };
        monitor.Start();
        await Wait.UntilAsync(() => monitor.Snapshot is not null);

        Assert.Single(monitor.Snapshot!.Tasks);
        Assert.Single(monitor.Snapshot.Transfers);
        Assert.Equal(2, monitor.Messages.Count);
        lock (added)
        {
            Assert.Single(added);
            Assert.Equal(monitor.HostId, added[0].HostId);
            Assert.Equal(2, added[0].Messages.Count);
        }
        lock (snapshots) Assert.Equal(monitor.HostId, snapshots[0].HostId);
        Assert.Contains("get_messages:0", fake.Calls);
    }

    [Fact]
    public async Task Second_tick_advances_message_seqno()
    {
        var fake = new FakeGuiRpcClient
        {
            OnGetMessages = seqno => Task.FromResult<IReadOnlyList<Message>>(
                seqno == 0 ? [TestData.MakeMessage(1), TestData.MakeMessage(2)] : []),
        };
        await using var monitor = new HostMonitor(Config(), () => fake, new FakeTimeProvider(), 5);
        monitor.Start();
        await Wait.UntilAsync(() => monitor.Snapshot is not null);
        monitor.RequestRefresh();
        await Wait.UntilAsync(() => fake.Calls.Contains("get_messages:2"));
        Assert.Equal(2, monitor.Messages.Count);   // nothing duplicated
    }

    [Fact]
    public async Task Unknown_workunit_triggers_state_refetch()
    {
        CcState knownState = TestData.MakeState(
            apps: [new App("app", "App")], workunits: [new Workunit("wu_1", "app", 0)]);
        CcState extendedState = TestData.MakeState(
            apps: [new App("app", "App")],
            workunits: [new Workunit("wu_1", "app", 0), new Workunit("wu_2", "app", 0)]);
        int stateCalls = 0;
        var results = new List<Result> { TestData.MakeResult(wuName: "wu_1") };
        var fake = new FakeGuiRpcClient
        {
            OnGetResults = _ => Task.FromResult<IReadOnlyList<Result>>([.. results]),
        };
        fake.OnGetState = () => Task.FromResult(Interlocked.Increment(ref stateCalls) == 1 ? knownState : extendedState);
        await using var monitor = new HostMonitor(Config(), () => fake, new FakeTimeProvider(), 5);
        monitor.Start();
        await Wait.UntilAsync(() => monitor.Snapshot is not null);
        Assert.Equal(1, stateCalls);

        results.Add(TestData.MakeResult(name: "task_2", wuName: "wu_2"));
        monitor.RequestRefresh();
        await Wait.UntilAsync(() => monitor.Snapshot?.Tasks.Count == 2);
        Assert.Equal(2, stateCalls);
        Assert.Equal("App", monitor.Snapshot!.Tasks[1].ApplicationName);
    }

    [Fact]
    public async Task Tick_failure_enters_retrying_then_recovers()
    {
        bool fail = false;
        var fake = new FakeGuiRpcClient();
        fake.OnGetCcStatus = () => fail
            ? Task.FromException<CcStatus>(new BoincConnectionException("poll died"))
            : Task.FromResult(FakeGuiRpcClient.DefaultStatus);
        var time = new FakeTimeProvider();
        await using var monitor = new HostMonitor(Config(), () => fake, time, 5);
        monitor.Start();
        await Wait.UntilAsync(() => monitor.Snapshot is not null);

        fail = true;
        monitor.RequestRefresh();
        await Wait.UntilAsync(() => monitor.Status.State == HostConnectionState.Retrying);
        Assert.Equal("poll died", monitor.Status.LastError);

        fail = false;
        await Wait.AdvanceUntilAsync(time,
            () => monitor.Status.State == HostConnectionState.Connected, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Mid_poll_unauthorized_reauths_once_and_continues()
    {
        int authCalls = 0;
        bool raised = false;
        var fake = new FakeGuiRpcClient();
        fake.OnAuthorize = _ => { Interlocked.Increment(ref authCalls); return Task.FromResult(true); };
        fake.OnGetCcStatus = () =>
        {
            if (!raised)
            {
                raised = true;
                return Task.FromException<CcStatus>(new BoincUnauthorizedException());
            }
            return Task.FromResult(FakeGuiRpcClient.DefaultStatus);
        };
        await using var monitor = new HostMonitor(Config(), () => fake, new FakeTimeProvider(), 5);
        monitor.Start();
        await Wait.UntilAsync(() => monitor.Snapshot is not null);
        Assert.Equal(2, authCalls);   // initial auth + one silent re-auth
        Assert.Equal(HostConnectionState.Connected, monitor.Status.State);
    }

    [Fact]
    public async Task Mid_poll_unauthorized_with_refused_reauth_is_auth_failed()
    {
        int authCalls = 0;
        var fake = new FakeGuiRpcClient();
        fake.OnAuthorize = _ => Task.FromResult(Interlocked.Increment(ref authCalls) == 1); // re-auth refused
        fake.OnGetCcStatus = () => Task.FromException<CcStatus>(new BoincUnauthorizedException());
        await using var monitor = new HostMonitor(Config(), () => fake, new FakeTimeProvider(), 5);
        monitor.Start();
        await Wait.UntilAsync(() => monitor.Status.State == HostConnectionState.AuthFailed);
    }

    [Fact]
    public async Task Reconnect_resets_message_cursor_and_log_after_daemon_restart()
    {
        bool failOnce = false;
        var fake = new FakeGuiRpcClient
        {
            OnGetCcStatus = () => failOnce
                ? Task.FromException<CcStatus>(new BoincConnectionException("daemon restarted"))
                : Task.FromResult(FakeGuiRpcClient.DefaultStatus),
            // The daemon's seqno counter reset on restart: both before and after the
            // simulated restart it serves the same seqno 1..2 from get_messages:0 —
            // proving the monitor actually re-asks from 0 rather than resuming from a
            // stale high cursor (which would ask for seqno > 2 and get nothing back).
            OnGetMessages = seqno => Task.FromResult<IReadOnlyList<Message>>(
                seqno == 0 ? [TestData.MakeMessage(1), TestData.MakeMessage(2)] : []),
        };
        var time = new FakeTimeProvider();
        await using var monitor = new HostMonitor(Config(), () => fake, time, 5);
        monitor.Start();
        await Wait.UntilAsync(() => monitor.Snapshot is not null);
        Assert.Equal(2, monitor.Messages.Count);
        Assert.Contains("get_messages:0", fake.Calls);

        // Simulate daemon restart: force a tick failure so the monitor tears down and
        // reconnects. Without the fix, the reconnect would keep _lastSeqno == 2 and
        // ask get_messages:2, silently missing the "new" (to the reset daemon) 1..2.
        failOnce = true;
        monitor.RequestRefresh();
        await Wait.UntilAsync(() => monitor.Status.State == HostConnectionState.Retrying);
        failOnce = false;
        await Wait.AdvanceUntilAsync(time,
            () => monitor.Status.State == HostConnectionState.Connected, TimeSpan.FromSeconds(1));
        await Wait.UntilAsync(() => monitor.Snapshot is not null);

        // The reconnect must have re-fetched from seqno 0 and the log must contain
        // exactly the refetched set (no stale entries carried over, none missed).
        Assert.Equal(2, monitor.Messages.Count);
        Assert.Equal(1, monitor.Messages[0].Seqno);
        Assert.Equal(2, monitor.Messages[1].Seqno);
        List<string> messageCalls = [.. fake.Calls.Where(c => c.StartsWith("get_messages:"))];
        Assert.Equal("get_messages:0", messageCalls[^1]);
    }

    [Fact]
    public async Task Mid_poll_repeated_unauthorized_after_reauth_backs_off_instead_of_spinning()
    {
        int authCalls = 0;
        var fake = new FakeGuiRpcClient();
        fake.OnAuthorize = _ => { Interlocked.Increment(ref authCalls); return Task.FromResult(true); };
        fake.OnGetCcStatus = () => Task.FromException<CcStatus>(new BoincUnauthorizedException());
        await using var monitor = new HostMonitor(Config(), () => fake, new FakeTimeProvider(), 5);
        monitor.Start();
        await Wait.UntilAsync(() => monitor.Status.State == HostConnectionState.Retrying);
        Assert.Equal(2, authCalls);   // initial auth + exactly one silent re-auth, no more
    }
}
