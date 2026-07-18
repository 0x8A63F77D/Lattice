using Lattice.Boinc.GuiRpc;
using Lattice.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Lattice.Tests;

public class HostMonitorManagerTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}", "config.json");

    private static HostConfig NewHost(string name = "h1") =>
        new(Guid.NewGuid(), name, "localhost", 31416, "pw");

    [Fact]
    public async Task Creates_and_starts_monitors_for_registry_hosts()
    {
        HostConfig host = NewHost();
        var registry = new HostRegistry(new LatticeConfig(5, [host]), TempPath());
        List<ConnectionStatus> statuses = [];
        await using var manager = new HostMonitorManager(registry, () => new FakeGuiRpcClient(), new FakeTimeProvider());
        manager.StatusChanged += (_, s) => { lock (statuses) statuses.Add(s); };
        manager.Start();
        await Wait.UntilAsync(() => manager.Monitors.Single().Status.State == HostConnectionState.Connected);
        lock (statuses)
            Assert.Contains(statuses, s => s.HostId == host.Id && s.State == HostConnectionState.Connected);
    }

    [Fact]
    public async Task Registry_add_and_remove_manage_monitor_lifecycle()
    {
        var registry = new HostRegistry(LatticeConfig.Default, TempPath());
        var fake = new FakeGuiRpcClient();
        await using var manager = new HostMonitorManager(registry, () => fake, new FakeTimeProvider());
        manager.Start();
        Assert.Empty(manager.Monitors);

        HostConfig host = NewHost();
        registry.AddHost(host);
        await Wait.UntilAsync(() =>
            manager.Monitors.Count == 1 && manager.Monitors[0].Status.State == HostConnectionState.Connected);

        registry.RemoveHost(host.Id);
        Assert.Empty(manager.Monitors);
        await Wait.UntilAsync(() => fake.Disposed);
    }

    [Fact]
    public async Task Registry_update_reaches_the_monitor()
    {
        HostConfig host = NewHost();
        var registry = new HostRegistry(new LatticeConfig(5, [host]), TempPath());
        List<string> connects = [];
        Func<IGuiRpcClient> factory = () => new FakeGuiRpcClient
        {
            OnConnect = (h, p) => { lock (connects) connects.Add($"{h}:{p}"); return Task.CompletedTask; },
        };
        await using var manager = new HostMonitorManager(registry, factory, new FakeTimeProvider());
        manager.Start();
        await Wait.UntilAsync(() => { lock (connects) return connects.Contains("localhost:31416"); });

        registry.UpdateHost(host with { Address = "otherhost" });
        await Wait.UntilAsync(() => { lock (connects) return connects.Contains("otherhost:31416"); });
    }

    [Fact]
    public async Task Registry_interval_change_reaches_running_monitor()
    {
        HostConfig host = NewHost();
        var registry = new HostRegistry(new LatticeConfig(60, [host]), TempPath());
        var fake = new FakeGuiRpcClient();
        var time = new FakeTimeProvider();
        await using var manager = new HostMonitorManager(registry, () => fake, time);
        manager.Start();
        await Wait.UntilAsync(() => manager.Monitors.Count == 1 && manager.Monitors[0].Snapshot is not null);

        int callsAfterFirstTick = fake.Calls.Count(c => c == "get_cc_status");
        Assert.Equal(1, callsAfterFirstTick);

        // Shrink the interval to 2s. If the change reached the monitor, advancing
        // fake time by just 2s (far short of the original 60s) triggers another tick.
        registry.SetPollingInterval(2);
        await Wait.AdvanceUntilAsync(time,
            () => fake.Calls.Count(c => c == "get_cc_status") > callsAfterFirstTick,
            TimeSpan.FromSeconds(2));
    }

    // --- Tray-residency cadence (issue #92) --------------------------------------
    //
    // Cadence is observed through fake time: after the loop parks on a WaitPollInterval
    // timer, we advance fake time in 1s steps until the next tick fires and measure the
    // elapsed fake time — a floored monitor needs ~30s, a full-speed one a couple of
    // seconds. ApplyCadence uses the QUIET (non-waking) setter, so a cadence change is
    // picked up only at the NEXT wait boundary and produces no immediate "wake tick";
    // tests therefore establish the target cadence BEFORE the loop parks on it (setting
    // visibility before Start, or seeding a host at creation) rather than draining a wake.

    private static int CcStatusCalls(FakeGuiRpcClient fake) => fake.Calls.Count(c => c == "get_cc_status");

    // Advance fake time in 1s steps until the tick count grows past baseline; returns the
    // fake time elapsed. The loop must already be parked on the interval under test.
    private static async Task<TimeSpan> AdvanceToNextTickAsync(
        FakeTimeProvider time, FakeGuiRpcClient fake, int baseline)
    {
        DateTimeOffset before = time.GetUtcNow();
        await Wait.AdvanceUntilAsync(time, () => CcStatusCalls(fake) > baseline, TimeSpan.FromSeconds(1));
        return time.GetUtcNow() - before;
    }

    [Fact]
    public async Task Hidden_window_floors_a_fast_host_to_the_30s_floor()
    {
        HostConfig host = NewHost();
        var registry = new HostRegistry(new LatticeConfig(2, [host]), TempPath());
        var fake = new FakeGuiRpcClient();
        var time = new FakeTimeProvider();
        await using var manager = new HostMonitorManager(registry, () => fake, time);
        // Hide before Start: the monitor is quiet-set to the floor and parks on 30s at its
        // first tick, so no old-interval tick contaminates the measurement.
        manager.SetWindowVisible(false);
        manager.Start();
        await Wait.UntilAsync(() => CcStatusCalls(fake) >= 1);   // first tick, parked at the floor

        // A 2s host would tick after ~2s of advancing; the floor forces ~30s.
        Assert.True(await AdvanceToNextTickAsync(time, fake, 1) >= TimeSpan.FromSeconds(30),
            "hidden window must floor the 2s host to the 30s floor");
    }

    [Fact]
    public async Task Restoring_the_window_returns_a_floored_host_to_full_speed()
    {
        HostConfig host = NewHost();
        var registry = new HostRegistry(new LatticeConfig(2, [host]), TempPath());
        var fake = new FakeGuiRpcClient();
        var time = new FakeTimeProvider();
        await using var manager = new HostMonitorManager(registry, () => fake, time);
        manager.SetWindowVisible(false);
        manager.Start();
        await Wait.UntilAsync(() => CcStatusCalls(fake) >= 1);   // parked at the 30s floor

        manager.SetWindowVisible(true);   // quiet: interval becomes 2 at the next boundary
        // The loop is still parked on the 30s wait; advance through it (the deferred-apply
        // semantics), then the SUBSEQUENT wait is the restored 2s cadence.
        await AdvanceToNextTickAsync(time, fake, 1);              // the old 30s wait fires
        int baseline = CcStatusCalls(fake);
        Assert.True(await AdvanceToNextTickAsync(time, fake, baseline) < TimeSpan.FromSeconds(30),
            "restoring the window must return the host to its configured 2s cadence");
    }

    [Fact]
    public async Task Host_added_while_hidden_starts_at_the_floor()
    {
        var registry = new HostRegistry(new LatticeConfig(2, []), TempPath());
        var fake = new FakeGuiRpcClient();
        var time = new FakeTimeProvider();
        await using var manager = new HostMonitorManager(registry, () => fake, time);
        manager.Start();
        manager.SetWindowVisible(false);

        registry.AddHost(NewHost());
        // The new monitor is seeded with the EFFECTIVE (floored) interval, so its first
        // tick happens immediately and it then parks at the 30s floor.
        await Wait.UntilAsync(() => manager.Monitors.Count == 1 && CcStatusCalls(fake) >= 1);

        Assert.True(await AdvanceToNextTickAsync(time, fake, 1) >= TimeSpan.FromSeconds(30),
            "a host added while hidden must start at the floor, not the raw 2s interval");
    }

    [Fact]
    public async Task Slow_host_is_not_sped_up_by_the_floor_while_hidden()
    {
        HostConfig host = NewHost();
        var registry = new HostRegistry(new LatticeConfig(60, [host]), TempPath());
        var fake = new FakeGuiRpcClient();
        var time = new FakeTimeProvider();
        await using var manager = new HostMonitorManager(registry, () => fake, time);
        manager.SetWindowVisible(false);   // effective stays 60 (floor never speeds up a slow host)
        manager.Start();
        await Wait.UntilAsync(() => CcStatusCalls(fake) >= 1);

        // The floor never SPEEDS UP an already-slow host: a 60s host stays 60 (not 30).
        Assert.True(await AdvanceToNextTickAsync(time, fake, 1) >= TimeSpan.FromSeconds(45),
            "the floor must not accelerate a 60s host down to 30s while hidden");
    }

    [Fact]
    public async Task Interval_change_while_hidden_stays_floored()
    {
        HostConfig host = NewHost();
        var registry = new HostRegistry(new LatticeConfig(2, [host]), TempPath());
        var fake = new FakeGuiRpcClient();
        var time = new FakeTimeProvider();
        await using var manager = new HostMonitorManager(registry, () => fake, time);
        manager.SetWindowVisible(false);
        manager.Start();
        await Wait.UntilAsync(() => CcStatusCalls(fake) >= 1);   // parked at the 30s floor

        // An IntervalChanged that lands while hidden must still route through ApplyCadence:
        // 5s is below the floor, so the effective interval stays 30, not 5. A regression to
        // the raw waking path would wake the loop (ticking early) AND drop to 5s.
        registry.SetPollingInterval(5);
        Assert.True(await AdvanceToNextTickAsync(time, fake, 1) >= TimeSpan.FromSeconds(30),
            "an interval change below the floor while hidden must stay floored at 30s");
    }

    [Fact]
    public async Task Full_speed_flag_bypasses_the_floor_while_hidden()
    {
        HostConfig host = NewHost();
        var registry = new HostRegistry(
            new LatticeConfig(2, [host]) { FullSpeedHiddenPolling = true }, TempPath());
        var fake = new FakeGuiRpcClient();
        var time = new FakeTimeProvider();
        await using var manager = new HostMonitorManager(registry, () => fake, time);
        manager.SetWindowVisible(false);   // full-speed on: hiding does NOT floor
        manager.Start();
        await Wait.UntilAsync(() => CcStatusCalls(fake) >= 1);

        // With the full-speed flag on, the 2s cadence is preserved while hidden.
        Assert.True(await AdvanceToNextTickAsync(time, fake, 1) < TimeSpan.FromSeconds(30),
            "the full-speed flag must bypass the hidden floor");
    }

    [Fact]
    public async Task RequestRefreshAll_wakes_every_monitor_without_advancing_time()
    {
        var registry = new HostRegistry(new LatticeConfig(60, [NewHost("a"), NewHost("b")]), TempPath());
        var fakes = new List<FakeGuiRpcClient>();
        Func<IGuiRpcClient> factory = () =>
        {
            var f = new FakeGuiRpcClient();
            lock (fakes) fakes.Add(f);
            return f;
        };
        var time = new FakeTimeProvider();
        await using var manager = new HostMonitorManager(registry, factory, time);
        manager.Start();
        // Both monitors reach their first tick and park on a 60s timer that never fires
        // (fake time is never advanced), so any further tick can ONLY come from the refresh.
        await Wait.UntilAsync(() => fakes.Count == 2 && fakes.All(f => CcStatusCalls(f) >= 1));
        List<int> baseline;
        lock (fakes) baseline = [.. fakes.Select(CcStatusCalls)];

        manager.RequestRefreshAll();

        await Wait.UntilAsync(() =>
        {
            lock (fakes)
                return fakes.Select(CcStatusCalls).Zip(baseline, (now, was) => now > was).All(x => x);
        });
    }

    [Fact]
    public async Task Hidden_cadence_does_not_cut_a_retrying_hosts_backoff_short()
    {
        // The P2 regression (issue #92): applying hidden cadence must NOT wake a monitor
        // parked in exponential backoff. HostMonitor.SetPollingInterval Wakes (skipping the
        // remaining backoff/poll wait); ApplyCadence therefore uses the QUIET setter so
        // hiding to the tray cannot force every unreachable host to reconnect immediately —
        // that would increase churn, the opposite of tray residency. Immediate wakeups
        // belong solely to the refresh channel (RequestRefreshAll). Same Wake/WaitAsync
        // mechanism gates the Connected poll wait, so this also pins "no immediate poll".
        HostConfig host = NewHost();
        var registry = new HostRegistry(new LatticeConfig(2, [host]), TempPath());
        var fake = new FakeGuiRpcClient { OnConnect = (_, _) => throw new BoincConnectionException("down") };
        var time = new FakeTimeProvider();
        await using var manager = new HostMonitorManager(registry, () => fake, time);
        manager.Start();
        // Climb a few backoff cycles so the parked backoff is large (>=8s), giving a wide
        // margin between "woke immediately" (~0s) and "honored the backoff".
        static int Connects(FakeGuiRpcClient f) => f.Calls.Count(c => c.StartsWith("connect:"));
        await Wait.AdvanceUntilAsync(time, () => Connects(fake) >= 5, TimeSpan.FromSeconds(1));
        await Wait.UntilAsync(() => manager.Monitors.Single().Status.State == HostConnectionState.Retrying);
        int connectsBefore = Connects(fake);

        manager.SetWindowVisible(false);   // quiet: must not interrupt the backoff wait

        DateTimeOffset before = time.GetUtcNow();
        await Wait.AdvanceUntilAsync(time, () => Connects(fake) > connectsBefore, TimeSpan.FromSeconds(1));
        Assert.True(time.GetUtcNow() - before >= TimeSpan.FromSeconds(4),
            "hiding must not wake a Retrying host's backoff into an immediate reconnect");
    }

    [Fact]
    public async Task TestConnection_reports_success_refusal_and_error()
    {
        TestConnectionResult ok = await HostMonitorManager.TestConnectionAsync(
            NewHost(), () => new FakeGuiRpcClient());
        Assert.True(ok.Success);
        Assert.Null(ok.Error);
        Assert.Equal(new VersionInfo(8, 2, 0), ok.Version);

        TestConnectionResult refused = await HostMonitorManager.TestConnectionAsync(
            NewHost(), () => new FakeGuiRpcClient { OnAuthorize = _ => Task.FromResult(false) });
        Assert.False(refused.Success);
        Assert.NotNull(refused.Error);
        Assert.Null(refused.Version);

        TestConnectionResult dead = await HostMonitorManager.TestConnectionAsync(
            NewHost(), () => new FakeGuiRpcClient { OnConnect = (_, _) => throw new BoincConnectionException("refused") });
        Assert.False(dead.Success);
        Assert.Equal("refused", dead.Error);
    }
}
