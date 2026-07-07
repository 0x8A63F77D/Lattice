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
