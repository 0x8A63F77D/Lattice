using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Lattice.App.Tests.Headless;

/// <summary>
/// Shared visual-tree search helpers for headless UI tests. Lives in Lattice.App.Tests
/// (not Lattice.TestSupport, which must stay Avalonia-free for Lattice.Tests' sake).
/// </summary>
internal static class VisualTree
{
    /// <summary>
    /// The realized <see cref="DataGridRow"/> at <paramref name="index"/> (the row's own
    /// <see cref="DataGridRow.Index"/>, which follows item order). Throws unless exactly one
    /// row carries that index — a materialized-row precondition, so Layout the window first.
    /// </summary>
    public static DataGridRow FindRow(DataGrid grid, int index) =>
        grid.GetVisualDescendants().OfType<DataGridRow>().Single(r => r.Index == index);

    /// <summary>
    /// Depth-first search for the first visual of type <typeparamref name="T"/> under
    /// (and including) <paramref name="root"/>, optionally filtered by a predicate.
    /// </summary>
    public static T? FindInVisualTree<T>(Visual root, Func<T, bool>? predicate = null) where T : Visual
    {
        if (root is T match && (predicate is null || predicate(match)))
            return match;

        foreach (var child in root.GetVisualChildren())
        {
            var result = FindInVisualTree(child, predicate);
            if (result != null)
                return result;
        }

        return null;
    }
}
