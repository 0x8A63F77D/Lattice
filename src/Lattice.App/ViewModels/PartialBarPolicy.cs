namespace Lattice.App.ViewModels;

/// <summary>
/// Pure dismissal-episode semantics for the Tasks view's partial-results bar.
/// Invariant: a dismissal lives exactly as long as the continuous outage
/// episode it was issued in — where "continuous" means BOTH halves of the
/// partial-state picture stay unchanged: the unreachable id-set AND the
/// covered id-set (Connected hosts with a snapshot — the exact set the grid
/// is built from). Any change to either half (outage grows/shrinks/swaps, OR
/// a covered host is lost/gained even with the outage tier untouched)
/// re-reports the bar; the dismissal itself is kept until re-dismissed
/// (design doc, docs/design/m2/README.md:113: "reappears when the reachable
/// set changes" — Codex P2, PR #21 round 2). Extracted from TasksViewModel
/// after two repair rounds on this logic (coverage count, recovery reset) so
/// the transition table is unit-pinned exhaustively; the scope gate
/// (All-hosts only) stays in the ViewModel — this class knows nothing about
/// scopes or hosts.
/// </summary>
public static class PartialBarPolicy
{
    private static readonly IReadOnlySet<Guid> Empty = new HashSet<Guid>();

    /// <summary>
    /// The whole partial-state picture a dismissal must pin. Equality is
    /// deliberately a hand-written structural set comparison
    /// (<see cref="Matches"/>: Count + SetEquals both ways), NOT the
    /// synthesized record/struct equality — HashSet doesn't override
    /// Equals, so default equality over ISet-typed fields would compare by
    /// reference and every fingerprint would look distinct.
    /// </summary>
    public readonly struct Fingerprint(IReadOnlySet<Guid> unreachable, IReadOnlySet<Guid> covered)
    {
        public IReadOnlySet<Guid> Unreachable { get; } = unreachable;
        public IReadOnlySet<Guid> Covered { get; } = covered;

        public bool Matches(Fingerprint other) =>
            SetsEqual(Unreachable, other.Unreachable) && SetsEqual(Covered, other.Covered);

        private static bool SetsEqual(IReadOnlySet<Guid> a, IReadOnlySet<Guid> b) =>
            a.Count == b.Count && a.SetEquals(b);
    }

    public static readonly Fingerprint EmptyFingerprint = new(Empty, Empty);

    /// <summary>
    /// Advances the episode state for a freshly computed fingerprint. An empty
    /// unreachable set = full recovery: the episode ended, so the dismissal is
    /// forgotten and the bar hides. Otherwise the bar is visible unless the
    /// CURRENT fingerprint matches the dismissed one exactly — any change to
    /// either the unreachable set or the covered set re-reports, with the
    /// dismissal kept until re-dismissed.
    /// </summary>
    public static (Fingerprint Dismissed, bool Visible) Advance(Fingerprint dismissed, Fingerprint current)
        => current.Unreachable.Count == 0
            ? (EmptyFingerprint, false)
            : (dismissed, !current.Matches(dismissed));

    /// <summary>Dismissal snapshots the current fingerprint (empty unreachable set = no-op).</summary>
    public static Fingerprint Dismiss(Fingerprint current) => current;
}
