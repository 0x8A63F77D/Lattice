using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Lattice.App.Localization;
using Lattice.App.ViewModels;
using Lattice.App.Views;
using Lattice.Boinc.GuiRpc;
using Lattice.Tests;
using LiveChartsCore.SkiaSharpView.Avalonia;
using Xunit;

namespace Lattice.App.Tests.Headless;

/// <summary>
/// Hosts the real StatisticsView so the LiveCharts CartesianChart, the segmented switcher and
/// the legend chips render on the Avalonia headless platform. Chart PIXELS are the snapshot
/// gate's job; this suite only proves the view wires up and the §5 overlays surface.
/// </summary>
public class StatisticsViewTests
{
    private static readonly DateTimeOffset Day0 = new(2026, 7, 14, 0, 0, 0, TimeSpan.Zero);

    private static FakeGuiRpcClient Fake(int count, int days = 9) => new()
    {
        OnGetState = () => Task.FromResult(TestData.MakeState(
            projects: [.. Enumerable.Range(0, count).Select(i =>
                new Project($"https://p{i}.org/", $"Project {i}", 0, 0, 0, i, 100, false, false))])),
        OnGetStatistics = () => Task.FromResult<IReadOnlyList<ProjectStatistics>>(
            [.. Enumerable.Range(0, count).Select(i => new ProjectStatistics($"https://p{i}.org/",
                [.. Enumerable.Range(0, days).Select(d =>
                    new DailyStatistics(Day0.AddDays(d), 1000 + d, 10 + d, 500 + d, 5 + d))]))]),
    };

    private static (HostGraphFixture Fx, Window Window, StatisticsView View, StatisticsViewModel Vm) MakeView()
    {
        var fx = new HostGraphFixture();
        var vm = new StatisticsViewModel(fx.Store, fx.Clock);
        var view = new StatisticsView { DataContext = vm };
        var window = fx.Host(view);
        return (fx, window, view, vm);
    }

    private static IEnumerable<TextBlock> Texts(StatisticsView v) =>
        v.GetVisualDescendants().OfType<TextBlock>();

    [AvaloniaFact]
    public async Task Renders_chart_and_legend_chips_when_data_arrives()
    {
        var (fx, _, view, vm) = MakeView();
        fx.AddHost("host-a", Fake(3));
        fx.Start();
        await fx.SettleAsync(() => vm.HasChart && vm.Chips.Count == 3);
        fx.Layout();

        var chart = Assert.Single(view.GetVisualDescendants().OfType<CartesianChart>());
        Assert.True(chart.IsVisible);
        // Three visible legend chips, one per project.
        var chips = view.GetVisualDescendants().OfType<ToggleButton>()
            .Where(t => t.Classes.Contains("legendChip")).ToList();
        Assert.Equal(3, chips.Count);
        Assert.All(chips, c => Assert.True(c.IsChecked));

        await fx.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task Shows_the_loading_overlay_before_the_first_snapshot()
    {
        var (fx, _, view, _) = MakeView();
        fx.AddHost("host-a", Fake(3));
        // No Start(): first fetch is still pending.
        fx.Layout();

        Assert.Contains(Texts(view), t => t.Text == Strings.StatisticsLoading && t.IsVisible);

        await fx.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task Shows_the_empty_state_when_a_connected_host_has_no_history()
    {
        var (fx, _, view, vm) = MakeView();
        // Projects but no statistics history.
        fx.AddHost("host-a", new FakeGuiRpcClient
        {
            OnGetState = () => Task.FromResult(TestData.MakeState(
                projects: [new Project("https://p.org/", "P", 0, 0, 0, 1, 100, false, false)])),
        });
        fx.Start();
        await fx.SettleAsync(() => vm.IsEmpty);
        fx.Layout();

        Assert.Contains(Texts(view), t => t.Text == Strings.StatisticsEmptyTitle && t.IsVisible);

        await fx.DisposeAsync();
    }
}
