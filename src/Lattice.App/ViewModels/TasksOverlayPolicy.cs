namespace Lattice.App.ViewModels;

/// <summary>
/// Pure overlay choice for the Tasks view: (scoped hosts' rail tiers +
/// snapshot presence, task presence) -> which of the two overlays shows.
/// Invariant: Loading means "a first fetch is still plausibly in flight";
/// Empty means "a Connected host answered and it genuinely has no tasks".
///
/// Restructured (third repair round on this decision — Codex P2 rounds 3, 5,
/// 6) around a TOTAL per-host taxonomy instead of accreting boolean tweaks.
/// Every scoped host is exactly one of:
/// <list type="bullet">
/// <item><b>Contributing</b> — Connected tier with a snapshot: its data is in
/// the grid.</item>
/// <item><b>PendingFirstData</b> — no snapshot yet and not terminally parked:
/// its first visible data can still arrive.</item>
/// <item><b>Stale</b> — has a cached snapshot but left the Connected tier
/// (without parking terminally): counts for NOTHING. The grid's
/// Connected-only filter hides its data, so it cannot vouch for anything
/// visible; and it is not on the first-fetch path either, so it must not
/// suppress the loading state of a host that is (Codex P2 round 6).</item>
/// <item><b>Terminal</b> — Unreachable tier or AuthFailed: parked; the rail
/// and the partial bar tell that story, so it holds no overlay open and
/// vouches for nothing (Codex P2 round 3).</item>
/// </list>
/// Loading = nobody contributes AND someone is still pending first data.
/// Empty = not loading, no (unfiltered) tasks, and a Connected host vouches.
/// </summary>
public static class TasksOverlayPolicy
{
    /// <summary>One scoped host as the policy sees it.</summary>
    public readonly record struct HostFacts(RailState Tier, bool HasSnapshot);

    private enum HostClass
    {
        Contributing,
        PendingFirstData,
        Stale,
        Terminal,
    }

    // Total over (Tier, HasSnapshot): Terminal is checked first (a terminal
    // host is Terminal regardless of any cached snapshot), then the three
    // non-terminal cells partition on Connected x HasSnapshot.
    private static HostClass Classify(HostFacts host) => host switch
    {
        { Tier: RailState.Unreachable or RailState.AuthFailed } => HostClass.Terminal,
        { Tier: RailState.Connected, HasSnapshot: true } => HostClass.Contributing,
        { HasSnapshot: false } => HostClass.PendingFirstData,
        _ => HostClass.Stale,
    };

    /// <param name="scoped">Every host in the current scope.</param>
    /// <param name="hasTasks">Whether the UNFILTERED merged task set is
    /// non-empty. Callers must not pass post-filter row presence: a text/state
    /// filter hiding every row is a filter miss, not an empty task set.</param>
    public static (bool IsLoading, bool IsEmpty) Decide(
        IReadOnlyList<HostFacts> scoped, bool hasTasks)
    {
        var anyContributing = false;
        var anyPending = false;
        foreach (var host in scoped)
        {
            switch (Classify(host))
            {
                case HostClass.Contributing: anyContributing = true; break;
                case HostClass.PendingFirstData: anyPending = true; break;
                // Stale and Terminal hosts count for nothing.
            }
        }

        // Loading: no host feeds the grid yet, and at least one host's first
        // visible data can still arrive.
        var isLoading = !anyContributing && anyPending;

        // Empty: not loading, no tasks, and a Connected host vouches for the
        // absence — "no tasks" is a statement about a host that answered.
        var isEmpty = !isLoading
            && !hasTasks
            && scoped.Any(h => h.Tier == RailState.Connected);

        return (isLoading, isEmpty);
    }
}
