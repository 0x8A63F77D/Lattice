using Avalonia;
using Avalonia.VisualTree;

namespace Lattice.App.Tests.Headless;

/// <summary>
/// Shared visual-tree search helpers for headless UI tests. Lives in Lattice.App.Tests
/// (not Lattice.TestSupport, which must stay Avalonia-free for Lattice.Tests' sake).
/// </summary>
internal static class VisualTree
{
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
