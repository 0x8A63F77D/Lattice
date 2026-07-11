using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using FluentAvalonia.UI.Controls;
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
        DataContextChanged += (_, _) => LoadColumnPreferences();
        ApplyColumnVisibility(BreakpointWidth);
    }

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
    // dictionary, menu checkbox state (unchosen columns show checked — the
    // all-visible default), and the resulting column visibility.
    private void LoadColumnPreferences()
    {
        if (DataContext is not TasksViewModel vm)
            return;
        foreach (var columnKey in _userColumnPreferences.Keys.ToList())
            _userColumnPreferences[columnKey] = vm.GetColumnPreference(columnKey);
        if (OverflowButton.Flyout is MenuFlyout flyout)
            foreach (var item in flyout.Items.OfType<MenuItem>())
                if (item.Tag is string columnKey)
                    item.IsChecked = _userColumnPreferences[columnKey] ?? true;
        ApplyColumnVisibility(BreakpointWidth);
    }

    // Re-derives every tracked column's visibility from the pure policy: user
    // preference (if any) wins, otherwise the breakpoint rule for that column
    // (a no-op default of "always visible" for columns without one).
    private void ApplyColumnVisibility(double width)
    {
        foreach (var (columnKey, column) in _columnsByTag)
            column.IsVisible = ColumnVisibilityPolicy.IsVisible(columnKey, width, _userColumnPreferences[columnKey]);
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

    private void OnLoadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (e.Row.DataContext is not TaskRowViewModel row)
            return;
        e.Row.Classes.Set("atRisk", row.IsDeadlineAtRisk);
        e.Row.Classes.Set("suspended", row.IsSuspended);
    }

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
