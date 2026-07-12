using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lattice.App.Infrastructure;
using Lattice.App.Localization;
using Lattice.App.Tests.Fakes;
using Lattice.App.ViewModels;
using Lattice.App.Views;
using Lattice.Core;
using Lattice.Tests;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using static Lattice.Tests.HeadlessLayout;

namespace Lattice.App.Tests.Headless;

public class TasksViewTests
{
    private static (Window Window, TasksView View, TasksViewModel Vm, HostRegistry Registry,
        HostMonitorManager Manager, Dictionary<string, FakeGuiRpcClient> Fakes) MakeView(UiStateStore? uiState = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}.json");
        var registry = new HostRegistry(new LatticeConfig(5, []), path);
        var fakes = new Dictionary<string, FakeGuiRpcClient>();
        // Frozen fake clock (never advanced): every manager-driven settle below is
        // reached by the immediate first poll (fires on Start before any interval
        // wait) or by an explicit RequestRefresh/F5 wake — none needs the clock to
        // tick. Freezing removes all background natural polls, so the settles are
        // deterministic with no real-time ceiling on the poll cadence.
        var manager = new HostMonitorManager(registry, () => new RoutingGuiRpcClient(fakes), new FakeTimeProvider());
        var store = new HostStore(registry, manager, new ImmediateUiDispatcher());
        var clock = new ManualUiClock();
        uiState ??= new UiStateStore(Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}-ui.json"));
        var vm = new TasksViewModel(store, clock, uiState, new DensityPreference(uiState));
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
            IsDeadlineAtRisk: true, IsSuspended: false, HostId: Guid.NewGuid(), Host: "host-a");
        var atRiskHolder = new TaskRow(atRiskRow.Key, atRiskRow);
        vm.Rows.Add(atRiskHolder);
        Layout(window);

        var row = window.GetVisualDescendants().OfType<DataGridRow>()
            .Single(r => ReferenceEquals(r.DataContext, atRiskHolder));
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
            IsDeadlineAtRisk: false, IsSuspended: true, HostId: Guid.NewGuid(), Host: "host-a");
        var suspendedHolder = new TaskRow(suspendedRow.Key, suspendedRow);
        vm.Rows.Add(suspendedHolder);
        Layout(window);

        var row = window.GetVisualDescendants().OfType<DataGridRow>()
            .Single(r => ReferenceEquals(r.DataContext, suspendedHolder));
        Assert.Contains("suspended", row.Classes);
        window.Close();
    }

    // Pins the row-class liveness fix (TasksView.axaml.cs OnLoadingRow):
    // post-retrofit, an in-place Data update does NOT re-run LoadingRow (the
    // row item's identity never changes), so classes must instead track the
    // holder's PropertyChanged. Also pins that DataGrid selection survives
    // the same in-place update, since both ride on holder identity.
    [AvaloniaFact]
    public void Row_going_at_risk_in_place_updates_the_row_class_and_keeps_selection()
    {
        var (window, view, vm, _, _, _) = MakeView();
        window.Show();

        var initial = new TaskRowViewModel(
            Project: "p", Application: "a", Name: "t", Fraction: 0.2, PercentText: "20%",
            ElapsedText: "1m 00s", RemainingText: "5m 00s", DeadlineText: "07-11 00:00",
            Deadline: DateTimeOffset.UtcNow.AddHours(1), StateKind: TaskStateKind.Running, StateText: "Running",
            IsDeadlineAtRisk: false, IsSuspended: false, HostId: Guid.NewGuid(), Host: "host-a");
        var holder = new TaskRow(initial.Key, initial);
        vm.Rows.Add(holder);
        Layout(window);

        view.Grid.SelectedItem = holder;
        var dataGridRow = window.GetVisualDescendants().OfType<DataGridRow>()
            .Single(r => ReferenceEquals(r.DataContext, holder));
        Assert.DoesNotContain("atRisk", dataGridRow.Classes);

        // Same holder, same Key — a keyed-reconcile Update op, not a
        // remove+insert: the row never leaves the grid, so LoadingRow never
        // re-fires for it.
        holder.Data = holder.Data with { IsDeadlineAtRisk = true };
        Layout(window);

        Assert.Contains("atRisk", dataGridRow.Classes);
        Assert.Same(holder, view.Grid.SelectedItem);
        window.Close();
    }

    // Teardown-drain regression (quality review, post-#24-retrofit): navigating
    // away from the Tasks page discards TasksView through the ContentControl
    // DataTemplate WITHOUT changing Grid.ItemsSource, so the DataGrid never
    // fires UnloadingRow for its realized rows — the row-class subscriptions
    // would then pin orphaned DataGridRows to the long-lived TaskRow holders,
    // growing unbounded across navigations. Detach must drain them all.
    [AvaloniaFact]
    public void Detaching_the_view_drains_all_row_class_subscriptions()
    {
        var (window, view, vm, _, _, _) = MakeView();
        window.Show();

        var row = new TaskRowViewModel(
            Project: "p", Application: "a", Name: "t", Fraction: 0.2, PercentText: "20%",
            ElapsedText: "1m 00s", RemainingText: "5m 00s", DeadlineText: "—",
            Deadline: null, StateKind: TaskStateKind.Running, StateText: "Running",
            IsDeadlineAtRisk: false, IsSuspended: false, HostId: Guid.NewGuid(), Host: "host-a");
        vm.Rows.Add(new TaskRow(row.Key, row));
        Layout(window);
        Assert.True(view.RowSubscriptionCount > 0, "a realized row should have subscribed");

        // Mimic the shell's navigation teardown: the view leaves the visual
        // tree while its ItemsSource stays untouched (no UnloadingRow fires).
        window.Content = null;
        Layout(window);

        Assert.Equal(0, view.RowSubscriptionCount);
        window.Close();
    }

    [AvaloniaFact]
    public void Density_toggle_flips_row_height_from_standard_to_compact()
    {
        var (window, view, vm, _, _, _) = MakeView();
        window.Show();
        var row1 = new TaskRowViewModel(
            Project: "p", Application: "a", Name: "t", Fraction: null, PercentText: "—",
            ElapsedText: "0s", RemainingText: "—", DeadlineText: "—",
            Deadline: null, StateKind: TaskStateKind.Waiting, StateText: "Waiting",
            IsDeadlineAtRisk: false, IsSuspended: false, HostId: Guid.NewGuid(), Host: "host-a");
        vm.Rows.Add(new TaskRow(row1.Key, row1));
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

        await HeadlessSync.WaitUntilAsync(() => !vm.IsLoading);
        Layout(window);

        Assert.True(vm.IsEmpty);
        Assert.Empty(vm.Rows);
        // The Single() call is the assertion: it throws unless exactly one visible
        // TextBlock carries the empty-overlay string.
        _ = window.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.IsVisible && t.Text == Strings.TasksEmptyAll);

        await manager.DisposeAsync();
        window.Close();
    }

    [AvaloniaFact]
    public void Loading_overlay_text_names_the_host_being_fetched()
    {
        var (window, _, vm, registry, _, fakes) = MakeView();
        AddHost(registry, fakes, "host-a", new FakeGuiRpcClient());
        window.Show();
        // Manager deliberately NOT started: the host has no snapshot yet, which is
        // exactly the loading phase the overlay text covers.
        Layout(window);

        Assert.True(vm.IsLoading);
        Assert.Equal(string.Format(Strings.LoadingFromFmt, "host-a"), vm.LoadingText);
        _ = window.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.IsVisible && t.Text == vm.LoadingText);
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

    [AvaloniaFact]
    public async Task F5_triggers_an_immediate_refresh_poll_via_the_key_binding()
    {
        // The frozen fake clock (MakeView) is what guarantees "attributable to F5
        // alone": with the clock never advanced, no natural steady-state poll can
        // ever land, so any extra get_results after the keypress comes from F5's
        // RequestRefresh wake — the earlier 3600s-interval hack is now redundant.
        var (window, view, vm, registry, manager, fakes) = MakeView();
        var fake = new FakeGuiRpcClient();
        AddHost(registry, fakes, "host-a", fake);

        window.Show();
        manager.Start();
        await HeadlessSync.WaitUntilAsync(() => !vm.IsLoading);
        Layout(window);

        var pollsBefore = fake.Calls.Count(c => c == "get_results");
        view.Grid.Focus();
        window.KeyPress(Key.F5, RawInputModifiers.None, PhysicalKey.F5, null);

        await HeadlessSync.WaitUntilAsync(() => fake.Calls.Count(c => c == "get_results") > pollsBefore);

        await manager.DisposeAsync();
        window.Close();
    }

    [AvaloniaFact]
    public void Persisted_column_preference_beats_the_breakpoint_default()
    {
        var uiPath = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}-ui.json");
        var uiState = new UiStateStore(uiPath);
        uiState.Save(UiState.Default with { ColumnVisibility = new() { ["Project"] = false } });

        var (window, _, _, _, _, _) = MakeView(uiState);
        window.Show();
        Layout(window);

        // At 1280px every breakpoint says "visible"; the persisted explicit hide
        // must win (ColumnVisibilityPolicy: non-null userPreference beats width).
        var headers = window.GetVisualDescendants().OfType<DataGridColumnHeader>()
            .Where(h => h.IsVisible)
            .Select(h => h.Content as string)
            .Where(text => text is not null)
            .ToList();
        Assert.DoesNotContain(Strings.ColProject, headers);
        Assert.Equal(8, headers.Count);
        window.Close();
        File.Delete(uiPath);
    }

    private static List<string?> VisibleHeaders(Window window) =>
        window.GetVisualDescendants().OfType<DataGridColumnHeader>()
            .Where(h => h.IsVisible)
            .Select(h => h.Content as string)
            .Where(text => text is not null)
            .ToList();

    [AvaloniaFact]
    public void Breakpoints_use_window_width_not_view_width()
    {
        // Mimic the shell layout: a 1280px window where a 260px nav pane sits
        // beside the view, leaving the view itself ~1020px. Design §Responsive
        // (2f) defines breakpoints on WINDOW width — at 1280 ALL columns show;
        // pre-fix the policy read the view's own width and auto-hid Elapsed.
        var (window, view, _, _, _, _) = MakeView();
        window.Content = null;
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("260,*") };
        var pane = new Border();
        Grid.SetColumn(pane, 0);
        Grid.SetColumn(view, 1);
        layout.Children.Add(pane);
        layout.Children.Add(view);
        window.Content = layout;
        window.Show();
        Layout(window);

        var headers = VisibleHeaders(window);
        Assert.Contains(Strings.ColElapsed, headers);
        Assert.Contains(Strings.ColApplication, headers);
        Assert.Equal(9, headers.Count);
        window.Close();
    }

    [AvaloniaFact]
    public void Auto_hidden_column_shows_unchecked_and_one_toggle_reveals_it()
    {
        var uiPath = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}-ui.json");
        var uiState = new UiStateStore(uiPath);
        var (window, view, vm, _, _, _) = MakeView(uiState);
        // 1050 sits in the design's 1000–1099 band: Elapsed auto-hides on the
        // breakpoint (no explicit preference), Application stays visible.
        window.Width = 1050;
        window.Show();
        Layout(window);

        Assert.DoesNotContain(Strings.ColElapsed, VisibleHeaders(window));
        var elapsedItem = ((MenuFlyout)view.OverflowButton.Flyout!).Items.OfType<MenuItem>()
            .Single(i => (string?)i.Tag == "Elapsed");
        // The checkbox mirrors EFFECTIVE visibility, not the raw preference:
        // an auto-hidden column reading "checked" would need TWO clicks to show
        // (the first only persists false with no visible change).
        Assert.False(elapsedItem.IsChecked);

        // One toggle, decomposed as a real click is: flip IsChecked, raise Click.
        elapsedItem.IsChecked = true;
        elapsedItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Layout(window);

        Assert.Contains(Strings.ColElapsed, VisibleHeaders(window));
        Assert.True(elapsedItem.IsChecked);
        Assert.True(vm.GetColumnPreference("Elapsed"));
        Assert.True(uiState.Load().ColumnVisibility["Elapsed"]);
        window.Close();
        File.Delete(uiPath);
    }

    [AvaloniaFact]
    public void Recreated_view_restores_the_state_filter_combo_from_the_vm()
    {
        // The shell owns ONE long-lived TasksViewModel; TasksViews are recreated
        // per navigation. A fresh view must render the VM's current filter, not
        // the XAML default "All" — otherwise the combo lies and clicking "All"
        // is a no-op (index 0 is already selected).
        var (window, _, vm, _, _, _) = MakeView();
        vm.StateFilter = TaskStateKind.Running;

        var recreated = new TasksView { DataContext = vm };
        window.Content = recreated;
        window.Show();
        Layout(window);

        Assert.Equal(1, recreated.StateFilterBox.SelectedIndex);
        // Attaching the view must not clobber the VM's filter either.
        Assert.Equal(TaskStateKind.Running, vm.StateFilter);
        window.Close();
    }

    // Bug 3 (coupled to Bug 1's resize enablement): once columns can be dragged wider
    // than the viewport, a horizontal scrollbar MUST surface so the overflow is reachable.
    // The star-sized Task column normally absorbs slack so total == viewport and no scrollbar
    // ever shows; here we reproduce the post-resize overflow by widening a FIXED column past
    // the viewport (the star column collapses to its MinWidth and the total overflows) — the
    // same end-state a user's drag produces, without touching the production column sizing.
    // Probed on the REAL Tasks grid inside its Panel+overlays wrapper, to prove that wrapper
    // does not clip or suppress the DataGrid's own horizontal ScrollBar (PART_HorizontalScrollbar).
    [AvaloniaFact]
    public void Widening_a_column_past_the_viewport_surfaces_the_horizontal_scrollbar()
    {
        var (window, view, vm, _, _, _) = MakeView();
        var row = new TaskRowViewModel(
            Project: "p", Application: "a", Name: "t", Fraction: 0.2, PercentText: "20%",
            ElapsedText: "1m 00s", RemainingText: "5m 00s", DeadlineText: "—",
            Deadline: null, StateKind: TaskStateKind.Running, StateText: "Running",
            IsDeadlineAtRisk: false, IsSuspended: false, HostId: Guid.NewGuid(), Host: "host-a");
        vm.Rows.Add(new TaskRow(row.Key, row));
        window.Show();
        Layout(window);

        var scrollbar = window.GetVisualDescendants().OfType<ScrollBar>()
            .Single(s => s.Name == "PART_HorizontalScrollbar");
        // Baseline: the star column exactly fills the viewport, so nothing overflows.
        Assert.False(scrollbar.IsVisible, "no horizontal overflow before a column is widened");

        // Widen the fixed Project column far past the 1280px viewport — the end-state of a
        // user dragging it wide. This is a runtime manipulation of the live grid, NOT an edit
        // to the view's declared column widths.
        view.Grid.Columns[0].Width = new DataGridLength(2000);
        Layout(window);

        Assert.True(scrollbar.IsVisible,
            "widening a column past the viewport must surface the DataGrid's horizontal scrollbar");
        // A genuine scroll extent: Maximum is the scrollable range (total content minus viewport),
        // which is strictly positive only when content actually overflows horizontally.
        Assert.True(scrollbar.Maximum > 0,
            $"the grid must expose a positive horizontal scroll extent, was {scrollbar.Maximum}");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Scope_switch_hiding_the_partial_bar_does_not_count_as_user_dismissal()
    {
        var (window, _, vm, registry, manager, fakes) = MakeView();
        var hostUp = AddHost(registry, fakes, "host-up", new FakeGuiRpcClient());
        AddHost(registry, fakes, "host-down",
            new FakeGuiRpcClient { OnAuthorize = _ => Task.FromResult(false) });
        window.Show();
        manager.Start();

        await HeadlessSync.WaitUntilAsync(() => vm.ShowPartialBar);
        Layout(window);

        // Scoping to the healthy host hides the bar through the IsOpen binding;
        // FAInfoBar raises Closed with Reason=Programmatic. That close is not a
        // user dismissal and must not snapshot the outage as dismissed.
        vm.Scope = new ScopeSelection(hostUp.Id);
        Layout(window);
        Assert.False(vm.ShowPartialBar);

        vm.Scope = ScopeSelection.AllHosts;
        Layout(window);

        Assert.True(vm.ShowPartialBar,
            "returning to All hosts must re-show the partial bar: the user never dismissed it");

        await manager.DisposeAsync();
        window.Close();
    }

    [AvaloniaFact]
    public void Task_column_middle_ellipsizes_and_other_text_columns_end_ellipsize()
    {
        var (window, _, vm, _, _, _) = MakeView();
        window.Show();
        var row = new TaskRowViewModel(
            Project: "einstein", Application: "O3AS", Name: "h1_1234_long_task_name_0_1",
            Fraction: 0.2, PercentText: "20%", ElapsedText: "1m 00s", RemainingText: "5m 00s",
            DeadlineText: "07-11 00:00", Deadline: DateTimeOffset.UtcNow.AddHours(1),
            StateKind: TaskStateKind.Running, StateText: "Running",
            IsDeadlineAtRisk: false, IsSuspended: false, HostId: Guid.NewGuid(), Host: "host-a");
        vm.Rows.Add(new TaskRow(row.Key, row));
        Layout(window);

        var grid = window.GetVisualDescendants().OfType<DataGrid>().Single();
        var taskTb = grid.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Text == "h1_1234_long_task_name_0_1");
        Assert.Same(TextTrimming.PrefixCharacterEllipsis, taskTb.TextTrimming);
        window.Close();
    }

    [AvaloniaFact]
    public void Tasks_default_column_widths_match_the_spec()
    {
        var (window, _, _, _, _, _) = MakeView();
        window.Show();
        Layout(window);
        var grid = window.GetVisualDescendants().OfType<DataGrid>().Single();
        double W(int i) => grid.Columns[i].Width.Value;
        Assert.Equal(108, W(0)); // Project
        Assert.Equal(118, W(1)); // Application
        Assert.True(grid.Columns[2].Width.IsStar); // Task
        Assert.Equal(112, W(3)); // Progress
        Assert.Equal(68,  W(4)); // Elapsed
        Assert.Equal(74,  W(5)); // Remaining
        Assert.Equal(100, W(6)); // Deadline
        Assert.Equal(112, W(7)); // State
        Assert.Equal(76,  W(8)); // Host
        window.Close();
    }

    [AvaloniaFact]
    public void Elapsed_and_remaining_cells_use_tabular_figures()
    {
        var (window, _, vm, _, _, _) = MakeView();
        window.Show();
        var row = new TaskRowViewModel(
            Project: "p", Application: "a", Name: "t", Fraction: 0.2, PercentText: "20%",
            ElapsedText: "1m 00s", RemainingText: "5m 00s", DeadlineText: "07-11 00:00",
            Deadline: DateTimeOffset.UtcNow.AddHours(1), StateKind: TaskStateKind.Running, StateText: "Running",
            IsDeadlineAtRisk: false, IsSuspended: false, HostId: Guid.NewGuid(), Host: "host-a");
        vm.Rows.Add(new TaskRow(row.Key, row));
        Layout(window);
        var grid = window.GetVisualDescendants().OfType<DataGrid>().Single();
        foreach (var text in new[] { "1m 00s", "5m 00s" })
        {
            var tb = grid.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == text);
            Assert.NotNull(tb.FontFeatures);
            Assert.Contains(tb.FontFeatures!, f => f.Tag == "tnum" && f.Value == 1);
        }
        window.Close();
    }
}
