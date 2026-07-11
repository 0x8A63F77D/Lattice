using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Lattice.App.Infrastructure;
using Xunit;
using static Lattice.Tests.HeadlessLayout;

namespace Lattice.App.Tests;

/// <summary>
/// The RowClassBinder contract, pinned against a real DataGrid (headless):
/// apply-on-load, re-apply on holder change, recycled-row handoff,
/// unload decrement, and — THE invariant the class exists for — drain when
/// the grid leaves the visual tree without ItemsSource ever changing.
/// </summary>
public class RowClassBinderTests
{
    // Minimal holder: the binder's contract needs only INotifyPropertyChanged
    // (RowHolder raises PropertyChanged solely for Data; this fake mirrors that).
    private sealed class Holder(string name) : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        public string Name { get; } = name;
        public void RaiseDataChanged() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Data"));
    }

    private static (Window Window, DataGrid Grid, RowClassBinder<Holder> Binder,
        List<(DataGridRow Row, Holder Holder)> Applied, ObservableCollection<Holder> Items)
        MakeGrid(params Holder[] holders)
    {
        var items = new ObservableCollection<Holder>(holders);
        var grid = new DataGrid
        {
            ItemsSource = items,
            Columns = { new DataGridTextColumn { Header = "Name", Binding = new Avalonia.Data.Binding("Name") } },
        };
        var applied = new List<(DataGridRow, Holder)>();
        // Attach before Show, as TasksView's constructor does.
        var binder = RowClassBinder<Holder>.Attach(grid, (row, holder) => applied.Add((row, holder)));
        var window = new Window { Width = 400, Height = 300, Content = grid };
        window.Show();
        Layout(window);
        return (window, grid, binder, applied, items);
    }

    [AvaloniaFact]
    public void Loading_a_row_applies_with_row_and_holder_and_tracks_it()
    {
        var holder = new Holder("a");
        var (window, _, binder, applied, _) = MakeGrid(holder);

        var call = Assert.Single(applied);
        Assert.Same(holder, call.Holder);
        Assert.Same(holder, call.Row.DataContext);
        Assert.Equal(1, binder.Count);
        window.Close();
    }

    [AvaloniaFact]
    public void Holder_change_while_loaded_reapplies()
    {
        var holder = new Holder("a");
        var (window, _, _, applied, _) = MakeGrid(holder);
        var loadedRow = Assert.Single(applied).Row;
        applied.Clear();

        holder.RaiseDataChanged();

        var call = Assert.Single(applied);
        Assert.Same(loadedRow, call.Row);
        Assert.Same(holder, call.Holder);
        window.Close();
    }

    // The contract, not the grid's internal path: after the item under a
    // realized row is replaced, the OLD holder must be dead (mutating it no
    // longer re-applies) and the NEW one live — whether the grid recycles the
    // DataGridRow through UnloadingRow+LoadingRow or re-loads it directly,
    // exactly one subscription survives.
    [AvaloniaFact]
    public void Replacing_the_item_unsubscribes_the_old_holder_and_binds_the_new()
    {
        var oldHolder = new Holder("old");
        var (window, _, binder, applied, items) = MakeGrid(oldHolder);

        var newHolder = new Holder("new");
        items[0] = newHolder;
        Layout(window);
        Assert.Equal(1, binder.Count);
        applied.Clear();

        oldHolder.RaiseDataChanged();
        Assert.Empty(applied);

        newHolder.RaiseDataChanged();
        var call = Assert.Single(applied);
        Assert.Same(newHolder, call.Holder);
        window.Close();
    }

    [AvaloniaFact]
    public void Unloading_a_row_unsubscribes_and_decrements_count()
    {
        var removed = new Holder("removed");
        var kept = new Holder("kept");
        var (window, _, binder, applied, items) = MakeGrid(removed, kept);
        Assert.Equal(2, binder.Count);

        items.RemoveAt(0);
        Layout(window);

        Assert.Equal(1, binder.Count);
        applied.Clear();
        removed.RaiseDataChanged();
        Assert.Empty(applied);
        window.Close();
    }

    // THE invariant this class exists for (the a2e0420 regression): navigation
    // detaches the grid WITHOUT touching ItemsSource, so UnloadingRow never
    // fires — the binder must drain every subscription on detach itself.
    [AvaloniaFact]
    public void Detaching_the_grid_drains_every_subscription()
    {
        var first = new Holder("a");
        var second = new Holder("b");
        var (window, _, binder, applied, _) = MakeGrid(first, second);
        Assert.Equal(2, binder.Count);

        window.Content = null;
        Layout(window);

        Assert.Equal(0, binder.Count);
        applied.Clear();
        first.RaiseDataChanged();
        second.RaiseDataChanged();
        Assert.Empty(applied);
        window.Close();
    }
}
