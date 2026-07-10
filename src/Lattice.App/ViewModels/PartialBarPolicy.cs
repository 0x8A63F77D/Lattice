namespace Lattice.App.ViewModels;

/// <summary>
/// Pure dismissal-episode semantics for the Tasks view's partial-results bar.
/// Invariant: a dismissal lives exactly as long as the continuous outage
/// episode it was issued in. Extracted from TasksViewModel after two repair
/// rounds on this logic (coverage count, recovery reset) so the transition
/// table is unit-pinned exhaustively; the scope gate (All-hosts only) stays
/// in the ViewModel — this class knows nothing about scopes or hosts.
/// </summary>
public static class PartialBarPolicy
{
    private static readonly IReadOnlySet<Guid> Empty = new HashSet<Guid>();

    /// <summary>
    /// Advances the episode state for a freshly computed unreachable set.
    /// Empty set = full recovery: the episode ended, so the dismissal is
    /// forgotten and the bar hides. Otherwise the bar is visible unless the
    /// outage set is exactly the one the user dismissed — any change (grow,
    /// shrink, swap) re-reports, with the dismissal kept until re-dismissed.
    /// </summary>
    public static (IReadOnlySet<Guid> Dismissed, bool Visible) Advance(
        IReadOnlySet<Guid> dismissed, IReadOnlySet<Guid> unreachable)
        => unreachable.Count == 0
            ? (Empty, false)
            : (dismissed, !unreachable.SetEquals(dismissed));

    /// <summary>Dismissal snapshots the current outage set (empty set = no-op).</summary>
    public static IReadOnlySet<Guid> Dismiss(IReadOnlySet<Guid> unreachable) => unreachable;
}
