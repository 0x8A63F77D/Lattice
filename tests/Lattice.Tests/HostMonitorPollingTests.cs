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
}
