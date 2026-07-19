using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using Lattice.App.Infrastructure;
using Lattice.App.Localization;
using Lattice.App.ViewModels;

namespace Lattice.App.Views;

public partial class TasksView : UserControl
{
    // Null = no explicit choice yet (ColumnVisibilityPolicy's breakpoint rule
    // decides); set once the user toggles a column from the overflow menu, and
    // from then on that column's visibility no longer follows the breakpoints
    // (design §Task 11 code-behind responsibilities). Populated from the VM's
    // persisted UiState when the DataContext attaches; every overflow toggle
    // writes back through TasksViewModel.SetColumnPreference.
    private readonly Dictionary<string, bool?> _userColumnPreferences = new()
    {
        ["Project"] = null,
        ["Application"] = null,
        ["Progress"] = null,
        ["Elapsed"] = null,
        ["Remaining"] = null,
        ["Deadline"] = null,
        ["State"] = null,
    };

    // DataGridColumn is a plain AvaloniaObject, not a Control/Visual: it has no
    // Name property, so x:Name is rejected on it at compile time (AVLN2000) and
    // the source generator has no field to emit either way. Columns are instead
    // looked up by their (localized, but fixed at process start) Header text —
    // both here and from the overflow menu's Click handler below — and cached
    // once per column that code-behind actually touches.
    // INVARIANT (load-bearing, unchecked at compile time): each XAML column's
    // Header="{x:Static loc:Strings.ColX}" and the constructor's
    // ColumnWithHeader(Strings.ColX) must reference the IDENTICAL Strings
    // symbol. If they drift, Single() throws InvalidOperationException the
    // first time a TasksView is constructed — at runtime, not build time
    // (though any headless TasksView test trips it immediately).
    private readonly Dictionary<string, DataGridColumn> _columnsByTag;

    // The hosting window, resolved on visual-tree attach. Design §Responsive
    // (2f) is authoritative: breakpoints are WINDOW-width thresholds ("≥1280
    // full rail + all columns"), not view-width — inside the shell the view is
    // window-minus-nav-pane, which would fire the 1100/1000 thresholds ~260px
    // early. Subscription is paired: attach subscribes, detach unsubscribes.
    private TopLevel? _topLevel;

    public TasksView()
    {
        InitializeComponent();
        // Post-retrofit, row DataContexts are TaskRow HOLDERS whose Data swaps
        // in place on value-change polls (CollectionReconciler's Update op) —
        // LoadingRow never re-fires for that, so classes must track the
        // holder. RowClassBinder owns the whole subscription lifecycle
        // (load/re-apply/recycle/unload/detach-drain); only the styling
        // applier is view-local.
        _rowBinder = RowClassBinder<TaskRow>.Attach(Grid, static (row, holder) =>
        {
            row.Classes.Set("atRisk", holder.Data.IsDeadlineAtRisk);
            row.Classes.Set("suspended", holder.Data.IsSuspended);
        });
        _columnsByTag = new Dictionary<string, DataGridColumn>
        {
            ["Project"] = ColumnWithHeader(Strings.ColProject),
            ["Application"] = ColumnWithHeader(Strings.ColApplication),
            ["Progress"] = ColumnWithHeader(Strings.ColProgress),
            ["Elapsed"] = ColumnWithHeader(Strings.ColElapsed),
            ["Remaining"] = ColumnWithHeader(Strings.ColRemaining),
            ["Deadline"] = ColumnWithHeader(Strings.ColDeadline),
            ["State"] = ColumnWithHeader(Strings.ColState),
        };
        DataContextChanged += (_, _) =>
        {
            LoadColumnPreferences();
            SyncStateFilterFromViewModel();
            WireConfirmationHandler();
        };
        // Right-click must move selection to the row under the pointer BEFORE
        // the ContextFlyout opens, or the menu would act on a stale selection.
        // Tunnel handling: the DataGrid's own pointer handling marks the event
        // handled on the bubble path.
        Grid.AddHandler(PointerPressedEvent, OnGridPointerPressed, RoutingStrategies.Tunnel);
        // And a context request that did NOT originate on a row (grid background,
        // column header, scrollbar) must not open the menu at all: the flyout is
        // attached to the whole DataGrid, so without this gate it would act on
        // the OLD selection — a task the user never right-clicked (Codex R2 P2,
        // PR #135). Cost: the keyboard menu key is suppressed too (its source is
        // the focused grid, not a row) — acceptable for M3.
        Grid.AddHandler(ContextRequestedEvent, OnGridContextRequested, RoutingStrategies.Tunnel);
        // Column-width persistence (#120): single-copy machinery, wired the same
        // one line in all four data views. Restores persisted widths on attach
        // and writes settled user resizes back — independent of the header-based
        // visibility path above (widths key off the column Tag, not the header).
        _widthPersistence = ColumnWidthPersistence.Attach(Grid, "tasks");
        ApplyColumnVisibility(BreakpointWidth);
#if DEBUG
        // Issue #95 measurement instrumentation; inert unless LATTICE_COMBO_PROBE is set.
        if (ComboOpenProbe.Mode is "tasks" or "watch")
            ComboOpenProbe.Attach(StateFilterBox, "tasks:StateFilterBox");
#endif
    }

    // The shell owns one long-lived TasksViewModel while TasksViews are
    // recreated per navigation, so the combo must render the VM's current
    // filter instead of the XAML default "All". Inverse of the switch in
    // OnStateFilterChanged; the SelectedIndex write re-enters that handler,
    // which assigns the same StateFilter value back — a no-op under the
    // ObservableProperty equality guard.
    private void SyncStateFilterFromViewModel()
    {
        if (DataContext is not TasksViewModel vm)
            return;
        // null (no filter) → the "All" index OUTSIDE the switch, so the switch over the non-nullable
        // TaskStateKind stays exhaustive with no `_` arm. (Inverse mapping in OnStateFilterChanged is
        // over an int index, not this enum, so it correctly keeps its `_`.)
        StateFilterBox.SelectedIndex = vm.StateFilter is { } kind ? StateFilterIndex(kind) : 0;
    }

#pragma warning disable CS8524 // No `_` arm on purpose: the old `_ => 0` folded any new NAMED
    // TaskStateKind into "All". CS8509 must stay live so a new kind forces a combo index; CS8524
    // (residual unnamed value, unreachable for a well-formed kind) is suppressed. RailTierProjection pattern.
    private static int StateFilterIndex(TaskStateKind kind) => kind switch
    {
        TaskStateKind.Running => 1,
        TaskStateKind.Waiting => 2,
        TaskStateKind.Suspended => 3,
        TaskStateKind.Uploading => 4,
    };
#pragma warning restore CS8524

    /// <summary>Window width when hosted (the design's breakpoint dimension);
    /// the view's own width only before attach, where it is a 0-width no-op.</summary>
    private double BreakpointWidth => _topLevel?.Bounds.Width ?? Bounds.Width;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is not null)
            _topLevel.PropertyChanged += OnTopLevelPropertyChanged;
        ApplyColumnVisibility(BreakpointWidth);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_topLevel is not null)
            _topLevel.PropertyChanged -= OnTopLevelPropertyChanged;
        _topLevel = null;
    }

    private void OnTopLevelPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == BoundsProperty)
            ApplyColumnVisibility(BreakpointWidth);
    }

    private DataGridColumn ColumnWithHeader(string header) =>
        Grid.Columns.Single(c => Equals(c.Header, header));

    // Restores persisted overflow-menu choices when the VM attaches: preference
    // dictionary, then the resulting column visibility (which also syncs the
    // menu checkboxes to the effective outcome).
    private void LoadColumnPreferences()
    {
        if (DataContext is not TasksViewModel vm)
            return;
        foreach (var columnKey in _userColumnPreferences.Keys.ToList())
            _userColumnPreferences[columnKey] = vm.GetColumnPreference(columnKey);
        ApplyColumnVisibility(BreakpointWidth);
    }

    // Re-derives every tracked column's visibility from the pure policy: user
    // preference (if any) wins, otherwise the breakpoint rule for that column
    // (a no-op default of "always visible" for columns without one).
    private void ApplyColumnVisibility(double width)
    {
        foreach (var (columnKey, column) in _columnsByTag)
            column.IsVisible = ColumnVisibilityPolicy.IsVisible(columnKey, width, _userColumnPreferences[columnKey]);
        SyncOverflowCheckboxes();
    }

    // The overflow checkboxes mirror EFFECTIVE visibility (the policy result),
    // not the raw preference: a breakpoint-hidden column reading "checked"
    // would take two clicks to show — the first persists false with no visible
    // change. Synced on every Apply so width changes keep them honest. Safe
    // against feedback: the toggle handler is Click-based, and programmatic
    // IsChecked writes do not raise Click.
    private void SyncOverflowCheckboxes()
    {
        if (OverflowButton.Flyout is not MenuFlyout flyout)
            return;
        foreach (var item in flyout.Items.OfType<MenuItem>())
            if (item.Tag is string columnKey && _columnsByTag.TryGetValue(columnKey, out var column))
                item.IsChecked = column.IsVisible;
    }

    private void OnColumnVisibilityToggled(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string columnName } item)
            return;
        _userColumnPreferences[columnName] = item.IsChecked;
        if (DataContext is TasksViewModel vm)
            vm.SetColumnPreference(columnName, item.IsChecked);
        ApplyColumnVisibility(BreakpointWidth);
    }

    private void OnStateFilterChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not TasksViewModel vm || sender is not ComboBox comboBox)
            return;
        vm.StateFilter = comboBox.SelectedIndex switch
        {
            1 => TaskStateKind.Running,
            2 => TaskStateKind.Waiting,
            3 => TaskStateKind.Suspended,
            4 => TaskStateKind.Uploading,
            _ => null,
        };
    }

    // Production wiring of the VM's dialog seam (design 2.5): the FA dialog is
    // constructed only here, never in VM code, so headless VM tests fake the
    // seam. The VM outlives its views (the shell recreates a TasksView per
    // navigation), so a handler installed by an EARLIER view is stale — its
    // closure resolves TopLevel off a detached control and would silently
    // decline every Confirm-class op (Codex P2, PR #135). Handlers therefore
    // carry their owning view via ViewConfirmationHandler, and wiring replaces
    // any VIEW-installed handler (newest view wins — order-independent, no
    // detach-time bookkeeping to race a page transition) while a fake the test
    // installed on the VM boundary is never touched.
    private void WireConfirmationHandler()
    {
        if (DataContext is not TasksViewModel vm)
            return;
        if (vm.ConfirmationHandler is null
            || vm.ConfirmationHandler.Target is ViewConfirmationHandler)
            vm.ConfirmationHandler = new ViewConfirmationHandler(this).ConfirmAsync;
    }

    // The marker type doubles as the closure: delegate.Target identifies a
    // view-installed handler regardless of WHICH view installed it.
    private sealed class ViewConfirmationHandler(TasksView owner)
    {
        public Task<bool> ConfirmAsync(ConfirmationRequest request) =>
            TopLevel.GetTopLevel(owner) is { } top
                ? ConfirmationDialog.ConfirmAsync(top, request)
                : Task.FromResult(false); // owner detached: fail safe, decline
    }

    private void OnGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(Grid).Properties.IsRightButtonPressed)
            return;
        if ((e.Source as Visual)?.FindAncestorOfType<DataGridRow>(includeSelf: true) is { } row)
            Grid.SelectedItem = row.DataContext;
    }

    private void OnGridContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<DataGridRow>(includeSelf: true) is null)
            e.Handled = true; // no row under the request: no target, no menu
    }

    // The failure bar holds the LAST op failure; any close (the bar's close
    // button, or IsOpen flipping through the binding) clears the surface —
    // unlike the partial bar there is no dismissal-episode bookkeeping here,
    // a dismissed failure simply stays gone until a newer failure replaces it.
    private void OnControlFailureClosed(FAInfoBar sender, FAInfoBarClosedEventArgs args)
    {
        if (args.Reason == FAInfoBarCloseReason.CloseButton && DataContext is TasksViewModel vm)
            vm.ControlFailure.Clear();
    }

    // Only a close-button click is a dismissal episode (design § partial-bar
    // dismissal semantics: dismiss snapshots the CURRENT unreachable id-set).
    // FAInfoBar also raises Closed with Reason=Programmatic whenever the IsOpen
    // binding flips false — scope switch to a single host, outage recovery —
    // and snapshotting there would suppress the warning on return to All hosts
    // even though the user never dismissed it.
    private void OnPartialBarClosed(FAInfoBar sender, FAInfoBarClosedEventArgs args)
    {
        if (args.Reason == FAInfoBarCloseReason.CloseButton && DataContext is TasksViewModel vm)
            vm.DismissPartialCommand.Execute(null);
    }

    private readonly RowClassBinder<TaskRow> _rowBinder;
    private readonly ColumnWidthPersistence _widthPersistence;

    /// <summary>Test seam (InternalsVisibleTo): live row-class subscriptions.
    /// The teardown-drain regression test pins this to 0 after detach.</summary>
    internal int RowSubscriptionCount => _rowBinder.Count;

    /// <summary>Test seam (InternalsVisibleTo): live column-width subscriptions.</summary>
    internal int ColumnWidthSubscriptionCount => _widthPersistence.SubscriptionCount;

    // Ctrl+F stays imperative on purpose: focusing a named control is a
    // view-local concern with no VM command to bind — unlike F5, which maps
    // 1:1 onto RefreshCommand and is declarative in the XAML KeyBindings.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
            return;
        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
        {
            FilterBox.Focus();
            e.Handled = true;
        }
    }
}
