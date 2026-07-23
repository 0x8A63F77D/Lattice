using Lattice.Boinc.GuiRpc;
using Lattice.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Lattice.Tests;

/// <summary>
/// Core plumbing for per-project credit history (issue #148): fetch on connect, refresh at
/// the low <see cref="StatisticsCadencePolicy"/> cadence, expose on the snapshot, and refetch
/// from scratch on reconnect. The cadence decision itself is unit-tested in
/// <see cref="StatisticsCadencePolicyTests"/>; these pin its integration into the poll loop.
/// </summary>
public class HostMonitorStatisticsTests
{
    private static HostConfig Config() => TestData.MakeHostConfig();

    private static ProjectStatistics History(string url, double userTotal) =>
        new(url, [new DailyStatistics(
            new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero), userTotal, 10, 20, 5)]);

    private static int StatisticsCalls(FakeGuiRpcClient fake) =>
        fake.Calls.Count(c => c == "get_statistics");

    [Fact]
    public async Task First_tick_fetches_statistics_and_surfaces_them_on_the_snapshot()
    {
        var fake = new FakeGuiRpcClient
        {
            OnGetStatistics = () => Task.FromResult<IReadOnlyList<ProjectStatistics>>(
                [History("https://einsteinathome.org/", 1000)]),
        };
        await using var monitor = new HostMonitor(Config(), () => fake, new FakeTimeProvider(), 5);
        monitor.Start();
        await Wait.UntilAsync(() => monitor.Snapshot is not null);

        ProjectStatistics row = Assert.Single(monitor.Snapshot!.Statistics);
        Assert.Equal("https://einsteinathome.org/", row.MasterUrl);
        Assert.Equal(1000, Assert.Single(row.Daily).UserTotalCredit);
        Assert.Equal(1, StatisticsCalls(fake));
    }

    [Fact]
    public async Task Statistics_are_empty_by_default_and_do_not_break_the_snapshot()
    {
        // A host with no attached projects returns an empty history; the snapshot still
        // publishes and Statistics is a non-null empty list.
        var fake = new FakeGuiRpcClient();
        await using var monitor = new HostMonitor(Config(), () => fake, new FakeTimeProvider(), 5);
        monitor.Start();
        await Wait.UntilAsync(() => monitor.Snapshot is not null);

        Assert.Empty(monitor.Snapshot!.Statistics);
        Assert.Equal(1, StatisticsCalls(fake)); // still fetched once on connect
    }

    [Fact]
    public async Task Statistics_are_not_refetched_within_the_refresh_interval()
    {
        var fake = new FakeGuiRpcClient
        {
            OnGetStatistics = () => Task.FromResult<IReadOnlyList<ProjectStatistics>>(
                [History("https://einsteinathome.org/", 1000)]),
        };
        // Fixed clock: every RequestRefresh tick runs at the same instant, so the cadence
        // is never due after the first fetch however many extra ticks the refresh drives.
        await using var monitor = new HostMonitor(Config(), () => fake, new FakeTimeProvider(), 5);
        monitor.Start();
        await Wait.UntilAsync(() => monitor.Snapshot is not null);

        for (int i = 0; i < 3; i++)
        {
            int ticksBefore = fake.Calls.Count(c => c == "get_cc_status");
            monitor.RequestRefresh();
            await Wait.UntilAsync(() => fake.Calls.Count(c => c == "get_cc_status") > ticksBefore);
        }

        Assert.Equal(1, StatisticsCalls(fake)); // three extra ticks, still only the connect fetch
    }

    [Fact]
    public async Task Statistics_are_refetched_after_the_refresh_interval_elapses()
    {
        int fetches = 0;
        var fake = new FakeGuiRpcClient
        {
            OnGetStatistics = () =>
            {
                int n = Interlocked.Increment(ref fetches);
                return Task.FromResult<IReadOnlyList<ProjectStatistics>>(
                    [History("https://einsteinathome.org/", 1000 * n)]);
            },
        };
        var time = new FakeTimeProvider();
        await using var monitor = new HostMonitor(Config(), () => fake, time, 5);
        monitor.Start();
        await Wait.UntilAsync(() => monitor.Snapshot is not null);
        Assert.Equal(1, StatisticsCalls(fake));

        // Cross the refresh boundary: the next poll tick must refetch and the fresher
        // history (userTotal 2000) must surface on the snapshot.
        await Wait.AdvanceUntilAsync(time, () => StatisticsCalls(fake) >= 2,
            StatisticsCadencePolicy.RefreshInterval);
        await Wait.UntilAsync(() =>
            monitor.Snapshot!.Statistics.SingleOrDefault()?.Daily[0].UserTotalCredit == 2000);
    }

    [Fact]
    public async Task Reconnect_refetches_statistics_from_scratch()
    {
        bool fail = false;
        int fetches = 0;
        var fake = new FakeGuiRpcClient
        {
            OnGetCcStatus = () => fail
                ? Task.FromException<CcStatus>(new BoincConnectionException("poll died"))
                : Task.FromResult(FakeGuiRpcClient.DefaultStatus),
            OnGetStatistics = () =>
            {
                int n = Interlocked.Increment(ref fetches);
                return Task.FromResult<IReadOnlyList<ProjectStatistics>>(
                    [History("https://einsteinathome.org/", 1000 * n)]);
            },
        };
        var time = new FakeTimeProvider();
        await using var monitor = new HostMonitor(Config(), () => fake, time, 5);
        monitor.Start();
        await Wait.UntilAsync(() => monitor.Snapshot is not null);
        Assert.Equal(1, StatisticsCalls(fake));

        // Drop the connection well within the refresh interval: the reconnect must still
        // refetch (the per-connection last-fetch time resets), not skip on the elapsed cadence.
        fail = true;
        monitor.RequestRefresh();
        await Wait.UntilAsync(() => monitor.Status.State == HostConnectionState.Retrying);
        fail = false;
        await Wait.AdvanceUntilAsync(time,
            () => monitor.Status.State == HostConnectionState.Connected, TimeSpan.FromSeconds(1));

        await Wait.UntilAsync(() =>
            monitor.Snapshot!.Statistics.SingleOrDefault()?.Daily[0].UserTotalCredit == 2000);
        Assert.Equal(2, StatisticsCalls(fake));
    }
}
