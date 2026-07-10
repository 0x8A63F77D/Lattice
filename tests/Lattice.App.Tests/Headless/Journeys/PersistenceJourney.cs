using Avalonia.Headless.XUnit;
using Lattice.App.ViewModels;
using Lattice.Core;
using Lattice.Tests;
using Xunit;

namespace Lattice.App.Tests.Headless.Journeys;

/// <summary>
/// End-to-end: a host (and a non-default polling interval) added in one harness
/// survives to a SECOND harness built fresh on the same config.json — the classic
/// "restart the app" scenario — and the reopened host actually reconnects.
/// </summary>
public class PersistenceJourney
{
    [AvaloniaFact]
    public async Task A_host_and_the_polling_interval_survive_a_restart()
    {
        const string address = "persistence-journey";
        string configPath;
        Guid hostId;

        await using (var first = new JourneyHarness())
        {
            configPath = first.ConfigPath;
            HostConfig host = first.AddHost(address, new FakeGuiRpcClient());
            hostId = host.Id;
            first.Registry.SetPollingInterval(30);
            first.Start();
            first.Window.Show();
            first.Layout();

            await first.SettleAsync(
                () => first.Shell.RailEntries.OfType<HostRailItemViewModel>()
                    .Single(h => h.HostId == hostId).State == RailState.Connected,
                "first harness should connect before it is torn down");

            // The `await using` block's Dispose runs here: Shell.Dispose /
            // Store.Dispose / Manager.DisposeAsync — the same shape as App's
            // desktop.Exit handler ("process restart").
        }

        // A second harness built on the SAME on-disk config: the host and the
        // polling interval must both already be present before Start() —
        // LoadRegistryWithFallback re-reads config.json from scratch.
        await using var second = new JourneyHarness(configPath);

        Assert.Equal(30, second.Registry.PollingIntervalSeconds);
        HostConfig reloadedHost = Assert.Single(second.Registry.Hosts);
        Assert.Equal(hostId, reloadedHost.Id);
        Assert.Equal(address, reloadedHost.Address);

        var railItem = Assert.Single(second.Shell.RailEntries.OfType<HostRailItemViewModel>());
        Assert.Equal(hostId, railItem.HostId);

        // Register a fresh fake for the reopened host and confirm it actually
        // reconnects on startup (not just that the config round-tripped).
        second.RegisterFake(address, new FakeGuiRpcClient());
        second.Start();
        second.Window.Show();
        second.Layout();

        await second.SettleAsync(
            () => railItem.State == RailState.Connected,
            "the reopened host should reconnect after the simulated restart");
    }
}
