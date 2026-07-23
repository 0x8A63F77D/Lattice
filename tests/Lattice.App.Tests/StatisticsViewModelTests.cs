using Lattice.App.Aggregation;
using Lattice.App.ViewModels;
using Lattice.Boinc.GuiRpc;
using Lattice.Core;
using Lattice.Tests;
using Xunit;

namespace Lattice.App.Tests;

/// <summary>
/// Chrome logic for the single-host Statistics page: metric switch, legend visibility, the
/// ≤6 overflow cap, empty/loading surfaces, and the all-hosts host picker. Chart-content
/// correctness is the F# policy's and the snapshot gate's job; this suite settles on the
/// VM's observable end state (never a transient), driven through the real store graph.
/// </summary>
public class StatisticsViewModelTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Day0 = new(2026, 7, 14, 0, 0, 0, TimeSpan.Zero);
    private HostGraphFixture _fx = null!;

    public ValueTask InitializeAsync()
    {
        _fx = new HostGraphFixture();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _fx.DisposeAsync();

    private static Project Proj(int i, double rac) =>
        new($"https://p{i}.org/", $"Project {i}", 0, 0, 0, rac, 100, false, false);

    private static ProjectStatistics Stats(int i, int days) =>
        new($"https://p{i}.org/",
            [.. Enumerable.Range(0, days).Select(d =>
                new DailyStatistics(Day0.AddDays(d), 1000 + d, 10 + d, 500 + d, 5 + d))]);

    /// <summary>A fake serving <paramref name="count"/> projects (RAC = ordinal) each with history.</summary>
    private static FakeGuiRpcClient Fake(int count, int days = 9) => new()
    {
        OnGetState = () => Task.FromResult(
            TestData.MakeState(projects: [.. Enumerable.Range(0, count).Select(i => Proj(i, i))])),
        OnGetStatistics = () => Task.FromResult<IReadOnlyList<ProjectStatistics>>(
            [.. Enumerable.Range(0, count).Select(i => Stats(i, days))]),
    };

    private StatisticsViewModel MakeVm() => new(_fx.Store, _fx.Clock);

    private static int SeriesCount(StatisticsViewModel vm) => vm.Series.Count();

    [Fact]
    public void No_hosts_shows_neither_chart_nor_overlay()
    {
        var vm = MakeVm();
        Assert.False(vm.HasChart);
        Assert.False(vm.IsLoading);
        Assert.False(vm.IsEmpty);
    }

    [Fact]
    public void A_pending_host_is_loading_before_its_first_snapshot()
    {
        _fx.AddHost("host-a", Fake(3));
        var vm = MakeVm();
        // No Start(): the host is Connecting with no snapshot yet — first fetch in flight.
        Assert.True(vm.IsLoading);
        Assert.False(vm.HasChart);
    }

    [Fact]
    public async Task Connected_host_with_no_history_is_empty_not_loading()
    {
        var fake = new FakeGuiRpcClient
        {
            OnGetState = () => Task.FromResult(TestData.MakeState(projects: [Proj(0, 1)])),
            // default OnGetStatistics returns []
        };
        _fx.AddHost("host-a", fake);
        var vm = MakeVm();
        _fx.Start();
        await _fx.SettleAsync(() => vm.IsEmpty);

        Assert.False(vm.HasChart);
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public async Task Default_visibility_is_top_six_by_rac_with_the_rest_in_overflow()
    {
        _fx.AddHost("host-a", Fake(8));
        var vm = MakeVm();
        _fx.Start();
        await _fx.SettleAsync(() => vm.HasChart && vm.Chips.Count == 6);

        // RAC = ordinal, so the top 6 by RAC are ordinals 2..7 (chips, ordinal-ordered),
        // and ordinals 1,0 are the overflow tail.
        Assert.Equal([2, 3, 4, 5, 6, 7], vm.Chips.Select(c => c.Ordinal));
        Assert.Equal(["https://p1.org/", "https://p0.org/"], vm.Overflow.Select(o => o.MasterUrl));
        Assert.Equal(6, SeriesCount(vm));
        Assert.True(vm.HasOverflow);
        Assert.True(vm.IsAtCap); // six shown → overflow rows disabled
        Assert.All(vm.Overflow, o => Assert.False(o.CanCheck));
    }

    [Fact]
    public async Task Unchecking_a_chip_hides_its_series_and_frees_the_cap()
    {
        _fx.AddHost("host-a", Fake(8));
        var vm = MakeVm();
        _fx.Start();
        await _fx.SettleAsync(() => vm.HasChart && vm.Chips.Count == 6);

        vm.Chips[0].IsVisible = false; // user toggle

        Assert.Equal(5, SeriesCount(vm));
        Assert.False(vm.IsAtCap);
        Assert.All(vm.Overflow, o => Assert.True(o.CanCheck)); // room again
    }

    [Fact]
    public async Task Checking_an_overflow_row_adds_its_series()
    {
        _fx.AddHost("host-a", Fake(8));
        var vm = MakeVm();
        _fx.Start();
        await _fx.SettleAsync(() => vm.HasChart && vm.Chips.Count == 6);

        vm.Chips[0].IsVisible = false; // make room
        vm.Overflow[0].IsVisible = true; // add the highest-RAC overflow project

        Assert.Equal(6, SeriesCount(vm));
        Assert.True(vm.IsAtCap);
    }

    [Fact]
    public async Task Switching_metric_rebuilds_the_chart_keeping_visibility()
    {
        _fx.AddHost("host-a", Fake(3));
        var vm = MakeVm();
        _fx.Start();
        await _fx.SettleAsync(() => vm.HasChart && vm.Chips.Count == 3);

        var before = vm.Series;
        vm.SelectedMetric = vm.MetricOptions.Single(m => m.Metric == CreditMetric.HostAverage);

        Assert.NotSame(before, vm.Series); // rebuilt
        Assert.Equal(3, SeriesCount(vm)); // same three visible series
    }

    [Fact]
    public async Task Counts_text_reports_project_and_day_totals()
    {
        _fx.AddHost("host-a", Fake(3, days: 9));
        var vm = MakeVm();
        _fx.Start();
        await _fx.SettleAsync(() => vm.HasChart);

        Assert.Contains("3 projects", vm.CountsText);
        Assert.Contains("9 days", vm.CountsText);
    }

    [Fact]
    public async Task Unreachable_host_keeps_the_chart_and_raises_the_stale_banner()
    {
        var fake = Fake(3);
        var host = _fx.AddHost("host-a", fake);
        var vm = MakeVm();
        vm.Scope = new ScopeSelection(host.Id);
        _fx.Start();
        await _fx.SettleAsync(() => vm.HasChart, "the host connects and charts its history");

        // Break the live tick (forces a teardown) AND every reconnect, so attempts climb into
        // the Unreachable tier (Retrying, attempt >= 4) while the cached snapshot — and its
        // history — persists.
        fake.OnGetCcStatus = () => throw new BoincConnectionException("poll boom");
        fake.OnConnect = (_, _) => throw new BoincConnectionException("no route");
        await _fx.AdvanceUntilAsync(
            () =>
            {
                var s = _fx.Store.Hosts.Single(h => h.Config.Id == host.Id).Status;
                return s.State == HostConnectionState.Retrying && s.Attempt >= 4;
            },
            TimeSpan.FromSeconds(30),
            "host should climb into the Unreachable tier");

        Assert.True(vm.IsStale);
        Assert.False(vm.IsEmpty);
        Assert.True(vm.HasChart, "the last-known history keeps rendering while stale");
        Assert.Contains("Host unreachable", vm.StaleText);
    }

    [Fact]
    public async Task All_hosts_scope_exposes_a_host_picker_defaulting_to_the_first_connected()
    {
        var a = _fx.AddHost("host-a", Fake(3));
        _fx.AddHost("host-b", Fake(4));
        var vm = MakeVm();
        _fx.Start();
        await _fx.SettleAsync(() => vm.IsAllHostsScope && vm.HasChart);

        Assert.Equal(2, vm.HostOptions.Count);
        Assert.NotNull(vm.SelectedHost);
        // The default charts one connected host (3 projects from host-a or 4 from host-b);
        // pin to host-a explicitly and confirm the chart follows the picker.
        vm.SelectedHost = vm.HostOptions.Single(o => o.HostId == a.Id);
        await _fx.SettleAsync(() => vm.Chips.Count == 3);
        Assert.Equal(3, SeriesCount(vm));
    }
}
