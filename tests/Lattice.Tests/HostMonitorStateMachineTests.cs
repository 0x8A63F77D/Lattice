using Lattice.Boinc.GuiRpc;
using Lattice.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Lattice.Tests;

public class HostMonitorStateMachineTests
{
    private static HostConfig Config(string password = "pw") =>
        TestData.MakeHostConfig(password: password);

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 8)]
    [InlineData(5, 16)]
    [InlineData(6, 32)]
    [InlineData(7, 60)]
    [InlineData(8, 60)]
    public void Backoff_doubles_and_caps_at_sixty(int attempt, int expectedSeconds)
        => Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), HostMonitor.BackoffDelay(attempt));

    [Fact]
    public async Task Happy_path_reaches_connected_with_status_sequence()
    {
        var fake = new FakeGuiRpcClient();
        List<HostConnectionState> states = [];
        await using var monitor = new HostMonitor(Config(), () => fake, new FakeTimeProvider(), 5);
        monitor.StatusChanged += (_, s) => { lock (states) states.Add(s.State); };
        monitor.Start();
        await Wait.UntilAsync(() => monitor.Status.State == HostConnectionState.Connected);
        lock (states)
            Assert.Equal([HostConnectionState.Connecting, HostConnectionState.Authorizing,
                          HostConnectionState.FetchingState, HostConnectionState.Connected], states);
        Assert.Equal(new VersionInfo(8, 2, 0), monitor.Status.DaemonVersion);
        Assert.Contains("authorize", fake.Calls);
    }

    [Fact]
    public async Task Empty_password_skips_authorization()
    {
        var fake = new FakeGuiRpcClient();
        await using var monitor = new HostMonitor(Config(password: ""), () => fake, new FakeTimeProvider(), 5);
        monitor.Start();
        await Wait.UntilAsync(() => monitor.Status.State == HostConnectionState.Connected);
        Assert.DoesNotContain("authorize", fake.Calls);
    }

    [Fact]
    public async Task Connect_failure_backs_off_exponentially()
    {
        var time = new FakeTimeProvider();
        Func<IGuiRpcClient> factory = () => new FakeGuiRpcClient
        {
            OnConnect = (_, _) => throw new BoincConnectionException("refused"),
        };
        await using var monitor = new HostMonitor(Config(), factory, time, 5);
        monitor.Start();

        await Wait.UntilAsync(() => monitor.Status is { State: HostConnectionState.Retrying, Attempt: 1 });
        Assert.Equal(time.GetUtcNow() + TimeSpan.FromSeconds(1), monitor.Status.NextAttemptAt);
        Assert.Equal("refused", monitor.Status.LastError);

        await Wait.AdvanceUntilAsync(time,
            () => monitor.Status is { State: HostConnectionState.Retrying, Attempt: 2 },
            TimeSpan.FromSeconds(1));
        Assert.Equal(time.GetUtcNow() + TimeSpan.FromSeconds(2), monitor.Status.NextAttemptAt);
    }

    [Fact]
    public async Task Attempt_counter_resets_after_success()
    {
        bool fail = true;
        var fake = new FakeGuiRpcClient();
        fake.OnConnect = (_, _) => fail ? throw new BoincConnectionException("down") : Task.CompletedTask;
        var time = new FakeTimeProvider();
        await using var monitor = new HostMonitor(Config(), () => fake, time, 5);
        monitor.Start();
        await Wait.UntilAsync(() => monitor.Status is { State: HostConnectionState.Retrying, Attempt: 1 });
        await Wait.AdvanceUntilAsync(time,
            () => monitor.Status is { State: HostConnectionState.Retrying, Attempt: 2 },
            TimeSpan.FromSeconds(1));
        fail = false;
        monitor.RequestRefresh();
        await Wait.UntilAsync(() => monitor.Status.State == HostConnectionState.Connected);
        Assert.Equal(0, monitor.Status.Attempt);
    }

    [Fact]
    public async Task Attempt_counter_restarts_at_one_after_a_poll_failure_following_reconnect()
    {
        // Regression test for the 97dfb74 restructure: `attempt = 0` moved inside
        // RunAttemptAsync, where it only mutated that method's local parameter copy
        // instead of the dispatcher's loop-local counter in RunAsync. Consequence:
        // fail N connection attempts, connect successfully, then have a LATER poll
        // tick fail — the dispatcher resumed backoff from attempt N+1 (a much larger
        // delay) instead of restarting fresh at attempt 1 with a 1s backoff, exactly
        // as a failure right after Connected should behave.
        bool failConnect = true;
        bool failPoll = false;
        var fake = new FakeGuiRpcClient
        {
            OnConnect = (_, _) => failConnect
                ? throw new BoincConnectionException("down")
                : Task.CompletedTask,
            OnGetCcStatus = () => failPoll
                ? throw new BoincConnectionException("poll boom")
                : Task.FromResult(FakeGuiRpcClient.DefaultStatus),
        };
        var time = new FakeTimeProvider();
        await using var monitor = new HostMonitor(Config(), () => fake, time, 5);
        monitor.Start();

        // Two failed connection attempts: reach Retrying at attempt 2.
        await Wait.UntilAsync(() => monitor.Status is { State: HostConnectionState.Retrying, Attempt: 1 });
        await Wait.AdvanceUntilAsync(time,
            () => monitor.Status is { State: HostConnectionState.Retrying, Attempt: 2 },
            TimeSpan.FromSeconds(1));

        // Let the connection succeed.
        failConnect = false;
        monitor.RequestRefresh();
        await Wait.UntilAsync(() => monitor.Status.State == HostConnectionState.Connected);

        // A later poll tick fails: the counter must restart at attempt 1 (1s
        // backoff), NOT resume at attempt 3 (4s), as if the pre-connect failures
        // still counted against this brand-new failure.
        failPoll = true;
        monitor.RequestRefresh();
        await Wait.UntilAsync(() => monitor.Status is { State: HostConnectionState.Retrying, Attempt: 1 });
        Assert.Equal(time.GetUtcNow() + TimeSpan.FromSeconds(1), monitor.Status.NextAttemptAt);
    }

    [Fact]
    public async Task RequestRefresh_skips_remaining_backoff()
    {
        bool fail = true;
        var fake = new FakeGuiRpcClient();
        fake.OnConnect = (_, _) => fail ? throw new BoincConnectionException("down") : Task.CompletedTask;
        await using var monitor = new HostMonitor(Config(), () => fake, new FakeTimeProvider(), 5);
        monitor.Start();
        await Wait.UntilAsync(() => monitor.Status.State == HostConnectionState.Retrying);
        fail = false;
        monitor.RequestRefresh();   // no fake-time advance: the wake alone must retry
        await Wait.UntilAsync(() => monitor.Status.State == HostConnectionState.Connected);
    }

    [Fact]
    public async Task Wrong_password_is_terminal_until_config_update()
    {
        var fake = new FakeGuiRpcClient { OnAuthorize = _ => Task.FromResult(false) };
        var time = new FakeTimeProvider();
        HostConfig config = Config();
        await using var monitor = new HostMonitor(config, () => fake, time, 5);
        monitor.Start();
        await Wait.UntilAsync(() => monitor.Status.State == HostConnectionState.AuthFailed);

        // Neither time nor RequestRefresh may revive it.
        time.Advance(TimeSpan.FromMinutes(5));
        monitor.RequestRefresh();
        await Task.Delay(100);
        Assert.Equal(HostConnectionState.AuthFailed, monitor.Status.State);

        fake.OnAuthorize = _ => Task.FromResult(true);
        monitor.UpdateConfig(config with { Password = "right" });
        await Wait.UntilAsync(() => monitor.Status.State == HostConnectionState.Connected);
    }

    [Fact]
    public async Task AuthFailed_disposes_client_while_parked()
    {
        var fake = new FakeGuiRpcClient { OnAuthorize = _ => Task.FromResult(false) };
        var time = new FakeTimeProvider();
        HostConfig config = Config();
        await using var monitor = new HostMonitor(config, () => fake, time, 5);
        monitor.Start();
        await Wait.UntilAsync(() => monitor.Status.State == HostConnectionState.AuthFailed);

        // The client must be torn down as soon as the loop parks in AuthFailed, not
        // held open for the entire parked duration: BOINC daemons allow very few
        // concurrent GUI RPC connections, so a lingering one can lock the user's
        // official Manager out.
        Assert.True(fake.Disposed);

        // Revival must still work: a config change un-parks the loop and reconnects.
        fake.OnAuthorize = _ => Task.FromResult(true);
        monitor.UpdateConfig(config with { Password = "right" });
        await Wait.UntilAsync(() => monitor.Status.State == HostConnectionState.Connected);
    }

    [Fact]
    public async Task Unauthorized_thrown_during_authorize_is_auth_failed()
    {
        var fake = new FakeGuiRpcClient { OnAuthorize = _ => throw new BoincUnauthorizedException() };
        await using var monitor = new HostMonitor(Config(), () => fake, new FakeTimeProvider(), 5);
        monitor.Start();
        await Wait.UntilAsync(() => monitor.Status.State == HostConnectionState.AuthFailed);
    }

    [Fact]
    public async Task Unauthorized_thrown_during_exchange_versions_is_auth_failed()
    {
        var fake = new FakeGuiRpcClient
        {
            OnExchangeVersions = () => throw new BoincUnauthorizedException(),
        };
        await using var monitor = new HostMonitor(Config(), () => fake, new FakeTimeProvider(), 5);
        monitor.Start();
        await Wait.UntilAsync(() => monitor.Status.State == HostConnectionState.AuthFailed);
    }

    [Fact]
    public async Task Unauthorized_thrown_during_get_state_is_auth_failed()
    {
        var fake = new FakeGuiRpcClient
        {
            OnGetState = () => throw new BoincUnauthorizedException(),
        };
        await using var monitor = new HostMonitor(Config(), () => fake, new FakeTimeProvider(), 5);
        monitor.Start();
        await Wait.UntilAsync(() => monitor.Status.State == HostConnectionState.AuthFailed);
    }

    [Fact]
    public async Task UpdateConfig_reconnects_with_new_address()
    {
        List<string> connects = [];
        Func<IGuiRpcClient> factory = () => new FakeGuiRpcClient
        {
            OnConnect = (host, port) => { lock (connects) connects.Add($"{host}:{port}"); return Task.CompletedTask; },
        };
        HostConfig config = Config();
        await using var monitor = new HostMonitor(config, factory, new FakeTimeProvider(), 5);
        monitor.Start();
        await Wait.UntilAsync(() => monitor.Status.State == HostConnectionState.Connected);
        monitor.UpdateConfig(config with { Address = "otherhost" });
        await Wait.UntilAsync(() => { lock (connects) return connects.Contains("otherhost:31416"); });
    }

    [Fact]
    public async Task Dispose_failure_in_client_does_not_stall_the_loop()
    {
        var fake = new FakeGuiRpcClient
        {
            OnConnect = (_, _) => throw new BoincConnectionException("refused"),
            OnDispose = () => throw new InvalidOperationException("dispose boom"),
        };
        var time = new FakeTimeProvider();
        await using var monitor = new HostMonitor(Config(), () => fake, time, 5);
        monitor.Start();

        await Wait.UntilAsync(() => monitor.Status is { State: HostConnectionState.Retrying, Attempt: 1 });
        await Wait.AdvanceUntilAsync(time,
            () => monitor.Status is { State: HostConnectionState.Retrying, Attempt: 2 },
            TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Throwing_factory_backs_off_instead_of_faulting_the_loop()
    {
        // Pre-fix, IGuiRpcClient client = _clientFactory() sat outside the try/catch:
        // a throwing factory propagated out of RunAsync and faulted the loop task
        // silently, leaving Status stuck at Connecting forever (this test times out
        // on Wait.UntilAsync pre-fix instead of observing Retrying).
        int calls = 0;
        var fake = new FakeGuiRpcClient();
        Func<IGuiRpcClient> factory = () =>
        {
            calls++;
            return calls == 1 ? throw new InvalidOperationException("factory boom") : fake;
        };
        var time = new FakeTimeProvider();
        await using var monitor = new HostMonitor(Config(), factory, time, 5);
        monitor.Start();

        await Wait.UntilAsync(() => monitor.Status is { State: HostConnectionState.Retrying, Attempt: 1 });
        Assert.Equal("factory boom", monitor.Status.LastError);

        await Wait.AdvanceUntilAsync(time,
            () => monitor.Status.State == HostConnectionState.Connected,
            TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Throwing_status_subscriber_does_not_fault_the_loop()
    {
        // Pre-fix, SetStatus invoked subscribers unguarded — including from INSIDE
        // the catch blocks. A throwing subscriber during the Retrying/AuthFailed
        // publish escaped RunAsync entirely and faulted the loop task: the actor
        // died silently and no config update could ever revive it (pre-fix, this
        // test times out waiting for AuthFailed — the loop dies publishing Retrying).
        var fake = new FakeGuiRpcClient { OnAuthorize = _ => Task.FromResult(false) };
        HostConfig config = Config();
        await using var monitor = new HostMonitor(config, () => fake, new FakeTimeProvider(), 5);
        monitor.StatusChanged += (_, _) => throw new InvalidOperationException("subscriber boom");
        monitor.Start();

        // Assert via polling Status only — never via the (throwing) subscriber.
        await Wait.UntilAsync(() => monitor.Status.State == HostConnectionState.AuthFailed);

        fake.OnAuthorize = _ => Task.FromResult(true);
        monitor.UpdateConfig(config with { Password = "right" });
        await Wait.UntilAsync(() => monitor.Status.State == HostConnectionState.Connected);
    }

    [Fact]
    public async Task Throwing_snapshot_subscriber_does_not_tear_down_a_healthy_connection()
    {
        // Pre-fix, a throwing SnapshotUpdated subscriber propagated out of TickAsync
        // into RunAsync's generic catch and needlessly tore down a HEALTHY connection
        // into Retrying/backoff even though every RPC had succeeded.
        var fake = new FakeGuiRpcClient();
        List<HostConnectionState> states = [];
        await using var monitor = new HostMonitor(Config(), () => fake, new FakeTimeProvider(), 5);
        monitor.StatusChanged += (_, s) => { lock (states) states.Add(s.State); };
        monitor.SnapshotUpdated += (_, _) => throw new InvalidOperationException("subscriber boom");
        monitor.Start();
        await Wait.UntilAsync(() => monitor.Snapshot is not null);

        // The loop must have survived the throw: a refresh still produces a second tick.
        monitor.RequestRefresh();
        await Wait.UntilAsync(() => fake.Calls.Count(c => c == "get_cc_status") >= 2);

        Assert.Equal(HostConnectionState.Connected, monitor.Status.State);
        lock (states)
            Assert.DoesNotContain(HostConnectionState.Retrying, states);
    }

    [Fact]
    public async Task Dispose_stops_loop_and_disposes_client()
    {
        var fake = new FakeGuiRpcClient();
        var monitor = new HostMonitor(Config(), () => fake, new FakeTimeProvider(), 5);
        monitor.Start();
        await Wait.UntilAsync(() => monitor.Status.State == HostConnectionState.Connected);
        await monitor.DisposeAsync();
        Assert.Equal(HostConnectionState.Disconnected, monitor.Status.State);
        Assert.True(fake.Disposed);
    }

    [Fact]
    public async Task UpdateConfig_aborts_inflight_connect_to_old_address()
    {
        // The old address's ConnectAsync never completes on its own — only a
        // cancellation of the connection's token (triggered by UpdateConfig) can
        // unstick it. Pre-fix, UpdateConfig only sets a flag/wake and the loop stays
        // blocked on this connect forever, so the test times out via Wait.UntilAsync.
        var oldConnectStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldConnectGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        List<string> connects = [];
        Func<IGuiRpcClient> factory = () => new FakeGuiRpcClient
        {
            OnConnect = (host, port) =>
            {
                lock (connects) connects.Add($"{host}:{port}");
                if (host == "localhost")
                {
                    oldConnectStarted.TrySetResult();
                    return oldConnectGate.Task;
                }
                return Task.CompletedTask;
            },
        };
        HostConfig config = Config();
        await using var monitor = new HostMonitor(config, factory, new FakeTimeProvider(), 5);
        monitor.Start();
        await oldConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        monitor.UpdateConfig(config with { Address = "newhost" });

        await Wait.UntilAsync(() => monitor.Status.State == HostConnectionState.Connected);
        lock (connects)
            Assert.Contains("newhost:31416", connects);
        Assert.False(oldConnectGate.Task.IsCompleted);
    }

    [Fact]
    public async Task Config_change_during_initial_fetch_suppresses_stale_snapshot()
    {
        // The OLD connection's get_state is gated on a TCS we control; the config
        // change happens while that RPC is still in flight. Releasing the gate AFTER
        // the config change proves that even a late-completing old-config RPC cannot
        // surface as a Connected status or a published snapshot for this HostId.
        var oldGetStateGate = new TaskCompletionSource<CcState>(TaskCreationOptions.RunContinuationsAsynchronously);
        // A single ordered timeline lets us assert the first snapshot happens AFTER
        // the connect to the new address, without relying on exactly how many ticks
        // the (pre-existing, by-design) sticky-wake mechanism fires in a row.
        List<string> timeline = [];
        List<HostConnectionState> statuses = [];
        int clientIndex = 0;
        Func<IGuiRpcClient> factory = () =>
        {
            int idx = Interlocked.Increment(ref clientIndex);
            return new FakeGuiRpcClient
            {
                OnConnect = (host, port) => { lock (timeline) timeline.Add($"connect:{idx}:{host}:{port}"); return Task.CompletedTask; },
                OnGetState = () => idx == 1
                    ? oldGetStateGate.Task
                    : Task.FromResult(FakeGuiRpcClient.EmptyState),
            };
        };
        HostConfig config = Config();
        await using var monitor = new HostMonitor(config, factory, new FakeTimeProvider(), 5);
        monitor.StatusChanged += (_, s) => { lock (statuses) statuses.Add(s.State); };
        monitor.SnapshotUpdated += (_, _) => { lock (timeline) timeline.Add("snapshot"); };
        monitor.Start();
        await Wait.UntilAsync(() => monitor.Status.State == HostConnectionState.FetchingState);

        monitor.UpdateConfig(config with { Address = "newhost" });
        oldGetStateGate.TrySetResult(FakeGuiRpcClient.EmptyState);

        await Wait.UntilAsync(() => monitor.Status.State == HostConnectionState.Connected);
        await Wait.UntilAsync(() => monitor.Snapshot is not null);

        lock (timeline)
        {
            Assert.Contains("connect:1:localhost:31416", timeline);
            int newConnectIndex = timeline.IndexOf("connect:2:newhost:31416");
            int firstSnapshotIndex = timeline.IndexOf("snapshot");
            Assert.True(newConnectIndex >= 0, "the new address must have been connected to");
            Assert.True(firstSnapshotIndex >= 0, "a snapshot must have been published");
            Assert.True(newConnectIndex < firstSnapshotIndex,
                "the first snapshot must be published after the connect to the new address");
        }
        // Only the new connection ever reaches Connected: the old one's get_state was
        // canceled before it could set the daemon version / advance the state machine.
        lock (statuses)
            Assert.Equal(1, statuses.Count(s => s == HostConnectionState.Connected));
    }
}
