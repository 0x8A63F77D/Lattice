using System.ComponentModel;
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
using Lattice.App.Aggregation;
using Lattice.App.Infrastructure;
using Lattice.App.Localization;
using Lattice.App.Tests.Fakes;
using Lattice.App.ViewModels;
using Lattice.App.Views;
using Lattice.Boinc.GuiRpc;
using Lattice.Core;
using Lattice.Tests;
using Xunit;

namespace Lattice.App.Tests.Headless;

// Composition root, dispatcher discipline and settle rules all come from the
// shared HostGraphFixture — see its class doc. This suite adds only the view
// hosting: which view it builds, and the window it renders in.
public class TasksViewTests
{
    private static (HostGraphFixture Fx, Window Window, TasksView View, TasksViewModel Vm) MakeView(
        UiStateStore? uiState = null)
    {
        var fx = new HostGraphFixture(uiState);
        var vm = new TasksViewModel(fx.Store, fx.Clock, fx.UiState, fx.Density);
        var view = new TasksView { DataContext = vm };
        // The fixture's 1280px default width matters here beyond matching
        // ShellWindow: it is wide enough that the Elapsed (<1100) and
        // Application (<1000) responsive breakpoints don't kick in and hide
        // columns out from under column-count assertions.
        var window = fx.Host(view);
        return (fx, window, view, vm);
    }

    // Store-backed view for facts whose polls mutate rows WHILE the grid renders
    // them: hosts are registered up front and the real store → VM pipeline is
    // driven by the started manager; the fixture's queue dispatcher delivers the
    // background poll results on the test (UI) thread at Layout/Settle drains.
    private static (HostGraphFixture Fx, Window Window, TasksView View, TasksViewModel Vm) MakeStoreView(
        params (string Address, FakeGuiRpcClient Fake)[] hosts)
    {
        var (fx, window, view, vm) = MakeView();
        foreach (var (address, fake) in hosts)
            fx.AddHost(address, fake);
        return (fx, window, view, vm);
    }

    [AvaloniaFact]
    public void Themed_render_shows_the_nine_column_headers()
    {
        // The ninth column is Host, shown only under genuine multi-host
        // presentation (IsAllHostsScope). Post-ScopeMachine that keys on >1
        // registered host, not on the AllHosts scope alone, so this aggregate
        // render must register two hosts to earn the Host column.
        var (fx, window, _, _) = MakeView();
        fx.AddHost("host-a", new FakeGuiRpcClient());
        fx.AddHost("host-b", new FakeGuiRpcClient());
        window.Show();
        fx.Layout();

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
        fx.Dispose();
    }

    [AvaloniaFact]
    public void Host_column_hides_when_scope_is_a_single_host()
    {
        var (fx, window, _, vm) = MakeView();
        window.Show();
        fx.Layout();

        vm.Scope = new ScopeSelection(Guid.NewGuid());
        fx.Layout();

        var headers = window.GetVisualDescendants().OfType<DataGridColumnHeader>()
            .Where(h => h.IsVisible)
            .Select(h => h.Content as string)
            .Where(text => text is not null)
            .ToList();
        Assert.DoesNotContain(Strings.ColHost, headers);
        Assert.Equal(8, headers.Count);
        fx.Dispose();
    }

    [AvaloniaFact]
    public void At_risk_row_carries_the_atRisk_class_after_LoadingRow()
    {
        var (fx, window, _, vm) = MakeView();
        window.Show();

        var atRiskRow = new TaskRowViewModel(
            Project: "p", Application: "a", Name: "at_risk_task", Fraction: 0.2, PercentText: "20%",
            ElapsedText: "1m 00s", RemainingText: "5m 00s", DeadlineText: "07-11 00:00",
            Deadline: DateTimeOffset.UtcNow.AddHours(1), StateKind: TaskStateKind.Running, StateText: "Running",
            IsDeadlineAtRisk: true, IsSuspended: false, HostId: Guid.NewGuid(), Host: "host-a");
        var atRiskHolder = new TaskRow(atRiskRow.Key, atRiskRow);
        vm.Rows.Add(atRiskHolder);
        fx.Layout();

        var row = window.GetVisualDescendants().OfType<DataGridRow>()
            .Single(r => ReferenceEquals(r.DataContext, atRiskHolder));
        Assert.Contains("atRisk", row.Classes);
        fx.Dispose();
    }

    [AvaloniaFact]
    public void Suspended_row_carries_the_suspended_class_after_LoadingRow()
    {
        var (fx, window, _, vm) = MakeView();
        window.Show();

        var suspendedRow = new TaskRowViewModel(
            Project: "p", Application: "a", Name: "susp_task", Fraction: 0.2, PercentText: "20%",
            ElapsedText: "1m 00s", RemainingText: "5m 00s", DeadlineText: "—",
            Deadline: null, StateKind: TaskStateKind.Suspended, StateText: "Suspended",
            IsDeadlineAtRisk: false, IsSuspended: true, HostId: Guid.NewGuid(), Host: "host-a");
        var suspendedHolder = new TaskRow(suspendedRow.Key, suspendedRow);
        vm.Rows.Add(suspendedHolder);
        fx.Layout();

        var row = window.GetVisualDescendants().OfType<DataGridRow>()
            .Single(r => ReferenceEquals(r.DataContext, suspendedHolder));
        Assert.Contains("suspended", row.Classes);
        fx.Dispose();
    }

    // Pins the row-class liveness fix (TasksView.axaml.cs OnLoadingRow):
    // post-retrofit, an in-place Data update does NOT re-run LoadingRow (the
    // row item's identity never changes), so classes must instead track the
    // holder's PropertyChanged. Also pins that DataGrid selection survives
    // the same in-place update, since both ride on holder identity.
    [AvaloniaFact]
    public void Row_going_at_risk_in_place_updates_the_row_class_and_keeps_selection()
    {
        var (fx, window, view, vm) = MakeView();
        window.Show();

        var initial = new TaskRowViewModel(
            Project: "p", Application: "a", Name: "t", Fraction: 0.2, PercentText: "20%",
            ElapsedText: "1m 00s", RemainingText: "5m 00s", DeadlineText: "07-11 00:00",
            Deadline: DateTimeOffset.UtcNow.AddHours(1), StateKind: TaskStateKind.Running, StateText: "Running",
            IsDeadlineAtRisk: false, IsSuspended: false, HostId: Guid.NewGuid(), Host: "host-a");
        var holder = new TaskRow(initial.Key, initial);
        vm.Rows.Add(holder);
        fx.Layout();

        view.Grid.SelectedItem = holder;
        var dataGridRow = window.GetVisualDescendants().OfType<DataGridRow>()
            .Single(r => ReferenceEquals(r.DataContext, holder));
        Assert.DoesNotContain("atRisk", dataGridRow.Classes);

        // Same holder, same Key — a keyed-reconcile Update op, not a
        // remove+insert: the row never leaves the grid, so LoadingRow never
        // re-fires for it.
        holder.Data = holder.Data with { IsDeadlineAtRisk = true };
        fx.Layout();

        Assert.Contains("atRisk", dataGridRow.Classes);
        Assert.Same(holder, view.Grid.SelectedItem);
        fx.Dispose();
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
        var (fx, window, view, vm) = MakeView();
        window.Show();

        var row = new TaskRowViewModel(
            Project: "p", Application: "a", Name: "t", Fraction: 0.2, PercentText: "20%",
            ElapsedText: "1m 00s", RemainingText: "5m 00s", DeadlineText: "—",
            Deadline: null, StateKind: TaskStateKind.Running, StateText: "Running",
            IsDeadlineAtRisk: false, IsSuspended: false, HostId: Guid.NewGuid(), Host: "host-a");
        vm.Rows.Add(new TaskRow(row.Key, row));
        fx.Layout();
        Assert.True(view.RowSubscriptionCount > 0, "a realized row should have subscribed");

        // Mimic the shell's navigation teardown: the view leaves the visual
        // tree while its ItemsSource stays untouched (no UnloadingRow fires).
        window.Content = null;
        fx.Layout();

        Assert.Equal(0, view.RowSubscriptionCount);
        fx.Dispose();
    }

    [AvaloniaFact]
    public void Density_toggle_flips_row_height_from_standard_to_compact()
    {
        var (fx, window, view, vm) = MakeView();
        window.Show();
        var row1 = new TaskRowViewModel(
            Project: "p", Application: "a", Name: "t", Fraction: null, PercentText: "—",
            ElapsedText: "0s", RemainingText: "—", DeadlineText: "—",
            Deadline: null, StateKind: TaskStateKind.Waiting, StateText: "Waiting",
            IsDeadlineAtRisk: false, IsSuspended: false, HostId: Guid.NewGuid(), Host: "host-a");
        vm.Rows.Add(new TaskRow(row1.Key, row1));
        fx.Layout();

        var row = window.GetVisualDescendants().OfType<DataGridRow>().Single();
        Assert.Equal(36, (int)row.Bounds.Height);

        vm.IsCompact = true;
        fx.Layout();

        row = window.GetVisualDescendants().OfType<DataGridRow>().Single();
        Assert.Equal(28, (int)row.Bounds.Height);
        fx.Dispose();
    }

    [AvaloniaFact]
    public async Task Empty_overlay_shows_the_all_hosts_message_for_connected_hosts_with_zero_tasks()
    {
        // Subject: the aggregate (all-hosts) empty overlay renders when every
        // connected host reports zero tasks. That message is gated on genuine
        // multi-host presentation (IsAllHostsScope), so two connected hosts are
        // required post-ScopeMachine — the single-host empty message is a
        // different string on the same panel.
        var (fx, window, _, vm) = MakeView();
        fx.AddHost("host-a", new FakeGuiRpcClient());
        fx.AddHost("host-b", new FakeGuiRpcClient());
        window.Show();
        fx.Start();

        await fx.SettleAsync(() => !vm.IsLoading);
        fx.Layout();

        Assert.True(vm.IsEmpty);
        Assert.Empty(vm.Rows);
        // The Single() call is the assertion: it throws unless exactly one visible
        // TextBlock carries the empty-overlay string.
        _ = window.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.IsVisible && t.Text == Strings.TasksEmptyAll);

        await fx.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task Empty_overlay_shows_the_single_host_message_for_one_connected_host_with_zero_tasks()
    {
        // Subject: a genuine single-host registry (one connected host, zero
        // tasks) must render the singular TasksEmpty message, not the
        // all-hosts aggregate message — post-Task-7B, IsAllHostsScope keys on
        // Scope.IsAllHosts && _store.Hosts.Count > 1, so a lone registered
        // host never earns the all-hosts presentation even while scoped to
        // AllHosts.
        var (fx, window, _, vm) = MakeView();
        fx.AddHost("host-a", new FakeGuiRpcClient());
        window.Show();
        fx.Start();

        await fx.SettleAsync(() => !vm.IsLoading);
        fx.Layout();

        Assert.True(vm.IsEmpty);
        Assert.Empty(vm.Rows);
        // The Single() call is the assertion: it throws unless exactly one visible
        // TextBlock carries the singular empty-overlay string.
        _ = window.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.IsVisible && t.Text == Strings.TasksEmpty);
        Assert.DoesNotContain(
            window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsVisible),
            t => t.Text == Strings.TasksEmptyAll);

        await fx.DisposeAsync();
    }

    [AvaloniaFact]
    public void Loading_overlay_text_names_the_host_being_fetched()
    {
        var (fx, window, _, vm) = MakeView();
        fx.AddHost("host-a", new FakeGuiRpcClient());
        window.Show();
        // Manager deliberately NOT started: the host has no snapshot yet, which is
        // exactly the loading phase the overlay text covers.
        fx.Layout();

        Assert.True(vm.IsLoading);
        Assert.Equal(string.Format(Strings.LoadingFromFmt, "host-a"), vm.LoadingText);
        _ = window.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.IsVisible && t.Text == vm.LoadingText);
        fx.Dispose();
    }

    [AvaloniaFact]
    public void Ctrl_F_focuses_the_filter_box()
    {
        var (fx, window, view, _) = MakeView();
        window.Show();
        fx.Layout();

        // Realistic pre-condition: some element inside the view already has focus
        // (here, the DataGrid) before the shortcut is pressed — KeyDown bubbles up
        // from the focused element through its ancestors, so TasksView.OnKeyDown
        // only ever sees the event if focus started at or below it.
        view.Grid.Focus();
        window.KeyPress(Key.F, RawInputModifiers.Control, PhysicalKey.F, "f");

        Assert.True(view.FilterBox.IsFocused);
        fx.Dispose();
    }

    [AvaloniaFact]
    public async Task F5_triggers_an_immediate_refresh_poll_via_the_key_binding()
    {
        // The frozen fake clock (MakeView) is what guarantees "attributable to F5
        // alone": with the clock never advanced, no natural steady-state poll can
        // ever land, so any extra get_results after the keypress comes from F5's
        // RequestRefresh wake — the earlier 3600s-interval hack is now redundant.
        var (fx, window, view, vm) = MakeView();
        var fake = new FakeGuiRpcClient();
        fx.AddHost("host-a", fake);

        window.Show();
        fx.Start();
        await fx.SettleAsync(() => !vm.IsLoading);
        fx.Layout();

        var pollsBefore = fake.Calls.Count(c => c == "get_results");
        view.Grid.Focus();
        window.KeyPress(Key.F5, RawInputModifiers.None, PhysicalKey.F5, null);

        await fx.SettleAsync(() => fake.Calls.Count(c => c == "get_results") > pollsBefore);

        await fx.DisposeAsync();
    }

    [AvaloniaFact]
    public void Persisted_column_preference_beats_the_breakpoint_default()
    {
        var uiPath = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}-ui.json");
        var uiState = new UiStateStore(uiPath);
        uiState.Save(UiState.Default with { ColumnVisibility = new() { ["Project"] = false } });

        // Two hosts so the Host column is present (multi-host presentation);
        // the subject here is that the persisted Project hide beats the
        // breakpoint default, leaving eight of the nine columns.
        var (fx, window, _, _) = MakeView(uiState);
        fx.AddHost("host-a", new FakeGuiRpcClient());
        fx.AddHost("host-b", new FakeGuiRpcClient());
        window.Show();
        fx.Layout();

        // At 1280px every breakpoint says "visible"; the persisted explicit hide
        // must win (ColumnVisibilityPolicy: non-null userPreference beats width).
        var headers = window.GetVisualDescendants().OfType<DataGridColumnHeader>()
            .Where(h => h.IsVisible)
            .Select(h => h.Content as string)
            .Where(text => text is not null)
            .ToList();
        Assert.DoesNotContain(Strings.ColProject, headers);
        Assert.Equal(8, headers.Count);
        fx.Dispose();
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
        // Two hosts so the Host column (multi-host presentation) is present and
        // the full nine-column count holds — the breakpoint check is about the
        // responsive columns (Elapsed/Application), not Host.
        var (fx, window, view, _) = MakeView();
        fx.AddHost("host-a", new FakeGuiRpcClient());
        fx.AddHost("host-b", new FakeGuiRpcClient());
        window.Content = null;
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("260,*") };
        var pane = new Border();
        Grid.SetColumn(pane, 0);
        Grid.SetColumn(view, 1);
        layout.Children.Add(pane);
        layout.Children.Add(view);
        window.Content = layout;
        window.Show();
        fx.Layout();

        var headers = VisibleHeaders(window);
        Assert.Contains(Strings.ColElapsed, headers);
        Assert.Contains(Strings.ColApplication, headers);
        Assert.Equal(9, headers.Count);
        fx.Dispose();
    }

    [AvaloniaFact]
    public void Auto_hidden_column_shows_unchecked_and_one_toggle_reveals_it()
    {
        var uiPath = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}-ui.json");
        var uiState = new UiStateStore(uiPath);
        var (fx, window, view, vm) = MakeView(uiState);
        // 1050 sits in the design's 1000–1099 band: Elapsed auto-hides on the
        // breakpoint (no explicit preference), Application stays visible.
        window.Width = 1050;
        window.Show();
        fx.Layout();

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
        fx.Layout();

        Assert.Contains(Strings.ColElapsed, VisibleHeaders(window));
        Assert.True(elapsedItem.IsChecked);
        Assert.True(vm.GetColumnPreference("Elapsed"));
        Assert.True(uiState.Load().ColumnVisibility["Elapsed"]);
        fx.Dispose();
        File.Delete(uiPath);
    }

    [AvaloniaFact]
    public void Recreated_view_restores_the_state_filter_combo_from_the_vm()
    {
        // The shell owns ONE long-lived TasksViewModel; TasksViews are recreated
        // per navigation. A fresh view must render the VM's current filter, not
        // the XAML default "All" — otherwise the combo lies and clicking "All"
        // is a no-op (index 0 is already selected).
        var (fx, window, _, vm) = MakeView();
        vm.StateFilter = TaskStateKind.Running;

        var recreated = new TasksView { DataContext = vm };
        window.Content = recreated;
        window.Show();
        fx.Layout();

        Assert.Equal(1, recreated.StateFilterBox.SelectedIndex);
        // Attaching the view must not clobber the VM's filter either.
        Assert.Equal(TaskStateKind.Running, vm.StateFilter);
        fx.Dispose();
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
        var (fx, window, view, vm) = MakeView();
        var row = new TaskRowViewModel(
            Project: "p", Application: "a", Name: "t", Fraction: 0.2, PercentText: "20%",
            ElapsedText: "1m 00s", RemainingText: "5m 00s", DeadlineText: "—",
            Deadline: null, StateKind: TaskStateKind.Running, StateText: "Running",
            IsDeadlineAtRisk: false, IsSuspended: false, HostId: Guid.NewGuid(), Host: "host-a");
        vm.Rows.Add(new TaskRow(row.Key, row));
        window.Show();
        fx.Layout();

        var scrollbar = window.GetVisualDescendants().OfType<ScrollBar>()
            .Single(s => s.Name == "PART_HorizontalScrollbar");
        // Baseline: the star column exactly fills the viewport, so nothing overflows.
        Assert.False(scrollbar.IsVisible, "no horizontal overflow before a column is widened");

        // Widen the fixed Project column far past the 1280px viewport — the end-state of a
        // user dragging it wide. This is a runtime manipulation of the live grid, NOT an edit
        // to the view's declared column widths.
        view.Grid.Columns[0].Width = new DataGridLength(2000);
        fx.Layout();

        Assert.True(scrollbar.IsVisible,
            "widening a column past the viewport must surface the DataGrid's horizontal scrollbar");
        // A genuine scroll extent: Maximum is the scrollable range (total content minus viewport),
        // which is strictly positive only when content actually overflows horizontally.
        Assert.True(scrollbar.Maximum > 0,
            $"the grid must expose a positive horizontal scroll extent, was {scrollbar.Maximum}");
        fx.Dispose();
    }

    [AvaloniaFact]
    public async Task Scope_switch_hiding_the_partial_bar_does_not_count_as_user_dismissal()
    {
        var (fx, window, _, vm) = MakeView();
        var hostUp = fx.AddHost("host-up", new FakeGuiRpcClient());
        fx.AddHost("host-down",
            new FakeGuiRpcClient { OnAuthorize = _ => Task.FromResult(false) });
        window.Show();
        fx.Start();

        await fx.SettleAsync(() => vm.ShowPartialBar);
        fx.Layout();

        // Scoping to the healthy host hides the bar through the IsOpen binding;
        // FAInfoBar raises Closed with Reason=Programmatic. That close is not a
        // user dismissal and must not snapshot the outage as dismissed.
        vm.Scope = new ScopeSelection(hostUp.Id);
        fx.Layout();
        Assert.False(vm.ShowPartialBar);

        vm.Scope = ScopeSelection.AllHosts;
        fx.Layout();

        Assert.True(vm.ShowPartialBar,
            "returning to All hosts must re-show the partial bar: the user never dismissed it");

        await fx.DisposeAsync();
    }

    [AvaloniaFact]
    public void Task_column_middle_ellipsizes_and_other_text_columns_end_ellipsize()
    {
        var (fx, window, _, vm) = MakeView();
        window.Show();
        var row = new TaskRowViewModel(
            Project: "einstein", Application: "O3AS", Name: "h1_1234_long_task_name_0_1",
            Fraction: 0.2, PercentText: "20%", ElapsedText: "1m 00s", RemainingText: "5m 00s",
            DeadlineText: "07-11 00:00", Deadline: DateTimeOffset.UtcNow.AddHours(1),
            StateKind: TaskStateKind.Running, StateText: "Running",
            IsDeadlineAtRisk: false, IsSuspended: false, HostId: Guid.NewGuid(), Host: "host-a");
        vm.Rows.Add(new TaskRow(row.Key, row));
        fx.Layout();

        var grid = window.GetVisualDescendants().OfType<DataGrid>().Single();
        var taskTb = grid.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Text == "h1_1234_long_task_name_0_1");
        Assert.Same(TextTrimming.PrefixCharacterEllipsis, taskTb.TextTrimming);
        fx.Dispose();
    }

    [AvaloniaFact]
    public void Tasks_default_column_widths_match_the_spec()
    {
        var (fx, window, _, _) = MakeView();
        window.Show();
        fx.Layout();
        var grid = window.GetVisualDescendants().OfType<DataGrid>().Single();
        double W(int i) => grid.Columns[i].Width.Value;
        Assert.Equal(108, W(0)); // Project
        Assert.Equal(118, W(1)); // Application
        Assert.False(grid.Columns[2].Width.IsStar); // Task fixed (not a star — Finding A)
        Assert.Equal(230, W(2)); // Task
        Assert.Equal(112, W(3)); // Progress
        Assert.Equal(68,  W(4)); // Elapsed
        Assert.Equal(74,  W(5)); // Remaining
        Assert.Equal(100, W(6)); // Deadline
        Assert.Equal(112, W(7)); // State
        Assert.Equal(76,  W(8)); // Host
        fx.Dispose();
    }

    [AvaloniaFact]
    public void Elapsed_and_remaining_cells_use_tabular_figures()
    {
        var (fx, window, _, vm) = MakeView();
        window.Show();
        var row = new TaskRowViewModel(
            Project: "p", Application: "a", Name: "t", Fraction: 0.2, PercentText: "20%",
            ElapsedText: "1m 00s", RemainingText: "5m 00s", DeadlineText: "07-11 00:00",
            Deadline: DateTimeOffset.UtcNow.AddHours(1), StateKind: TaskStateKind.Running, StateText: "Running",
            IsDeadlineAtRisk: false, IsSuspended: false, HostId: Guid.NewGuid(), Host: "host-a");
        vm.Rows.Add(new TaskRow(row.Key, row));
        fx.Layout();
        var grid = window.GetVisualDescendants().OfType<DataGrid>().Single();
        foreach (var text in new[] { "1m 00s", "5m 00s" })
        {
            var tb = grid.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == text);
            Assert.NotNull(tb.FontFeatures);
            Assert.Contains(tb.FontFeatures!, f => f.Tag == "tnum" && f.Value == 1);
        }
        fx.Dispose();
    }

    private static TaskRow TaskHolder(
        string name, double fraction, DateTimeOffset deadline, TaskStateKind state, double elapsedSeconds = 0)
    {
        var vm = new TaskRowViewModel(
            Project: "p", Application: "a", Name: name, Fraction: fraction, PercentText: "x",
            ElapsedText: "e", RemainingText: "r", DeadlineText: "d", Deadline: deadline,
            StateKind: state, StateText: state.ToString(), IsDeadlineAtRisk: false,
            IsSuspended: false, HostId: Guid.NewGuid(), Host: "host-a", ElapsedSeconds: elapsedSeconds);
        return new TaskRow(vm.Key, vm);
    }

    // The four template columns (Task, Progress, Deadline, State) had no Binding for the DataGrid
    // to derive a sort key from, so clicking their headers did nothing (owner report). SortMemberPath
    // now points each at its underlying comparable property. Assert they report CanUserSort and that
    // a real sort orders by the underlying VALUE (Deadline chronologically, not the display string).
    [AvaloniaFact]
    public void Task_template_columns_are_sortable_by_their_underlying_value()
    {
        var (fx, window, _, vm) = MakeView();
        window.Show();
        var t = new DateTimeOffset(2026, 7, 14, 0, 0, 0, TimeSpan.Zero);
        // Deadline and Elapsed orders differ, so each proves it sorts by its own underlying value.
        vm.Rows.Add(TaskHolder("beta",  0.5, t.AddHours(2), TaskStateKind.Waiting,   elapsedSeconds: 300));
        vm.Rows.Add(TaskHolder("alpha", 0.9, t.AddHours(3), TaskStateKind.Running,   elapsedSeconds: 60));
        vm.Rows.Add(TaskHolder("gamma", 0.1, t.AddHours(1), TaskStateKind.Suspended, elapsedSeconds: 600));
        fx.Layout();

        var grid = window.GetVisualDescendants().OfType<DataGrid>().Single();
        // Task(2) Progress(3) Elapsed(4) Remaining(5) Deadline(6) State(7) all sort.
        foreach (var i in new[] { 2, 3, 4, 5, 6, 7 })
            Assert.True(grid.Columns[i].CanUserSort, $"column {i} ({grid.Columns[i].Header}) should be sortable");

        // Sort by Deadline ascending: order by the DateTimeOffset, not the "d" text —
        // gamma(+1h), beta(+2h), alpha(+3h).
        grid.Columns[6].Sort(ListSortDirection.Ascending);
        fx.Layout();
        Assert.Equal(new[] { "gamma", "beta", "alpha" }, SortedNames(grid));

        // Sort by Elapsed ascending: order by the raw seconds (60/300/600), not the "e" text —
        // alpha(60), beta(300), gamma(600). A different order than Deadline, so this can't pass by
        // accident.
        grid.Columns[4].Sort(ListSortDirection.Ascending);
        fx.Layout();
        Assert.Equal(new[] { "alpha", "beta", "gamma" }, SortedNames(grid));
        fx.Dispose();
    }

    // Issue #86 headline (Tasks leg): a poll that reorders SURVIVING rows must not cost the
    // DataGrid selection. Display order is view-owned — the source collection never
    // removes/reinserts a surviving holder (Reconcile.alignToExisting keeps its slot; the old
    // Move→Remove+Insert replay is what cleared selection) — and the rendered order still follows
    // the new deadline order because the post-reconcile guard Refreshes the view, across whose
    // Reset selection rides holder identity (Projects U3 precedent).
    [AvaloniaFact]
    public async Task Selected_row_survives_a_poll_that_reorders_surviving_rows()
    {
        var sooner = new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero);
        var later = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        IReadOnlyList<Result> results =
        [
            TestData.MakeResult(name: "alpha", deadline: sooner),
            TestData.MakeResult(name: "beta", deadline: later),
        ];
        var fake = new FakeGuiRpcClient { OnGetResults = _ => Task.FromResult(results) };
        var (fx, window, view, vm) = MakeStoreView(("host-a", fake));
        window.Show();
        fx.Start();
        await fx.SettleAsync(() => vm.Rows.Count == 2);
        fx.Layout();
        Assert.Equal(new[] { "alpha", "beta" }, RenderedTaskNames(view));

        // Select the row the reorder MOVES (the diff extracts beta into slot 0):
        // beta is exactly the holder the old Move→Remove+Insert replay removed,
        // clearing its selection — alpha never left the collection either way.
        var beta = vm.Rows.Single(r => r.Data.Name == "beta");
        view.Grid.SelectedItem = beta;

        // The next poll swaps the two deadlines AND the list positions (daemon
        // result order is not contractual): same keys, surviving rows, new order.
        results =
        [
            TestData.MakeResult(name: "beta", deadline: sooner),
            TestData.MakeResult(name: "alpha", deadline: later),
        ];
        vm.RefreshCommand.Execute(null);
        await fx.SettleAsync(() => beta.Data.Deadline == sooner);
        fx.Layout();
        Dispatcher.UIThread.RunJobs();
        fx.Layout();

        Assert.Equal(new[] { "beta", "alpha" }, RenderedTaskNames(view));
        Assert.Same(beta, view.Grid.SelectedItem);

        await fx.DisposeAsync();
    }

    // Issue #86: the built-in header sort must COMPOSE with view-owned order. A header click
    // installs the column's FromPath description through the grid's own ProcessSort (replacing
    // the VM's default deadline order), and an in-place poll Update that violates the clicked
    // order re-sorts through the same conditional-Refresh guard — the collection view alone
    // never re-compares survivors. Selection rides holder identity across that Reset.
    [AvaloniaFact]
    public async Task Header_sort_stays_live_across_in_place_updates()
    {
        IReadOnlyList<Result> results =
        [
            TestData.MakeResult(name: "alpha", finalElapsed: 60),
            TestData.MakeResult(name: "beta", finalElapsed: 300),
        ];
        var fake = new FakeGuiRpcClient { OnGetResults = _ => Task.FromResult(results) };
        var (fx, window, view, vm) = MakeStoreView(("host-a", fake));
        window.Show();
        fx.Start();
        await fx.SettleAsync(() => vm.Rows.Count == 2);
        fx.Layout();

        // Elapsed ascending via the real column-sort path (index 4, as
        // Task_template_columns_are_sortable_by_their_underlying_value pins).
        var grid = window.GetVisualDescendants().OfType<DataGrid>().Single();
        grid.Columns[4].Sort(ListSortDirection.Ascending);
        Dispatcher.UIThread.RunJobs();
        fx.Layout();
        Assert.Equal(new[] { "alpha", "beta" }, RenderedTaskNames(view));

        var alpha = vm.Rows.Single(r => r.Data.Name == "alpha");
        view.Grid.SelectedItem = alpha;

        // The next poll inverts the elapsed order — an in-place Update, invisible
        // to the view's own event processing.
        results =
        [
            TestData.MakeResult(name: "alpha", finalElapsed: 600),
            TestData.MakeResult(name: "beta", finalElapsed: 300),
        ];
        vm.RefreshCommand.Execute(null);
        await fx.SettleAsync(() => alpha.Data.ElapsedSeconds == 600);
        fx.Layout();
        Dispatcher.UIThread.RunJobs();
        fx.Layout();

        Assert.Equal(new[] { "beta", "alpha" }, RenderedTaskNames(view));
        Assert.Same(alpha, view.Grid.SelectedItem);

        await fx.DisposeAsync();
    }

    // The Task-name text of each displayed row in on-screen Y order (a sort/reorder Reset can leave
    // a recycled ghost container behind — IsVisible=false / zero height — so filter it out).
    private static string[] RenderedTaskNames(TasksView view) =>
        view.Grid.GetVisualDescendants().OfType<DataGridRow>()
            .Where(r => r.DataContext is TaskRow && r.IsVisible && r.Bounds.Height > 0)
            .OrderBy(r => r.Bounds.Y)
            .Select(r => ((TaskRow)r.DataContext!).Data.Name)
            .ToArray();

    // Sorted VIEW order from the realized rows, robust to headless row recycling (a recycled
    // container can briefly duplicate a DataContext): group by name, take each name's min Index.
    // Avalonia sorts the collection view, not vm.Rows, so vm.Rows stays in insertion order.
    private static string[] SortedNames(DataGrid grid) =>
        grid.GetVisualDescendants().OfType<DataGridRow>()
            .Where(r => r.DataContext is TaskRow)
            .GroupBy(r => ((TaskRow)r.DataContext!).Data.Name)
            .OrderBy(g => g.Min(r => r.Index))
            .Select(g => g.Key)
            .ToArray();
}
