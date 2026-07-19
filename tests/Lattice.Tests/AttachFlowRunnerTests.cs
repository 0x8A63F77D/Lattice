using Lattice.Boinc.GuiRpc;
using Lattice.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Lattice.Tests;

/// <summary>
/// AttachFlowRunner drives the pure AttachMachine over one FakeGuiRpcClient
/// connection. Poll delays run on the fake TimeProvider; tests advance it until
/// the flow task settles (never wall-clock waits). Reaching Done is the runner's
/// obligation — these tests are the liveness leg the machine's FsCheck suite
/// deliberately leaves out (design Part 5).
/// </summary>
public class AttachFlowRunnerTests
{
    private static readonly string Url = "https://example.org/";

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}", "config.json");

    private static AttachMachine.AttachRequest EmailRequest() =>
        new(Url, "Example", AttachMachine.Credentials.NewEmailPassword("user@example.org", "pw"));

    private static AttachMachine.AttachRequest KeyRequest() =>
        new(Url, "Example", AttachMachine.Credentials.NewAuthenticatorKey("key123"));

    // IProgress that records synchronously on the reporting thread — Progress<T>
    // posts to a sync context and would race the assertions.
    private sealed class RecordingProgress : IProgress<AttachMachine.Stage>
    {
        private readonly object _gate = new();
        private readonly List<AttachMachine.Stage> _stages = [];
        public IReadOnlyList<AttachMachine.Stage> Stages { get { lock (_gate) return [.. _stages]; } }
        public void Report(AttachMachine.Stage value) { lock (_gate) _stages.Add(value); }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public Guid HostId { get; } = Guid.NewGuid();
        public FakeGuiRpcClient Fake { get; } = new();
        public FakeGuiRpcClient MonitorFake { get; } = new();
        public FakeTimeProvider Time { get; } = new();
        public HostRegistry Registry { get; }
        public HostMonitorManager Manager { get; }
        public HostControlService Service { get; }
        public AttachFlowRunner Runner { get; }

        // controlFactory serves the control LANE's side connections (the attach flow and
        // any control op submitted to Service); by default every lane turn gets Fake.
        public Fixture(string password = "", Func<IGuiRpcClient>? controlFactory = null)
        {
            var host = new HostConfig(HostId, "host", "127.0.0.1", 31416, password);
            Registry = new HostRegistry(new LatticeConfig(5, [host]), TempPath());
            Manager = new HostMonitorManager(Registry, () => MonitorFake, Time);
            Service = new HostControlService(Registry, Manager, controlFactory ?? (() => Fake));
            Runner = new AttachFlowRunner(Service, Manager, Time);
        }

        public ValueTask DisposeAsync() => Manager.DisposeAsync();
    }

    private static async Task<AttachFlowResult> RunToEndAsync(
        Fixture fx, AttachMachine.AttachRequest request,
        IProgress<AttachMachine.Stage>? progress = null, CancellationToken ct = default)
    {
        Task<AttachFlowResult> run = fx.Runner.RunAsync(fx.HostId, request, progress, ct);
        await Wait.AdvanceUntilAsync(fx.Time, () => run.IsCompleted, TimeSpan.FromSeconds(1));
        return await run;
    }

    [Fact]
    public async Task Email_flow_with_in_progress_polls_attaches_on_one_connection()
    {
        await using var fx = new Fixture();
        int polls = 0;
        fx.Fake.OnPollAccountLookup = () => Task.FromResult(++polls < 3
            ? new AccountLookupReply(BoincErrorCodes.InProgress, "", "")
            : new AccountLookupReply(0, "", "AUTH"));
        string? attachAuthenticator = null;
        string? attachEmail = null;
        fx.Fake.OnRequestProjectAttach = (_, authenticator, _, email) =>
        {
            attachAuthenticator = authenticator;
            attachEmail = email;
            return Task.CompletedTask;
        };
        fx.Fake.OnPollProjectAttach = () => Task.FromResult(new ProjectAttachReply(0, ["welcome"]));
        var progress = new RecordingProgress();

        AttachFlowResult result = await RunToEndAsync(fx, EmailRequest(), progress);

        Assert.Equal(AttachFlowOutcome.Attached, result.Outcome);
        Assert.Equal(["welcome"], result.Messages);
        // Lookup authenticator forwarded to the attach; email travels with it (W6).
        Assert.Equal("AUTH", attachAuthenticator);
        Assert.Equal("user@example.org", attachEmail);
        // The whole flow ran on ONE connection, in protocol order, no auth RPC
        // (empty password), and the polls reused the requesting connection.
        Assert.Equal(
            [
                "connect:127.0.0.1:31416",
                $"lookup_account:{Url}:user@example.org",
                "lookup_account_poll", "lookup_account_poll", "lookup_account_poll",
                $"project_attach:{Url}:Example",
                "project_attach_poll",
            ],
            fx.Fake.Calls);
        Assert.Equal(
            [AttachMachine.Stage.LookupStage, AttachMachine.Stage.AttachStage],
            progress.Stages);
        Assert.True(fx.Fake.Disposed);
    }

    [Fact]
    public async Task Authenticator_key_flow_skips_the_lookup_leg()
    {
        await using var fx = new Fixture();
        string? attachAuthenticator = null;
        string? attachEmail = null;
        fx.Fake.OnRequestProjectAttach = (_, authenticator, _, email) =>
        {
            attachAuthenticator = authenticator;
            attachEmail = email;
            return Task.CompletedTask;
        };
        var progress = new RecordingProgress();

        AttachFlowResult result = await RunToEndAsync(fx, KeyRequest(), progress);

        Assert.Equal(AttachFlowOutcome.Attached, result.Outcome);
        Assert.Equal("key123", attachAuthenticator);
        Assert.Equal("", attachEmail);
        Assert.DoesNotContain(fx.Fake.Calls, c => c.StartsWith("lookup_account"));
        Assert.Equal([AttachMachine.Stage.AttachStage], progress.Stages);
    }

    [Fact]
    public async Task Password_configured_authorizes_on_the_control_connection_first()
    {
        await using var fx = new Fixture(password: "s3cret");
        fx.Fake.OnPollAccountLookup = () => Task.FromResult(new AccountLookupReply(0, "", "AUTH"));

        AttachFlowResult result = await RunToEndAsync(fx, EmailRequest());

        Assert.Equal(AttachFlowOutcome.Attached, result.Outcome);
        Assert.Equal("connect:127.0.0.1:31416", fx.Fake.Calls[0]);
        Assert.Equal("authorize", fx.Fake.Calls[1]);
    }

    [Fact]
    public async Task Refused_password_fails_without_starting_the_flow()
    {
        await using var fx = new Fixture(password: "s3cret");
        fx.Fake.OnAuthorize = _ => Task.FromResult(false);

        AttachFlowResult result = await RunToEndAsync(fx, EmailRequest());

        Assert.Equal(AttachFlowOutcome.Faulted, result.Outcome);
        Assert.Equal("The host refused the password.", result.Error);
        Assert.DoesNotContain(fx.Fake.Calls, c => c.StartsWith("lookup_account"));
        Assert.True(fx.Fake.Disposed);
    }

    [Fact]
    public async Task Failed_lookup_reply_surfaces_as_LookupFailed()
    {
        await using var fx = new Fixture();
        // PR B parses a bare-<error> poll body into the reply — script the reply,
        // don't throw (plan E2.2).
        fx.Fake.OnPollAccountLookup = () =>
            Task.FromResult(new AccountLookupReply(-161, "no such account", ""));

        AttachFlowResult result = await RunToEndAsync(fx, EmailRequest());

        Assert.Equal(AttachFlowOutcome.LookupFailed, result.Outcome);
        Assert.Equal("no such account", result.Error);
        Assert.True(fx.Fake.Disposed);
    }

    [Fact]
    public async Task BoincRpcException_on_a_poll_feeds_back_as_that_stages_failure_not_a_throw()
    {
        await using var fx = new Fixture();
        fx.Fake.OnPollAccountLookup = () =>
            Task.FromException<AccountLookupReply>(new BoincRpcException("project says no"));

        AttachFlowResult result = await RunToEndAsync(fx, EmailRequest());

        // Design 1.2: an <error> reply to the poll means the LOOKUP failed, not the
        // RPC — the runner maps the exception to a failed-poll input (errorNum -1).
        Assert.Equal(AttachFlowOutcome.LookupFailed, result.Outcome);
        Assert.Equal("project says no", result.Error);
        Assert.True(fx.Fake.Disposed);
    }

    [Fact]
    public async Task Attach_failure_reply_surfaces_as_AttachFailed_with_messages()
    {
        await using var fx = new Fixture();
        fx.Fake.OnPollProjectAttach = () =>
            Task.FromResult(new ProjectAttachReply(-136, ["Already attached to project"]));

        AttachFlowResult result = await RunToEndAsync(fx, KeyRequest());

        Assert.Equal(AttachFlowOutcome.AttachFailed, result.Outcome);
        Assert.Equal(["Already attached to project"], result.Messages);
        Assert.True(fx.Fake.Disposed);
    }

    [Fact]
    public async Task Attach_request_rejected_by_the_daemon_surfaces_as_AttachFailed()
    {
        await using var fx = new Fixture();
        // Design 1.2: the daemon answers project_attach itself with <error> for an
        // already-attached URL — a daemon attach rejection, not a transport fault
        // (Codex P2 on PR #129).
        fx.Fake.OnRequestProjectAttach = (_, _, _, _) =>
            Task.FromException(new BoincRpcException("Already attached to project"));

        AttachFlowResult result = await RunToEndAsync(fx, KeyRequest());

        Assert.Equal(AttachFlowOutcome.AttachFailed, result.Outcome);
        Assert.Equal(["Already attached to project"], result.Messages);
        Assert.True(fx.Fake.Disposed);
    }

    [Fact]
    public async Task Lookup_request_rejected_by_the_daemon_surfaces_as_LookupFailed()
    {
        await using var fx = new Fixture();
        fx.Fake.OnRequestAccountLookup = (_, _) =>
            Task.FromException(new BoincRpcException("lookup rejected"));

        AttachFlowResult result = await RunToEndAsync(fx, EmailRequest());

        Assert.Equal(AttachFlowOutcome.LookupFailed, result.Outcome);
        Assert.Equal("lookup rejected", result.Error);
        Assert.True(fx.Fake.Disposed);
    }

    [Fact]
    public async Task Connection_death_mid_flow_surfaces_as_Faulted()
    {
        await using var fx = new Fixture();
        fx.Fake.OnPollAccountLookup = () =>
            Task.FromException<AccountLookupReply>(new BoincConnectionException("connection died"));

        AttachFlowResult result = await RunToEndAsync(fx, EmailRequest());

        Assert.Equal(AttachFlowOutcome.Faulted, result.Outcome);
        Assert.Equal("connection died", result.Error);
        Assert.True(fx.Fake.Disposed);
    }

    [Fact]
    public async Task Cancellation_mid_poll_returns_Canceled_and_disposes_the_client()
    {
        await using var fx = new Fixture();
        fx.Fake.OnPollAccountLookup = () =>
            Task.FromResult(new AccountLookupReply(BoincErrorCodes.InProgress, "", ""));
        using var cts = new CancellationTokenSource();

        Task<AttachFlowResult> run = fx.Runner.RunAsync(fx.HostId, EmailRequest(), null, cts.Token);
        // Let at least one poll round-trip happen, then cancel inside the next delay.
        await Wait.AdvanceUntilAsync(fx.Time,
            () => fx.Fake.Calls.Contains("lookup_account_poll"), TimeSpan.FromSeconds(1));
        cts.Cancel();
        AttachFlowResult result = await run;

        Assert.Equal(AttachFlowOutcome.Canceled, result.Outcome);
        Assert.True(fx.Fake.Disposed);
    }

    [Fact]
    public async Task Lookup_that_never_settles_times_out_after_the_poll_cap()
    {
        await using var fx = new Fixture();
        fx.Fake.OnPollAccountLookup = () =>
            Task.FromResult(new AccountLookupReply(BoincErrorCodes.Retry, "", ""));

        AttachFlowResult result = await RunToEndAsync(fx, EmailRequest());

        Assert.Equal(AttachFlowOutcome.TimedOut, result.Outcome);
        Assert.Equal(AttachMachine.PollLimit,
            fx.Fake.Calls.Count(c => c == "lookup_account_poll"));
        Assert.True(fx.Fake.Disposed);
    }

    [Fact]
    public async Task Unknown_host_fails_fast_without_connecting()
    {
        await using var fx = new Fixture();

        AttachFlowResult result = await fx.Runner.RunAsync(Guid.NewGuid(), EmailRequest());

        Assert.Equal(AttachFlowOutcome.Faulted, result.Outcome);
        Assert.Equal("The host is no longer registered.", result.Error);
        Assert.Empty(fx.Fake.Calls);
    }

    // ---- Lane integration (issue #130): the attach flow holds the host's control lane.
    // Composed invariant under test: the attach occupies the lane exclusively for its
    // whole duration (I-AF1/I-CL1); ops submitted meanwhile run afterwards in submission
    // order (I-CL5); and a faulted or canceled attach still releases the lane — the next
    // op MUST run (the chain's ignore-predecessor guard is load-bearing now that lane
    // turns can throw). Roles are pinned by factory-call index, not inferred from
    // execution order: lane turns are FIFO and the client factory is only invoked inside
    // a turn, so factory call 1 is deterministically the attach (submitted first and
    // parked holding the lane) and call 2 the queued task op.

    private static (Func<IGuiRpcClient> factory, FakeGuiRpcClient attachFake, FakeGuiRpcClient opFake)
        LaneFactory()
    {
        var attachFake = new FakeGuiRpcClient();
        var opFake = new FakeGuiRpcClient();
        int calls = 0;
        return (() => Interlocked.Increment(ref calls) == 1 ? attachFake : opFake, attachFake, opFake);
    }

    [Fact]
    public async Task Task_op_submitted_during_a_running_attach_waits_for_the_attach()
    {
        var (factory, attachFake, opFake) = LaneFactory();
        await using var fx = new Fixture(controlFactory: factory);
        // Strict cross-connection call order, appended from the fakes' hooks.
        var order = new List<string>();
        void Log(string call) { lock (order) order.Add(call); }
        var lookupGate = new TaskCompletionSource<AccountLookupReply>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        attachFake.OnPollAccountLookup = () => { Log("lookup_poll"); return lookupGate.Task; };
        attachFake.OnRequestProjectAttach = (_, _, _, _) => { Log("attach_request"); return Task.CompletedTask; };
        attachFake.OnPollProjectAttach = () =>
        { Log("attach_poll"); return Task.FromResult(new ProjectAttachReply(0, ["ok"])); };
        opFake.OnTaskOp = (_, _, _) => { Log("task_op"); return Task.CompletedTask; };

        Task<AttachFlowResult> attach = fx.Runner.RunAsync(fx.HostId, EmailRequest());
        // Park the attach inside its first lookup poll (it now holds the lane).
        await Wait.AdvanceUntilAsync(fx.Time,
            () => { lock (order) return order.Contains("lookup_poll"); }, TimeSpan.FromSeconds(1));

        Task<ControlOpResult> op = fx.Service.PerformTaskOpAsync(fx.HostId, TaskOp.Suspend, "url", "t");
        // The lane is held by the parked attach, so the op cannot have started or finished.
        Assert.False(op.IsCompleted);
        lock (order) Assert.DoesNotContain("task_op", order);

        lookupGate.SetResult(new AccountLookupReply(0, "", "AUTH"));
        await Wait.AdvanceUntilAsync(fx.Time,
            () => attach.IsCompleted && op.IsCompleted, TimeSpan.FromSeconds(1));

        Assert.Equal(AttachFlowOutcome.Attached, (await attach).Outcome);
        Assert.Equal(ControlOpOutcome.Succeeded, (await op).Outcome);
        // Strict order: the op ran strictly after the WHOLE attach flow, never interleaved.
        Assert.Equal(["lookup_poll", "attach_request", "attach_poll", "task_op"], order);
    }

    [Fact]
    public async Task A_canceled_attach_releases_the_lane_and_the_queued_op_still_runs()
    {
        var (factory, attachFake, opFake) = LaneFactory();
        await using var fx = new Fixture(controlFactory: factory);
        // Never-completing lookup poll; the runner's ct aborts it mid-poll.
        var never = new TaskCompletionSource<AccountLookupReply>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        attachFake.OnPollAccountLookup = () => never.Task;
        using var cts = new CancellationTokenSource();

        Task<AttachFlowResult> attach = fx.Runner.RunAsync(fx.HostId, EmailRequest(), null, cts.Token);
        await Wait.AdvanceUntilAsync(fx.Time,
            () => attachFake.Calls.Contains("lookup_account_poll"), TimeSpan.FromSeconds(1));
        Task<ControlOpResult> op = fx.Service.PerformTaskOpAsync(fx.HostId, TaskOp.Suspend, "url", "t");
        Assert.False(op.IsCompleted);   // queued behind the parked attach

        cts.Cancel();

        Assert.Equal(AttachFlowOutcome.Canceled, (await attach).Outcome);
        Assert.Equal(ControlOpOutcome.Succeeded, (await op).Outcome);
        Assert.Contains("task_op:suspend:url:t", opFake.Calls);
        Assert.True(attachFake.Disposed);
    }

    [Fact]
    public async Task A_faulted_attach_releases_the_lane_and_the_queued_op_still_runs()
    {
        var (factory, attachFake, opFake) = LaneFactory();
        await using var fx = new Fixture(controlFactory: factory);
        // The attach parks in its connect leg, then the connection attempt dies — this
        // faults the lane TURN itself (hook leg, not a machine-classified flow fault).
        var connectGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        attachFake.OnConnect = (_, _) => connectGate.Task;

        Task<AttachFlowResult> attach = fx.Runner.RunAsync(fx.HostId, EmailRequest());
        await Wait.UntilAsync(() => attachFake.Calls.Contains("connect:127.0.0.1:31416"),
            "the attach should be parked in its connect leg");
        Task<ControlOpResult> op = fx.Service.PerformTaskOpAsync(fx.HostId, TaskOp.Suspend, "url", "t");
        Assert.False(op.IsCompleted);   // queued behind the parked attach

        connectGate.SetException(new BoincConnectionException("host died"));

        AttachFlowResult attachResult = await attach;
        Assert.Equal(AttachFlowOutcome.Faulted, attachResult.Outcome);
        Assert.Equal("host died", attachResult.Error);
        Assert.Equal(ControlOpOutcome.Succeeded, (await op).Outcome);
        Assert.Contains("task_op:suspend:url:t", opFake.Calls);
        Assert.True(attachFake.Disposed);
    }

    [Fact]
    public async Task Successful_attach_nudges_the_host_monitor_for_a_refresh_tick()
    {
        await using var fx = new Fixture();
        fx.Fake.OnPollAccountLookup = () => Task.FromResult(new AccountLookupReply(0, "", "AUTH"));
        fx.Manager.Start();
        await Wait.UntilAsync(() => fx.Manager.Monitors[0].Snapshot is not null);
        int ticksBefore = fx.MonitorFake.Calls.Count(c => c == "get_cc_status");

        AttachFlowResult result = await RunToEndAsync(fx, EmailRequest());

        Assert.Equal(AttachFlowOutcome.Attached, result.Outcome);
        // RequestRefresh wakes the monitor's poll wait: a new tick lands without
        // the 5 s interval elapsing (settle on the fake's observed calls).
        await Wait.UntilAsync(
            () => fx.MonitorFake.Calls.Count(c => c == "get_cc_status") > ticksBefore,
            "refresh tick after successful attach");
    }
}
