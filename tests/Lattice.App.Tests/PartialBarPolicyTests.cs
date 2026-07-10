using Lattice.App.ViewModels;
using Xunit;

namespace Lattice.App.Tests;

/// <summary>
/// Exhaustive transition table for the partial-bar dismissal-episode semantics.
/// Invariant under test: a dismissal lives exactly as long as the continuous
/// outage episode it was issued in. These unit-pin the paths the integration
/// tests can't cheaply reach (shrink / swap / dismiss-on-empty).
/// </summary>
public class PartialBarPolicyTests
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();

    private static IReadOnlySet<Guid> Set(params Guid[] ids) => new HashSet<Guid>(ids);

    [Fact]
    public void No_outage_and_nothing_dismissed_stays_hidden_and_empty()
    {
        var (dismissed, visible) = PartialBarPolicy.Advance(Set(), Set());
        Assert.Empty(dismissed);
        Assert.False(visible);
    }

    [Fact]
    public void New_outage_with_nothing_dismissed_is_visible()
    {
        var (dismissed, visible) = PartialBarPolicy.Advance(Set(), Set(A));
        Assert.Empty(dismissed);
        Assert.True(visible);
    }

    [Fact]
    public void Dismissing_the_current_set_hides_the_bar()
    {
        var dismissed = PartialBarPolicy.Dismiss(Set(A));
        var (kept, visible) = PartialBarPolicy.Advance(dismissed, Set(A));
        Assert.True(kept.SetEquals(Set(A)));
        Assert.False(visible);
    }

    [Fact]
    public void Same_set_persisting_stays_hidden_across_repeated_advances()
    {
        IReadOnlySet<Guid> dismissed = PartialBarPolicy.Dismiss(Set(A, B));
        for (var i = 0; i < 3; i++)
        {
            (dismissed, bool visible) = PartialBarPolicy.Advance(dismissed, Set(A, B));
            Assert.False(visible);
        }
        Assert.True(dismissed.SetEquals(Set(A, B)));
    }

    [Fact]
    public void Growing_set_is_visible_again_with_the_dismissal_kept()
    {
        var (dismissed, visible) = PartialBarPolicy.Advance(Set(A), Set(A, B));
        Assert.True(visible);
        // The dismissal is kept until re-dismissed, not silently widened.
        Assert.True(dismissed.SetEquals(Set(A)));
    }

    [Fact]
    public void Shrinking_set_partial_recovery_is_visible()
    {
        var (dismissed, visible) = PartialBarPolicy.Advance(Set(A, B), Set(A));
        Assert.True(visible);
        Assert.True(dismissed.SetEquals(Set(A, B)));
    }

    [Fact]
    public void Swapped_membership_is_visible()
    {
        var (dismissed, visible) = PartialBarPolicy.Advance(Set(A), Set(B));
        Assert.True(visible);
        Assert.True(dismissed.SetEquals(Set(A)));
    }

    [Fact]
    public void Full_recovery_forgets_the_dismissal_and_hides()
    {
        var (dismissed, visible) = PartialBarPolicy.Advance(Set(A), Set());
        Assert.Empty(dismissed);
        Assert.False(visible);
    }

    [Fact]
    public void Refail_with_the_identical_set_after_recovery_is_visible()
    {
        // The full episode: outage -> dismiss -> recover -> same-set outage.
        IReadOnlySet<Guid> dismissed = PartialBarPolicy.Dismiss(Set(A));

        (dismissed, bool hiddenNow) = PartialBarPolicy.Advance(dismissed, Set(A));
        Assert.False(hiddenNow);

        (dismissed, _) = PartialBarPolicy.Advance(dismissed, Set());

        (dismissed, bool visible) = PartialBarPolicy.Advance(dismissed, Set(A));
        Assert.True(visible, "a new outage after full recovery must be reported even with the same id-set");
    }

    [Fact]
    public void Dismiss_on_an_empty_set_is_a_no_op()
    {
        var dismissed = PartialBarPolicy.Dismiss(Set());
        Assert.Empty(dismissed);
        // And advancing a fresh outage from that state still reports it.
        var (_, visible) = PartialBarPolicy.Advance(dismissed, Set(A));
        Assert.True(visible);
    }
}
