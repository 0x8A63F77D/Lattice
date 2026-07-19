using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Lattice.App.ViewModels;

namespace Lattice.App.Infrastructure;

/// <summary>
/// Persists and restores user-resized DataGrid column widths (issue #120), the
/// single-copy machinery all four data views wire in with one line — mirroring
/// how <see cref="RowClassBinder{THolder}"/> owns the row-class lifecycle. The
/// correctness decisions (garbage rejection, key building, which measured width
/// is worth writing) live in the pure <see cref="ColumnWidthPolicy"/>; this
/// class is the thin framework shell around it.
///
/// Keying: each persisted column carries a stable, non-localized string
/// <c>Tag</c> in XAML (DataGridColumn has no x:Name — it is a non-Visual
/// AvaloniaObject); the composite key is "{viewKey}/{tag}". Columns without a
/// Tag (fixed icon/indicator columns) are not persisted.
///
/// Star columns: every production grid uses fixed pixel columns, but the guard
/// is deliberate — a star column is SKIPPED entirely (never captured, restored,
/// or written), so its proportional fill is preserved. Restoring a persisted
/// width always pins a column to an absolute pixel width; applying that to a
/// star column would silently convert its sizing mode, so we don't.
/// </summary>
public sealed class ColumnWidthPersistence
{
    private readonly DataGrid _grid;
    private readonly Dictionary<string, DataGridColumn> _columns = new();
    private readonly Dictionary<string, double> _defaults = new();
    private readonly Dictionary<DataGridColumn, EventHandler<AvaloniaPropertyChangedEventArgs>> _handlers = new();
    private UiStateStore? _store;
    private bool _restoring;
    private bool _flushArmed;
    private bool _attached;

    private ColumnWidthPersistence(DataGrid grid, string viewKey)
    {
        _grid = grid;
        // Capture the pristine XAML widths at construction (columns already
        // materialized by InitializeComponent, Tags set, and no restore has run
        // yet) — the baseline the persist diff measures against.
        foreach (var column in grid.Columns)
        {
            if (column.Tag is not string tag || column.Width.IsStar)
                continue;
            var key = ColumnWidthPolicy.Key(viewKey, tag);
            _columns[key] = column;
            _defaults[key] = column.Width.Value;
        }
    }

    /// <summary>
    /// Wires the persistence to the grid's attach/detach lifecycle. Call once in
    /// the view constructor. The store is resolved from the inherited
    /// <see cref="ColumnWidthScope"/> at attach time; with no scope set the whole
    /// thing is an inert no-op.
    /// </summary>
    public static ColumnWidthPersistence Attach(DataGrid grid, string viewKey)
    {
        var persistence = new ColumnWidthPersistence(grid, viewKey);
        grid.AttachedToVisualTree += (_, _) => persistence.OnAttached();
        grid.DetachedFromVisualTree += (_, _) => persistence.OnDetached();
        return persistence;
    }

    /// <summary>Live width-change subscriptions; the teardown test pins this to 0 after detach.</summary>
    internal int SubscriptionCount => _handlers.Count;

    private void OnAttached()
    {
        if (_attached)
            return;
        _store = ColumnWidthScope.GetStore(_grid);
        if (_store is null)
            return; // no scope provided (e.g. a bare-window test) → inert
        _attached = true;
        Restore();
        Subscribe();
    }

    private void OnDetached()
    {
        if (!_attached)
            return;
        Flush(); // persist any settled-but-not-yet-flushed resize before teardown
        foreach (var (column, handler) in _handlers)
            column.PropertyChanged -= handler;
        _handlers.Clear();
        _attached = false;
    }

    private void Restore()
    {
        var persisted = _store!.Load().ColumnWidths;
        _restoring = true;
        try
        {
            foreach (var (key, column) in _columns)
                if (ColumnWidthPolicy.TryGetRestoreWidth(persisted, key, out var width))
                    column.Width = new DataGridLength(width, DataGridLengthUnitType.Pixel);
        }
        finally
        {
            _restoring = false;
        }
    }

    private void Subscribe()
    {
        foreach (var column in _columns.Values)
        {
            EventHandler<AvaloniaPropertyChangedEventArgs> handler = (_, e) =>
            {
                if (e.Property == DataGridColumn.WidthProperty)
                    OnWidthChanged();
            };
            column.PropertyChanged += handler;
            _handlers[column] = handler;
        }
    }

    private void OnWidthChanged()
    {
        // Ignore our own restore writes and post-detach stragglers; coalesce a
        // burst of notifications into a single armed flush.
        if (_restoring || !_attached || _flushArmed)
            return;
        _flushArmed = true;
        // Background priority: a live resize drag emits Input-priority width
        // changes continuously, starving this lower-priority flush until the user
        // pauses or releases — so it observes a SETTLED width and coalesces the
        // whole drag into one write, never per pixel. Timer-free, so it is fully
        // deterministic under the headless dispatcher's RunJobs.
        Dispatcher.UIThread.Post(Flush, DispatcherPriority.Background);
    }

    private void Flush()
    {
        _flushArmed = false;
        if (_store is null)
            return;
        var measured = _columns.ToDictionary(kv => kv.Key, kv => kv.Value.ActualWidth);
        var writes = ColumnWidthPolicy.ComputeWrites(measured, _store.Load().ColumnWidths, _defaults);
        if (writes.Count == 0)
            return;
        // Read-modify-write through Update: a fresh load before mutating, so a
        // width save never clobbers a visibility/theme/scope preference another
        // consumer wrote since (UiStateStore.Update doctrine, Codex P2 PR #45).
        _store.Update(state =>
        {
            var widths = new Dictionary<string, double>(state.ColumnWidths);
            foreach (var (key, value) in writes)
                widths[key] = value;
            return state with { ColumnWidths = widths };
        });
    }
}
