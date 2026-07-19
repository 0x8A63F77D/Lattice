using Lattice.Boinc.GuiRpc;
using Lattice.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Lattice.Tests;

/// <summary>
/// <see cref="HostControlService"/> control-lane behavior. The lane's concurrency
/// invariants (stated in the service's source): same-host ops never overlap and run
/// in submission order (I-CL1/I-CL5); different-host ops proceed independently; a
/// faulted or canceled op never wedges the lane; every path returns a
/// <see cref="ControlOpResult"/>. Every assertion settles on the fake client's
/// observed calls — never on wall-clock time.
/// </summary>
public class HostControlServiceTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}", "config.json");

    private static HostConfig Host(string name = "h1", string address = "localhost", string password = "") =>
        new(Guid.NewGuid(), name, address, 31416, password);

    // A manual gate: a hook awaits Wait until Release() is called. RunContinuationsAsynchronously
    // so releasing never runs the continuation inline under a test lock.
    private sealed class Latch
    {
        private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Wait => _tcs.Task;
        public void Release() => _tcs.TrySetResult();
    }

    // High-water concurrency tracker: hooks Enter on entry and Exit on exit; Max is the
    // peak simultaneous occupancy. For same-host ops Max must stay 1; for two hosts it reaches 2.
    private sealed class Tracker
    {
        private readonly object _gate = new();
        private int _active;
        public int Max { get; private set; }
        public int Current { get { lock (_gate) return _active; } }
        public void Enter() { lock (_gate) { _active++; if (_active > Max) Max = _active; } }
        public void Exit() { lock (_gate) _active--; }
    }

    // Builds a service over a registry of the given hosts and an (un-started) monitor
    // manager. The monitor manager gets its own benign factory; the control factory is
    // the one under test. Returns the manager so callers can dispose it.
    private static (HostControlService service, HostMonitorManager manager, HostRegistry registry) Build(
        Func<IGuiRpcClient> controlFactory, params HostConfig[] hosts)
    {
        var registry = new HostRegistry(new LatticeConfig(60, [.. hosts]), TempPath());
        var manager = new HostMonitorManager(registry, () => new FakeGuiRpcClient(), new FakeTimeProvider());
        var service = new HostControlService(registry, manager, controlFactory);
        return (service, manager, registry);
    }

    // ---- Outcome taxonomy: each ControlOpOutcome reached via its scripted cause -------

    [Fact]
    public async Task Successful_op_returns_Succeeded()
    {
        HostConfig host = Host();
        var (service, manager, _) = Build(() => new FakeGuiRpcClient(), host);
        await using var _m = manager;

        ControlOpResult result = await service.PerformTaskOpAsync(host.Id, TaskOp.Suspend, "url", "task");

        Assert.Equal(ControlOpOutcome.Succeeded, result.Outcome);
    }

    [Fact]
    public async Task Connection_failure_maps_to_Unreachable_with_text()
    {
        HostConfig host = Host();
        var (service, manager, _) = Build(
            () => new FakeGuiRpcClient { OnConnect = (_, _) => throw new BoincConnectionException("down") }, host);
        await using var _m = manager;

        ControlOpResult result = await service.PerformProjectOpAsync(host.Id, ProjectOp.Update, "url");

        Assert.Equal(ControlOpOutcome.Unreachable, result.Outcome);
        Assert.Equal("down", result.Error);
    }

    [Fact]
    public async Task Unauthorized_during_op_maps_to_AuthFailed()
    {
        HostConfig host = Host(password: "pw");
        var (service, manager, _) = Build(
            () => new FakeGuiRpcClient { OnTaskOp = (_, _, _) => throw new BoincUnauthorizedException() }, host);
        await using var _m = manager;

        ControlOpResult result = await service.PerformTaskOpAsync(host.Id, TaskOp.Abort, "url", "task");

        Assert.Equal(ControlOpOutcome.AuthFailed, result.Outcome);
    }

    [Fact]
    public async Task Refused_password_maps_to_AuthFailed_without_running_the_op()
    {
        HostConfig host = Host(password: "pw");
        var fake = new FakeGuiRpcClient { OnAuthorize = _ => Task.FromResult(false) };
        var (service, manager, _) = Build(() => fake, host);
        await using var _m = manager;

        ControlOpResult result = await service.PerformTaskOpAsync(host.Id, TaskOp.Suspend, "url", "task");

        Assert.Equal(ControlOpOutcome.AuthFailed, result.Outcome);
        // The op RPC must not fire when auth was refused.
        Assert.DoesNotContain(fake.Calls, c => c.StartsWith("task_op:"));
    }

    [Fact]
    public async Task Daemon_error_maps_to_DaemonError_preserving_text()
    {
        HostConfig host = Host();
        var (service, manager, _) = Build(
            () => new FakeGuiRpcClient { OnProjectOp = (_, _) => throw new BoincRpcException("no such project") }, host);
        await using var _m = manager;

        ControlOpResult result = await service.PerformProjectOpAsync(host.Id, ProjectOp.Detach, "url");

        Assert.Equal(ControlOpOutcome.DaemonError, result.Outcome);
        Assert.Contains("no such project", result.Error);
    }

    [Fact]
    public async Task Unexpected_exception_maps_to_Unreachable()
    {
        HostConfig host = Host();
        var (service, manager, _) = Build(
            () => new FakeGuiRpcClient { OnConnect = (_, _) => throw new InvalidOperationException("boom") }, host);
        await using var _m = manager;

        ControlOpResult result = await service.SetModeAsync(host.Id, ModeLane.Cpu, RunMode.Never, TimeSpan.FromMinutes(15));

        Assert.Equal(ControlOpOutcome.Unreachable, result.Outcome);
    }

    [Fact]
    public async Task Precanceled_token_maps_to_Canceled_without_connecting()
    {
        HostConfig host = Host();
        int created = 0;
        var (service, manager, _) = Build(() => { created++; return new FakeGuiRpcClient(); }, host);
        await using var _m = manager;
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        ControlOpResult result = await service.PerformTaskOpAsync(host.Id, TaskOp.Suspend, "url", "task", cts.Token);

        Assert.Equal(ControlOpOutcome.Canceled, result.Outcome);
        Assert.Equal(0, created);   // cancellation observed before any connection
    }

    [Fact]
    public async Task Cancellation_mid_op_maps_to_Canceled()
    {
        HostConfig host = Host();
        var latch = new Latch();
        var (service, manager, _) = Build(
            () => new FakeGuiRpcClient { OnConnect = (_, _) => latch.Wait }, host);   // hangs until token cancels
        await using var _m = manager;
        using var cts = new CancellationTokenSource();

        Task<ControlOpResult> op = service.PerformTaskOpAsync(host.Id, TaskOp.Suspend, "url", "task", cts.Token);
        await cts.CancelAsync();

        Assert.Equal(ControlOpOutcome.Canceled, (await op).Outcome);
    }

    // ---- Unknown host -----------------------------------------------------------------

    [Fact]
    public async Task Unknown_host_returns_Unreachable_without_connecting()
    {
        int created = 0;
        var (service, manager, _) = Build(() => { created++; return new FakeGuiRpcClient(); });   // empty registry
        await using var _m = manager;

        ControlOpResult result = await service.PerformTaskOpAsync(Guid.NewGuid(), TaskOp.Suspend, "url", "task");

        Assert.Equal(ControlOpOutcome.Unreachable, result.Outcome);
        Assert.Equal(0, created);
    }

    // ---- Auth wiring: password vs empty-password path ---------------------------------

    [Fact]
    public async Task Password_host_authorizes_empty_password_host_does_not()
    {
        HostConfig withPw = Host("a", "a-host", password: "pw");
        HostConfig noPw = Host("b", "b-host", password: "");
        var fakes = new Dictionary<string, FakeGuiRpcClient>();
        Func<IGuiRpcClient> factory = () =>
        {
            var f = new FakeGuiRpcClient();
            // Tag by connect address so the test can pick the right fake afterwards.
            f.OnConnect = (h, _) => { lock (fakes) fakes[h] = f; return Task.CompletedTask; };
            return f;
        };
        var (service, manager, _) = Build(factory, withPw, noPw);
        await using var _m = manager;

        await service.PerformTaskOpAsync(withPw.Id, TaskOp.Suspend, "url", "task");
        await service.PerformTaskOpAsync(noPw.Id, TaskOp.Suspend, "url", "task");

        Assert.Contains("authorize", fakes["a-host"].Calls);
        Assert.DoesNotContain("authorize", fakes["b-host"].Calls);
    }

    // ---- Same-host serialization: non-overlap + submission order ----------------------

    [Fact]
    public async Task Two_ops_on_one_host_never_overlap()
    {
        HostConfig host = Host();
        var tracker = new Tracker();
        var latch = new Latch();
        int fakeIndex = 0;
        Func<IGuiRpcClient> factory = () =>
        {
            int index = Interlocked.Increment(ref fakeIndex);
            return new FakeGuiRpcClient
            {
                // Only the first op parks in its hook; while it is held, the second op
                // must not enter (the lane serializes it behind the first).
                OnConnect = async (_, _) =>
                {
                    tracker.Enter();
                    if (index == 1)
                        await latch.Wait;
                    tracker.Exit();
                },
            };
        };
        var (service, manager, _) = Build(factory, host);
        await using var _m = manager;

        Task<ControlOpResult> op1 = service.PerformTaskOpAsync(host.Id, TaskOp.Suspend, "url", "t1");
        Task<ControlOpResult> op2 = service.PerformTaskOpAsync(host.Id, TaskOp.Resume, "url", "t2");

        // Op1 is parked inside its hook; op2 must be queued (not yet entered).
        await Wait.UntilAsync(() => tracker.Current == 1, "op1 should be executing");
        Assert.Equal(1, tracker.Max);   // op2 has not overlapped op1
        Assert.False(op2.IsCompleted);

        latch.Release();
        await Task.WhenAll(op1, op2);
        Assert.Equal(1, tracker.Max);   // the two never overlapped across the whole run
    }

    [Fact]
    public async Task Three_ops_on_one_host_run_in_submission_order()
    {
        HostConfig host = Host();
        var order = new List<string>();
        var latch = new Latch();
        int fakeIndex = 0;
        Func<IGuiRpcClient> factory = () =>
        {
            int index = Interlocked.Increment(ref fakeIndex);
            return new FakeGuiRpcClient
            {
                OnConnect = async (_, _) => { if (index == 1) await latch.Wait; },
                OnTaskOp = (op, _, _) => { lock (order) order.Add(op.ToString()); return Task.CompletedTask; },
            };
        };
        var (service, manager, _) = Build(factory, host);
        await using var _m = manager;

        // All three submitted while op1 is gated, so they queue in submission order.
        Task<ControlOpResult> op1 = service.PerformTaskOpAsync(host.Id, TaskOp.Suspend, "url", "t");
        Task<ControlOpResult> op2 = service.PerformTaskOpAsync(host.Id, TaskOp.Resume, "url", "t");
        Task<ControlOpResult> op3 = service.PerformTaskOpAsync(host.Id, TaskOp.Abort, "url", "t");
        latch.Release();
        await Task.WhenAll(op1, op2, op3);

        Assert.Equal(["Suspend", "Resume", "Abort"], order);
    }

    [Fact]
    public async Task A_faulting_op_does_not_wedge_the_lane()
    {
        HostConfig host = Host();
        var order = new List<string>();
        int fakeIndex = 0;
        Func<IGuiRpcClient> factory = () =>
        {
            int index = Interlocked.Increment(ref fakeIndex);
            return new FakeGuiRpcClient
            {
                // The first op faults at connect; the rest run normally.
                OnConnect = (_, _) => index == 1 ? throw new BoincConnectionException("down") : Task.CompletedTask,
                OnTaskOp = (op, _, _) => { lock (order) order.Add(op.ToString()); return Task.CompletedTask; },
            };
        };
        var (service, manager, _) = Build(factory, host);
        await using var _m = manager;

        Task<ControlOpResult> op1 = service.PerformTaskOpAsync(host.Id, TaskOp.Suspend, "url", "t");
        Task<ControlOpResult> op2 = service.PerformTaskOpAsync(host.Id, TaskOp.Resume, "url", "t");
        Task<ControlOpResult> op3 = service.PerformTaskOpAsync(host.Id, TaskOp.Abort, "url", "t");
        ControlOpResult[] results = await Task.WhenAll(op1, op2, op3);

        Assert.Equal(ControlOpOutcome.Unreachable, results[0].Outcome);
        Assert.Equal(ControlOpOutcome.Succeeded, results[1].Outcome);
        Assert.Equal(ControlOpOutcome.Succeeded, results[2].Outcome);
        // Op1 never reached its RPC; op2 and op3 still ran, in order.
        Assert.Equal(["Resume", "Abort"], order);
    }

    [Fact]
    public async Task A_canceled_op_mid_chain_does_not_wedge_the_lane()
    {
        HostConfig host = Host();
        var order = new List<string>();
        var latch = new Latch();
        int fakeIndex = 0;
        Func<IGuiRpcClient> factory = () =>
        {
            int index = Interlocked.Increment(ref fakeIndex);
            return new FakeGuiRpcClient
            {
                OnConnect = (_, _) => index == 1 ? latch.Wait : Task.CompletedTask,   // op1 hangs until canceled
                OnTaskOp = (op, _, _) => { lock (order) order.Add(op.ToString()); return Task.CompletedTask; },
            };
        };
        var (service, manager, _) = Build(factory, host);
        await using var _m = manager;
        using var cts = new CancellationTokenSource();

        Task<ControlOpResult> op1 = service.PerformTaskOpAsync(host.Id, TaskOp.Suspend, "url", "t", cts.Token);
        Task<ControlOpResult> op2 = service.PerformTaskOpAsync(host.Id, TaskOp.Resume, "url", "t");
        Task<ControlOpResult> op3 = service.PerformTaskOpAsync(host.Id, TaskOp.Abort, "url", "t");
        await cts.CancelAsync();   // op1 aborts mid-connect; op2/op3 must still run in order
        ControlOpResult[] results = await Task.WhenAll(op1, op2, op3);

        Assert.Equal(ControlOpOutcome.Canceled, results[0].Outcome);
        Assert.Equal(ControlOpOutcome.Succeeded, results[1].Outcome);
        Assert.Equal(ControlOpOutcome.Succeeded, results[2].Outcome);
        Assert.Equal(["Resume", "Abort"], order);
    }

    // ---- Different-host independence --------------------------------------------------

    [Fact]
    public async Task Ops_on_two_hosts_run_concurrently()
    {
        HostConfig a = Host("a", "a-host");
        HostConfig b = Host("b", "b-host");
        var tracker = new Tracker();
        var latch = new Latch();
        Func<IGuiRpcClient> factory = () => new FakeGuiRpcClient
        {
            OnConnect = async (_, _) => { tracker.Enter(); await latch.Wait; tracker.Exit(); },
        };
        var (service, manager, _) = Build(factory, a, b);
        await using var _m = manager;

        Task<ControlOpResult> opA = service.PerformTaskOpAsync(a.Id, TaskOp.Suspend, "url", "t");
        Task<ControlOpResult> opB = service.PerformTaskOpAsync(b.Id, TaskOp.Suspend, "url", "t");

        // Both ops reach their hook at once: the lane does not serialize across hosts.
        await Wait.UntilAsync(() => tracker.Max == 2, "ops on different hosts should run concurrently");

        latch.Release();
        await Task.WhenAll(opA, opB);
    }

    // ---- RequestRefresh nudge on success ----------------------------------------------

    [Fact]
    public async Task Success_nudges_exactly_the_target_monitor()
    {
        // Live monitors so the nudge is observable as an extra poll tick. Fake time is
        // never advanced, so the only tick beyond the first can come from RequestRefresh.
        HostConfig a = Host("a", "a-host");
        HostConfig b = Host("b", "b-host");
        var registry = new HostRegistry(new LatticeConfig(60, [a, b]), TempPath());
        var monitorFakes = new List<FakeGuiRpcClient>();
        Func<IGuiRpcClient> monitorFactory = () =>
        {
            var f = new FakeGuiRpcClient();
            lock (monitorFakes) monitorFakes.Add(f);
            return f;
        };
        await using var manager = new HostMonitorManager(registry, monitorFactory, new FakeTimeProvider());
        var service = new HostControlService(registry, manager, () => new FakeGuiRpcClient());
        manager.Start();

        // Both monitors reach their first tick and park on a 60s timer that never fires.
        await Wait.UntilAsync(() => monitorFakes.Count == 2 && monitorFakes.All(f => Ticks(f) >= 1));

        FakeGuiRpcClient aFake = monitorFakes.Single(f => f.Calls.Contains("connect:a-host:31416"));
        FakeGuiRpcClient bFake = monitorFakes.Single(f => f.Calls.Contains("connect:b-host:31416"));

        await service.PerformTaskOpAsync(a.Id, TaskOp.Suspend, "url", "t");

        // The target monitor re-polls; the other stays parked.
        await Wait.UntilAsync(() => Ticks(aFake) >= 2, "the target monitor should be nudged");
        Assert.Equal(1, Ticks(bFake));

        static int Ticks(FakeGuiRpcClient f) => f.Calls.Count(c => c == "get_cc_status");
    }

    [Fact]
    public async Task Disposes_the_side_connection_on_success_and_on_failure()
    {
        HostConfig host = Host();
        var fakes = new List<FakeGuiRpcClient>();
        Func<IGuiRpcClient> factory = () =>
        {
            var f = new FakeGuiRpcClient();
            lock (fakes) fakes.Add(f);
            return f;
        };
        var (service, manager, _) = Build(factory, host);
        await using var _m = manager;

        await service.PerformTaskOpAsync(host.Id, TaskOp.Suspend, "url", "t");   // success path
        // failure path: swap in a throwing connect on a fresh fake
        var (service2, manager2, _) = Build(
            () => { var f = new FakeGuiRpcClient { OnTaskOp = (_, _, _) => throw new BoincRpcException("x") }; lock (fakes) fakes.Add(f); return f; },
            host);
        await using var _m2 = manager2;
        await service2.PerformTaskOpAsync(host.Id, TaskOp.Suspend, "url", "t");

        Assert.All(fakes, f => Assert.True(f.Disposed));
    }
}
