namespace Lattice.App.ViewModels;

/// <summary>
/// Pure overlay choice for the Tasks view: (scoped hosts' rail tiers +
/// snapshot presence, row presence) -> which of the two overlays shows.
/// Invariant: Loading means "a first fetch is still plausibly in flight";
/// Empty means "a Connected host answered and it genuinely has no tasks".
/// A scope where every host parked terminally (Unreachable tier, AuthFailed)
/// gets NEITHER overlay — the rail and the partial bar tell that story.
/// </summary>
public static class TasksOverlayPolicy
{
    /// <summary>One scoped host as the policy sees it.</summary>
    public readonly record struct HostFacts(RailState Tier, bool HasSnapshot);

    public static (bool IsLoading, bool IsEmpty) Decide(
        IReadOnlyList<HostFacts> scoped, bool hasRows)
    {
        // Loading: nothing fetched yet anywhere, and at least one host is
        // still on the initial path (not terminally parked) — its first
        // snapshot can still arrive.
        var isLoading = scoped.Count > 0
            && scoped.All(h => !h.HasSnapshot)
            && scoped.Any(h => h.Tier is not (RailState.Unreachable or RailState.AuthFailed));

        // Empty: not loading, no rows, and a Connected host vouches for the
        // absence — "no tasks" is a statement about a host that answered.
        var isEmpty = !isLoading
            && !hasRows
            && scoped.Any(h => h.Tier == RailState.Connected);

        return (isLoading, isEmpty);
    }
}
