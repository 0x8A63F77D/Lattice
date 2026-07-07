using Lattice.Boinc.GuiRpc;
using Lattice.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Lattice.Tests;

public class HostMonitorStateMachineTests
{
    private static HostConfig Config(string password = "pw") =>
        new(Guid.NewGuid(), "test", "localhost", 31416, password);

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
}
