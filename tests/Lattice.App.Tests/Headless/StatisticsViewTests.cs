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
    public async Task Host_picker_opens_on_press_so_the_loading_ring_cannot_starve_it()
    {
        // Issue #95: the FAProgressRing shown while a host is still loading starves pointer input,
        // so a release-open picker reads as stuck. The escape hatch to a working host must carry
        // ComboBoxPressOpenBehavior like the Tasks view's combo.
        var (fx, _, view, vm) = MakeView();
        fx.AddHost("host-a", Fake(3));
        fx.AddHost("host-b", Fake(3));
        fx.Start();
        await fx.SettleAsync(() => vm.IsAllHostsScope);
        fx.Layout();

        var combo = Assert.Single(view.GetVisualDescendants().OfType<ComboBox>());
        Assert.True(ComboBoxPressOpenBehavior.GetOpenOnPress(combo));

        await fx.DisposeAsync();
    }

    // ---- #175: the picker must never keep a selection the CONTROL has silently dropped -----
    //
    // Both cases below rebuild HostOptions with value-equal-but-new StatisticsHostOption records,
    // which is the trap: a value-equality guard on the selection repair short-circuits, no
    // PropertyChanged fires, and the ComboBox — which tracks INSTANCES and coerced its own
    // SelectedItem to null while the list was empty/replaced — is never told. Both therefore
    // assert on `combo.SelectedItem`; a ViewModel-only assertion cannot see this bug at all
    // (vm.SelectedHostId is correct throughout).

    [AvaloniaFact]
    public async Task Host_picker_defaults_when_the_session_started_in_a_single_host_scope()
    {
        // Trigger 1 (owner report, #175): the shell restores a persisted SINGLE-host scope in its
        // constructor — the ViewModel is pushed out of "All hosts" BEFORE the view ever binds, so
        // HostOptions is emptied while the selection is still set — and the user then
        // switches the rail back to All hosts on the Statistics page.
        var fx = new HostGraphFixture();
        var vm = new StatisticsViewModel(fx.Store, fx.Clock);
        var a = fx.AddHost("host-a", Fake(3));
        fx.AddHost("host-b", Fake(3));
        vm.Scope = new ScopeSelection(a.Id); // shell's restore, pre-bind
        var view = new StatisticsView { DataContext = vm };
        fx.Host(view);
        fx.Start();
        await fx.SettleAsync(() => vm.HasChart);
        fx.Layout();

        vm.Scope = ScopeSelection.AllHosts; // user clicks "All hosts" in the rail
        await fx.SettleAsync(() => vm.IsAllHostsScope);
        fx.Layout();

        var combo = Assert.Single(view.GetVisualDescendants().OfType<ComboBox>());
        Assert.NotNull(vm.SelectedHostId);
        Assert.NotNull(combo.SelectedItem);
        Assert.Equal(vm.SelectedHostId, ((StatisticsHostOption)combo.SelectedItem).HostId);

        await fx.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task Host_picker_keeps_its_selection_when_a_host_joins_the_fleet()
    {
        // Trigger 2 (same class, #175): still in All hosts scope, a third host arrives. The option
        // list is rebuilt from scratch, so the control's SelectedItem instance is no longer in
        // Items — and the user's explicit pick must survive both in the ViewModel and on screen.
        var (fx, _, view, vm) = MakeView();
        var a = fx.AddHost("host-a", Fake(3));
        fx.AddHost("host-b", Fake(4));
        fx.Start();
        await fx.SettleAsync(() => vm.IsAllHostsScope && vm.HasChart);
        fx.Layout();

        vm.SelectedHostId = a.Id;
        await fx.SettleAsync(() => vm.Chips.Count == 3);
        fx.Layout();

        fx.AddHost("host-c", Fake(5)); // rebuilds HostOptions with new instances
        await fx.SettleAsync(() => vm.HostOptions.Count == 3);
        fx.Layout();

        var combo = Assert.Single(view.GetVisualDescendants().OfType<ComboBox>());
        Assert.Equal(a.Id, vm.SelectedHostId); // the pick is not reset by the fleet change
        Assert.NotNull(combo.SelectedItem);
        Assert.Equal(a.Id, ((StatisticsHostOption)combo.SelectedItem).HostId);

        await fx.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task Loading_progress_bar_animates_only_while_loading()
    {
        // The loading indicator is a stock indeterminate ProgressBar, not an FAProgressRing:
        // the ring starves app-wide pointer input while it spins (issue #95), and lowering
        // DispatcherOptions.InputStarvationTimeout was MEASURED not to help (PR #167). The
        // §95 leak-gate discipline still applies to the new control: IsIndeterminate must
        // track IsLoading so the animation runs only during a first fetch. A dropped binding
        // fails the during-loading assertion (IsIndeterminate defaults false) or the leak gate.
        var (fx, _, view, vm) = MakeView();
        fx.AddHost("host-a", Fake(3));
        fx.Layout();

        Assert.True(vm.IsLoading);
        var bar = view.GetVisualDescendants().OfType<ProgressBar>().Single();
        Assert.True(bar.IsIndeterminate); // animating during the first fetch

        fx.Start();
        // Settle on the BAR's end state, not the VM flag (the flag flips first; the binding
        // target is the behaviour under guard — same fixture-determinism contract as the rings).
        await fx.SettleAsync(() => !bar.IsIndeterminate);
        Assert.False(vm.IsLoading); // leak gate: loading ended and the animation stopped

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
