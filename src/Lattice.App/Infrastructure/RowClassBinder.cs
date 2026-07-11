using System.ComponentModel;
using Avalonia.Controls;

namespace Lattice.App.Infrastructure;

/// <summary>
/// Owns the row-class subscription lifecycle for a DataGrid bound to
/// RowHolder rows: apply on load, re-apply when the holder's Data is swapped
/// in place by the reconciler, unsubscribe on unload AND on row recycling,
/// and drain everything when the grid leaves the visual tree — the shell's
/// ContentControl swaps views without touching ItemsSource, so UnloadingRow
/// never fires on navigation (the a2e0420 regression). Views attach once and
/// structurally cannot forget any leg of this.
/// </summary>
public sealed class RowClassBinder<THolder> where THolder : class, INotifyPropertyChanged
{
    private readonly Dictionary<DataGridRow, (THolder Holder, PropertyChangedEventHandler Handler)> _subscriptions = new();
    private readonly Action<DataGridRow, THolder> _apply;

    private RowClassBinder(Action<DataGridRow, THolder> apply) => _apply = apply;

    /// <summary>Rows currently tracked; the teardown-drain tests pin this to 0 after detach.</summary>
    public int Count => _subscriptions.Count;

    public static RowClassBinder<THolder> Attach(DataGrid grid, Action<DataGridRow, THolder> apply)
    {
        var binder = new RowClassBinder<THolder>(apply);
        grid.LoadingRow += binder.OnLoadingRow;
        grid.UnloadingRow += binder.OnUnloadingRow;
        grid.DetachedFromVisualTree += (_, _) => binder.Drain();
        return binder;
    }

    private void OnLoadingRow(object? sender, DataGridRowEventArgs e)
    {
        // Row recycling: a recycled DataGridRow gets a fresh LoadingRow for its
        // new item — unsubscribe any previous entry before overwriting.
        if (_subscriptions.Remove(e.Row, out var stale))
            stale.Holder.PropertyChanged -= stale.Handler;
        if (e.Row.DataContext is not THolder holder) return;

        _apply(e.Row, holder);
        // Holders raise PropertyChanged only for Data (the reconciler's
        // in-place swap), so no property-name filter is needed.
        PropertyChangedEventHandler handler = (_, _) => _apply(e.Row, holder);
        holder.PropertyChanged += handler;
        _subscriptions[e.Row] = (holder, handler);
    }

    private void OnUnloadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (_subscriptions.Remove(e.Row, out var sub))
            sub.Holder.PropertyChanged -= sub.Handler;
    }

    private void Drain()
    {
        foreach (var (holder, handler) in _subscriptions.Values)
            holder.PropertyChanged -= handler;
        _subscriptions.Clear();
    }
}
