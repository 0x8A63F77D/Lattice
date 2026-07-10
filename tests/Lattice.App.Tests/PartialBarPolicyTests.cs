using Lattice.App.ViewModels;
using Xunit;

namespace Lattice.App.Tests;

/// <summary>
/// Exhaustive transition table for the partial-bar dismissal-episode semantics.
/// Invariant under test: a dismissal lives exactly as long as the continuous
/// outage episode it was issued in, where "continuous" covers BOTH the
/// unreachable id-set AND the covered id-set (Codex P2 round 2 — the design
/// doc's "reappears when the reachable set changes"). These unit-pin the
/// paths the integration tests can't cheaply reach (shrink / swap / grow /
/// covered-only-change / dismiss-on-empty).
/// </summary>
public class PartialBarPolicyTests
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();
    private static readonly Guid C = Guid.NewGuid();

    private static IReadOnlySet<Guid> Set(params Guid[] ids) => new HashSet<Guid>(ids);

    private static PartialBarPolicy.Fingerprint Fp(IReadOnlySet<Guid> unreachable, IReadOnlySet<Guid> covered) =>
        new(unreachable, covered);

    [Fact]
    public void No_outage_and_nothing_dismissed_stays_hidden_and_empty()
    {
        var (dismissed, visible) = PartialBarPolicy.Advance(
            PartialBarPolicy.EmptyFingerprint, Fp(Set(), Set()));
        Assert.True(dismissed.Matches(PartialBarPolicy.EmptyFingerprint));
        Assert.False(visible);
    }

    [Fact]
    public void New_outage_with_nothing_dismissed_is_visible()
    {
        var (dismissed, visible) = PartialBarPolicy.Advance(
            PartialBarPolicy.EmptyFingerprint, Fp(Set(A), Set(B)));
        Assert.True(dismissed.Matches(PartialBarPolicy.EmptyFingerprint));
        Assert.True(visible);
    }

    [Fact]
    public void Dismissing_the_current_fingerprint_hides_the_bar()
    {
        var current = Fp(Set(A), Set(B));
        var dismissed = PartialBarPolicy.Dismiss(current);
        var (kept, visible) = PartialBarPolicy.Advance(dismissed, current);
        Assert.True(kept.Matches(current));
        Assert.False(visible);
    }

    [Fact]
    public void Same_fingerprint_persisting_stays_hidden_across_repeated_advances()
    {
        var current = Fp(Set(A, B), Set(C));
        var dismissed = PartialBarPolicy.Dismiss(current);
        for (var i = 0; i < 3; i++)
        {
            (dismissed, bool visible) = PartialBarPolicy.Advance(dismissed, current);
            Assert.False(visible);
        }
        Assert.True(dismissed.Matches(current));
    }

    [Fact]
    public void Growing_unreachable_set_is_visible_again_with_the_dismissal_kept()
    {
        var dismissed = PartialBarPolicy.Dismiss(Fp(Set(A), Set(C)));
        var (kept, visible) = PartialBarPolicy.Advance(dismissed, Fp(Set(A, B), Set(C)));
        Assert.True(visible);
        // The dismissal is kept until re-dismissed, not silently widened.
        Assert.True(kept.Matches(Fp(Set(A), Set(C))));
    }

    [Fact]
    public void Shrinking_unreachable_set_partial_recovery_is_visible()
    {
        var (dismissed, visible) = PartialBarPolicy.Advance(
            Fp(Set(A, B), Set(C)), Fp(Set(A), Set(C)));
        Assert.True(visible);
        Assert.True(dismissed.Matches(Fp(Set(A, B), Set(C))));
    }

    [Fact]
    public void Swapped_unreachable_membership_is_visible()
    {
        var (dismissed, visible) = PartialBarPolicy.Advance(
            Fp(Set(A), Set(C)), Fp(Set(B), Set(C)));
        Assert.True(visible);
        Assert.True(dismissed.Matches(Fp(Set(A), Set(C))));
    }

    [Fact]
    public void Full_recovery_forgets_the_dismissal_and_hides()
    {
        var (dismissed, visible) = PartialBarPolicy.Advance(
            Fp(Set(A), Set(C)), Fp(Set(), Set(B, C)));
        Assert.True(dismissed.Matches(PartialBarPolicy.EmptyFingerprint));
        Assert.False(visible);
    }

    [Fact]
    public void Refail_with_the_identical_fingerprint_after_recovery_is_visible()
    {
        // The full episode: outage -> dismiss -> recover -> same-fingerprint outage.
        var outage = Fp(Set(A), Set(C));
        var dismissed = PartialBarPolicy.Dismiss(outage);

        (dismissed, bool hiddenNow) = PartialBarPolicy.Advance(dismissed, outage);
        Assert.False(hiddenNow);

        (dismissed, _) = PartialBarPolicy.Advance(dismissed, Fp(Set(), Set(A, C)));

        (dismissed, bool visible) = PartialBarPolicy.Advance(dismissed, outage);
        Assert.True(visible, "a new outage after full recovery must be reported even with the same fingerprint");
    }

    [Fact]
    public void Dismiss_on_an_empty_unreachable_set_is_a_no_op()
    {
        var dismissed = PartialBarPolicy.Dismiss(Fp(Set(), Set(A, B)));
        Assert.True(dismissed.Matches(Fp(Set(), Set(A, B))));
        // And advancing a fresh outage from that state still reports it.
        var (_, visible) = PartialBarPolicy.Advance(dismissed, Fp(Set(A), Set(B)));
        Assert.True(visible);
    }

    [Fact]
    public void Covered_set_shrinking_while_unreachable_set_is_unchanged_is_visible_again()
    {
        // Codex P2 round 2: a covered host dropping out (e.g. into Retrying,
        // below the unreachable tier) changes the grid's coverage without
        // touching the unreachable id-set at all. The old id-set-only
        // fingerprint would keep the bar hidden forever after dismissal.
        var dismissed = PartialBarPolicy.Dismiss(Fp(Set(A), Set(B, C)));
        var (kept, visible) = PartialBarPolicy.Advance(dismissed, Fp(Set(A), Set(B)));
        Assert.True(visible, "the covered set shrank from {B, C} to {B}; the bar must reappear");
        Assert.True(kept.Matches(Fp(Set(A), Set(B, C))), "the dismissal is kept until re-dismissed");
    }

    [Fact]
    public void Covered_set_growing_while_unreachable_set_is_unchanged_is_visible_again()
    {
        var dismissed = PartialBarPolicy.Dismiss(Fp(Set(A), Set(B)));
        var (_, visible) = PartialBarPolicy.Advance(dismissed, Fp(Set(A), Set(B, C)));
        Assert.True(visible, "a newly covered host changes the fingerprint too");
    }

    [Fact]
    public void Unchanged_fingerprint_with_both_sets_identical_stays_hidden()
    {
        var current = Fp(Set(A), Set(B, C));
        var dismissed = PartialBarPolicy.Dismiss(current);
        var (_, visible) = PartialBarPolicy.Advance(dismissed, Fp(Set(A), Set(B, C)));
        Assert.False(visible, "nothing changed in either half of the fingerprint");
    }

    [Fact]
    public void Episode_end_forgets_dismissal_even_if_covered_set_also_changed()
    {
        // The forget-on-recovery rule is keyed on the unreachable set only:
        // once it empties the whole dismissal is dropped, regardless of what
        // the covered set is doing at that instant.
        var dismissed = PartialBarPolicy.Dismiss(Fp(Set(A), Set(B)));
        var (afterRecovery, visible) = PartialBarPolicy.Advance(dismissed, Fp(Set(), Set(B, C)));
        Assert.False(visible);
        Assert.True(afterRecovery.Matches(PartialBarPolicy.EmptyFingerprint));
    }
}
