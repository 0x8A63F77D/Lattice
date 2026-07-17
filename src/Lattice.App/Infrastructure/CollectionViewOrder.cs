using Avalonia.Collections;

namespace Lattice.App.Infrastructure;

/// <summary>
/// Post-reconcile order guard for grids whose display order is view-owned
/// (issue #86 — the flat-grid variant of the Projects pattern from #57): the
/// source collection stays in reconcile-friendly order (via
/// <c>Reconcile.alignToExisting</c>, so the diff never emits a Move) and the
/// bound <see cref="DataGridCollectionView"/>'s sort descriptions own display
/// order. The view does no live shaping — an in-place holder Update can change
/// a sort-relevant value without the view re-sorting — so after each reconcile
/// the VM asks this guard to Refresh the view iff its current order violates
/// its own active descriptions.
/// </summary>
public static class CollectionViewOrder
{
    public static void RefreshIfStale(DataGridCollectionView view)
    {
        // No active sort (e.g. the user ctrl-cleared the header sort): the view
        // mirrors source order and can never be order-stale.
        if (view.SortDescriptions.Count == 0)
            return;

        var current = view.Cast<object>().ToArray();
        // The same OrderBy/ThenBy chain DataGridCollectionView.SortList applies
        // on Refresh. Enumerable's sort is stable, so a conforming order re-sorts
        // to itself and ties never cause a spurious Refresh — steady-state polls
        // stay zero-event (issue #24).
        IEnumerable<object> expected = current;
        foreach (var sort in view.SortDescriptions)
            expected = expected is IOrderedEnumerable<object> ordered ? sort.ThenBy(ordered) : sort.OrderBy(expected);

        if (!expected.SequenceEqual(current))
            view.Refresh();
    }
}
