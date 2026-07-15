using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Lattice.App.Aggregation;
using Lattice.App.Infrastructure;
using Xunit;

namespace Lattice.App.Tests;

public class CollectionReconcilerTests
{
    private static ObservableCollection<RowHolder<int, string>> Collection(params (int Key, string Row)[] items) =>
        new(items.Select(i => new RowHolder<int, string>(i.Key, i.Row)));

    private static void ReconcileInto(ObservableCollection<RowHolder<int, string>> rows, params (int Key, string Row)[] target)
    {
        // C# tuple literal -> ValueTuple cast to match F#'s struct ('Key * 'Row) parameter shape.
        var existing = rows.Select(h => ((int, string))(h.Key, h.Data)).ToArray();
        CollectionReconciler.Apply(rows, Reconcile.diff(existing, target.Select(t => ((int, string))t).ToArray()));
    }

    [Fact]
    public void Value_change_keeps_holder_identity_and_raises_no_collection_event()
    {
        var rows = Collection((1, "a"), (2, "b"));
        var holder = rows[0];
        var events = new List<NotifyCollectionChangedAction>();
        rows.CollectionChanged += (_, e) => events.Add(e.Action);

        ReconcileInto(rows, (1, "a2"), (2, "b"));

        Assert.Empty(events);
        Assert.Same(holder, rows[0]);
        Assert.Equal("a2", rows[0].Data);
    }

    // A reorder is applied as Remove+Insert of the SAME holder instances, NOT ObservableCollection
    // .Move: Avalonia 12.1's DataGrid ignores a CollectionChanged.Move (the backing collection
    // reorders but the rendered rows do not — the "Projects can't sort" root cause, 2026-07-15).
    // The invariants that matter are unchanged and asserted here: never a Reset (Clear), and the
    // moved holder OBJECT identity survives so in-place value liveness/selection is preserved. The
    // rendered-grid proof of this workaround is A_reconciler_move_reorders_the_rendered_grid... in
    // the headless ProjectsViewTests.
    [Fact]
    public void Reorder_reinserts_holders_without_reset()
    {
        var rows = Collection((1, "a"), (2, "b"));
        var first = rows[0];
        var second = rows[1];
        var events = new List<NotifyCollectionChangedAction>();
        rows.CollectionChanged += (_, e) => events.Add(e.Action);

        ReconcileInto(rows, (2, "b"), (1, "a"));

        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, events);
        // The DataGrid-visible shape: Remove then Insert, not a Move it would ignore.
        Assert.DoesNotContain(NotifyCollectionChangedAction.Move, events);
        Assert.Contains(NotifyCollectionChangedAction.Remove, events);
        Assert.Contains(NotifyCollectionChangedAction.Add, events);
        // Same holder OBJECTS, reordered — identity preserved through the reinsert.
        Assert.Same(second, rows[0]);
        Assert.Same(first, rows[1]);
    }

    [Fact]
    public void Add_and_remove_touch_only_the_changed_identities()
    {
        var rows = Collection((1, "a"), (2, "b"));
        var survivor = rows[1];

        ReconcileInto(rows, (2, "b"), (3, "c"));

        Assert.Equal(2, rows.Count);
        Assert.Same(survivor, rows[0]);
        Assert.Equal(3, rows[1].Key);
    }

    private sealed class FakeRow(int key, string data) : RowHolder<int, string>(key, data);

    [Fact]
    public void CreateHolder_overload_inserts_subclass_instances()
    {
        var rows = new ObservableCollection<RowHolder<int, string>>();
        var existing = Array.Empty<(int, string)>();

        CollectionReconciler.Apply(rows, Reconcile.diff(existing, [(1, "a")]), (k, r) => new FakeRow(k, r));

        Assert.IsType<FakeRow>(rows[0]);
        Assert.Equal("a", rows[0].Data);
    }
}
