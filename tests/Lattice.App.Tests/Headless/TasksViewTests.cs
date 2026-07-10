using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lattice.App.Infrastructure;
using Lattice.App.Localization;
using Lattice.App.Tests.Fakes;
using Lattice.App.ViewModels;
using Lattice.App.Views;
using Lattice.Core;
using Lattice.Tests;
using Xunit;

namespace Lattice.App.Tests.Headless;

public class TasksViewTests
{
    private static (Window Window, TasksView View, TasksViewModel Vm, HostRegistry Registry,
        HostMonitorManager Manager, Dictionary<string, FakeGuiRpcClient> Fakes) MakeView()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}.json");
        var registry = new HostRegistry(new LatticeConfig(5, []), path);
        var fakes = new Dictionary<string, FakeGuiRpcClient>();
        var manager = new HostMonitorManager(registry, () => new RoutingGuiRpcClient(fakes), TimeProvider.System);
        var store = new HostStore(registry, manager, new ImmediateUiDispatcher());
        var clock = new ManualUiClock();
        var vm = new TasksViewModel(store, clock);
        var view = new TasksView { DataContext = vm };
        // 1280px matches ShellWindow's default width: wide enough that the Elapsed
        // (<1100) and Application (<1000) responsive breakpoints don't kick in and
        // hide columns out from under column-count assertions.
        var window = new Window { Width = 1280, Height = 800, Content = view };
        return (window, view, vm, registry, manager, fakes);
    }

    private static HostConfig AddHost(
        HostRegistry registry, Dictionary<string, FakeGuiRpcClient> fakes, string address, FakeGuiRpcClient fake)
    {
        var host = TestData.MakeHostConfig(name: address, address: address);
        fakes[address] = fake;
        registry.AddHost(host);
        return host;
    }

    // Headless Show() does not run a full layout pass (precedent: ShellWindowTests.Layout,
    // SettingsViewTests.Layout) — a measure/arrange realizes the DataGrid's row/header
    // containers so the visual tree actually has something to search.
    private static void Layout(Window window)
    {
        window.Measure(new Size(window.Width, window.Height));
        window.Arrange(new Rect(0, 0, window.Width, window.Height));
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Themed_render_shows_the_nine_column_headers()
    {
        var (window, _, _, _, _, _) = MakeView();
        window.Show();
        Layout(window);

        var expected = new[]
        {
            Strings.ColProject, Strings.ColApplication, Strings.ColTask, Strings.ColProgress,
            Strings.ColElapsed, Strings.ColRemaining, Strings.ColDeadline, Strings.ColState, Strings.ColHost,
        };
        // GetVisualDescendants returns headers regardless of IsVisible, and the
        // Fluent DataGrid theme also materializes non-data chrome (a filler header
        // for excess width, etc.) whose Content isn't one of our column strings —
        // filter to visible headers whose Content is text (precedent: the same
        // "returns elements regardless of IsVisible" caveat from StatusBarControlTests).
        var headers = window.GetVisualDescendants().OfType<DataGridColumnHeader>()
            .Where(h => h.IsVisible)
            .Select(h => h.Content as string)
            .Where(text => text is not null)
            .ToList();

        Assert.Equal(expected.Length, headers.Count);
        foreach (var header in expected)
            Assert.Contains(header, headers);
        window.Close();
    }

    [AvaloniaFact]
    public void Host_column_hides_when_scope_is_a_single_host()
    {
        var (window, _, vm, _, _, _) = MakeView();
        window.Show();
        Layout(window);

        vm.Scope = new ScopeSelection(Guid.NewGuid());
        Layout(window);

        var headers = window.GetVisualDescendants().OfType<DataGridColumnHeader>()
            .Where(h => h.IsVisible)
            .Select(h => h.Content as string)
            .Where(text => text is not null)
            .ToList();
        Assert.DoesNotContain(Strings.ColHost, headers);
        Assert.Equal(8, headers.Count);
        window.Close();
    }

    [AvaloniaFact]
    public void At_risk_row_carries_the_atRisk_class_after_LoadingRow()
    {
        var (window, _, vm, _, _, _) = MakeView();
        window.Show();

        var atRiskRow = new TaskRowViewModel(
            Project: "p", Application: "a", Name: "at_risk_task", Fraction: 0.2, PercentText: "20%",
            ElapsedText: "1m 00s", RemainingText: "5m 00s", DeadlineText: "07-11 00:00",
            Deadline: DateTimeOffset.UtcNow.AddHours(1), StateKind: TaskStateKind.Running, StateText: "Running",
            IsDeadlineAtRisk: true, IsSuspended: false, Host: "host-a");
        vm.Rows.Add(atRiskRow);
        Layout(window);

        var row = window.GetVisualDescendants().OfType<DataGridRow>()
            .Single(r => ReferenceEquals(r.DataContext, atRiskRow));
        Assert.Contains("atRisk", row.Classes);
        window.Close();
    }

    [AvaloniaFact]
    public void Suspended_row_carries_the_suspended_class_after_LoadingRow()
    {
        var (window, _, vm, _, _, _) = MakeView();
        window.Show();

        var suspendedRow = new TaskRowViewModel(
            Project: "p", Application: "a", Name: "susp_task", Fraction: 0.2, PercentText: "20%",
            ElapsedText: "1m 00s", RemainingText: "5m 00s", DeadlineText: "—",
            Deadline: null, StateKind: TaskStateKind.Suspended, StateText: "Suspended",
            IsDeadlineAtRisk: false, IsSuspended: true, Host: "host-a");
        vm.Rows.Add(suspendedRow);
        Layout(window);

        var row = window.GetVisualDescendants().OfType<DataGridRow>()
            .Single(r => ReferenceEquals(r.DataContext, suspendedRow));
        Assert.Contains("suspended", row.Classes);
        window.Close();
    }

    [AvaloniaFact]
    public void Density_toggle_flips_row_height_from_standard_to_compact()
    {
        var (window, view, vm, _, _, _) = MakeView();
        window.Show();
        vm.Rows.Add(new TaskRowViewModel(
            Project: "p", Application: "a", Name: "t", Fraction: null, PercentText: "—",
            ElapsedText: "0s", RemainingText: "—", DeadlineText: "—",
            Deadline: null, StateKind: TaskStateKind.Waiting, StateText: "Waiting",
            IsDeadlineAtRisk: false, IsSuspended: false, Host: "host-a"));
        Layout(window);

        var row = window.GetVisualDescendants().OfType<DataGridRow>().Single();
        Assert.Equal(36, (int)row.Bounds.Height);

        vm.IsCompact = true;
        Layout(window);

        row = window.GetVisualDescendants().OfType<DataGridRow>().Single();
        Assert.Equal(28, (int)row.Bounds.Height);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Empty_overlay_shows_for_a_connected_host_with_zero_tasks()
    {
        var (window, _, vm, registry, manager, fakes) = MakeView();
        AddHost(registry, fakes, "host-a", new FakeGuiRpcClient());
        window.Show();
        manager.Start();

        await Wait.UntilAsync(() => !vm.IsLoading, "the first (empty) snapshot should land");
        Layout(window);

        Assert.True(vm.IsEmpty);
        Assert.Empty(vm.Rows);
        var overlayText = window.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.IsVisible && t.Text == Strings.TasksEmptyAll);
        Assert.NotNull(overlayText);

        await manager.DisposeAsync();
        window.Close();
    }

    [AvaloniaFact]
    public void Ctrl_F_focuses_the_filter_box()
    {
        var (window, view, _, _, _, _) = MakeView();
        window.Show();
        Layout(window);

        // Realistic pre-condition: some element inside the view already has focus
        // (here, the DataGrid) before the shortcut is pressed — KeyDown bubbles up
        // from the focused element through its ancestors, so TasksView.OnKeyDown
        // only ever sees the event if focus started at or below it.
        view.Grid.Focus();
        window.KeyPress(Key.F, RawInputModifiers.Control, PhysicalKey.F, "f");

        Assert.True(view.FilterBox.IsFocused);
        window.Close();
    }
}
