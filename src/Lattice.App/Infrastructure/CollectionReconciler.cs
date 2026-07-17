using System.Collections.ObjectModel;
using System.Diagnostics;
using Lattice.App.Aggregation;
using Microsoft.FSharp.Collections;

namespace Lattice.App.Infrastructure;

/// <summary>
/// Applies Reconcile.diff ops to the bound collection. All decision logic is
/// in the F# diff; this class only translates ops into collection mutations.
/// Never raises Reset (Clear is never called).
/// </summary>
public static class CollectionReconciler
{
    public static void Apply<TKey, TRow>(
        ObservableCollection<RowHolder<TKey, TRow>> rows,
        FSharpList<ReconcileOp<TKey, TRow>> ops)
        where TKey : notnull
        => Apply(rows, ops, (key, row) => new RowHolder<TKey, TRow>(key, row));

    /// <summary>Overload for closed holder subclasses (XAML-bindable rows).</summary>
    public static void Apply<TKey, TRow>(
        ObservableCollection<RowHolder<TKey, TRow>> rows,
        FSharpList<ReconcileOp<TKey, TRow>> ops,
        Func<TKey, TRow, RowHolder<TKey, TRow>> createHolder)
        where TKey : notnull
    {
        foreach (var op in ops)
        {
            switch (op)
            {
                case ReconcileOp<TKey, TRow>.Update u:
                    Debug.Assert(
                        EqualityComparer<TKey>.Default.Equals(rows[u.Index].Key, u.Key),
                        "diff Update op must target the holder with the matching key");
                    rows[u.Index].Data = u.Row;
                    break;
                case ReconcileOp<TKey, TRow>.Insert ins:
                    rows.Insert(ins.Index, createHolder(ins.Key, ins.Row));
                    break;
                case ReconcileOp<TKey, TRow>.RemoveAt rem:
                    Debug.Assert(
                        EqualityComparer<TKey>.Default.Equals(rows[rem.Index].Key, rem.Key),
                        "diff RemoveAt op must target the holder with the matching key");
                    rows.RemoveAt(rem.Index);
                    break;
                case ReconcileOp<TKey, TRow>.Move mv:
                    // Contract (#86): a collection the DataGrid renders must never receive a
                    // Move — Avalonia 12.1's DataGridCollectionView.ProcessCollectionChanged has
                    // no Move case, so a source Move silently never renders (the "Projects can't
                    // sort" root cause, 2026-07-15). Every grid-bound consumer therefore aligns
                    // its target (Reconcile.alignToExisting), letting the bound collection view
                    // own display order; their diffs emit no Move by construction. A Move only
                    // reaches collections nothing renders directly, so it applies natively.
                    // (History: this used to translate to Remove+Insert of the same holder for
                    // the then directly-bound Tasks/Transfers grids; retired with its last
                    // consumer in #86.)
                    rows.Move(mv.FromIndex, mv.ToIndex);
                    break;
                default:
                    throw new UnreachableException($"Unhandled ReconcileOp: {op}");
            }
        }
    }
}
