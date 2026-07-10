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
    // (design §Task 11 code-behind responsibilities). UiStateStore persistence
    // (Task 12) can populate this dictionary at startup without touching the
    // policy or this view.
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
    private readonly Dictionary<string, DataGridColumn> _columnsByTag;

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
        PropertyChanged += OnViewPropertyChanged;
        ApplyColumnVisibility(Bounds.Width);
    }

    private DataGridColumn ColumnWithHeader(string header) =>
        Grid.Columns.Single(c => Equals(c.Header, header));

    private void OnViewPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == BoundsProperty)
            ApplyColumnVisibility(Bounds.Width);
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
        ApplyColumnVisibility(Bounds.Width);
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

    private void OnPartialBarClosed(FAInfoBar sender, FAInfoBarClosedEventArgs args)
    {
        if (DataContext is TasksViewModel vm)
            vm.DismissPartialCommand.Execute(null);
    }

    private void OnLoadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (e.Row.DataContext is not TaskRowViewModel row)
            return;
        e.Row.Classes.Set("atRisk", row.IsDeadlineAtRisk);
        e.Row.Classes.Set("suspended", row.IsSuspended);
    }

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
        else if (e.Key == Key.F5)
        {
            if (DataContext is TasksViewModel vm)
                vm.RefreshCommand.Execute(null);
            e.Handled = true;
        }
    }
}
