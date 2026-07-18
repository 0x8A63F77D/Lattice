using Avalonia.Controls;
using Lattice.App.ViewModels;
using Xunit;

namespace Lattice.App.Tests;

/// <summary>
/// Exhaustive transition table for close-to-tray (issue #92, plan Part 3a):
/// 5 <see cref="WindowCloseReason"/> values × 2³ boolean flags = 40 rows.
/// The dominance order is reason &gt; exitRequested &gt; isProgrammatic &gt; exitOnClose;
/// every combination is asserted, so an added flag interaction cannot slip through.
/// </summary>
public class WindowClosePolicyTests
{
    // Every named WindowCloseReason. The switch names all five; the [Theory] below
    // drives each against all 8 flag combinations.
    public static readonly WindowCloseReason[] AllReasons =
    [
        WindowCloseReason.Undefined,
        WindowCloseReason.WindowClosing,
        WindowCloseReason.OwnerWindowClosing,
        WindowCloseReason.ApplicationShutdown,
        WindowCloseReason.OSShutdown,
    ];

    public static TheoryData<WindowCloseReason, bool, bool, bool, CloseVerdict> Cases()
    {
        var data = new TheoryData<WindowCloseReason, bool, bool, bool, CloseVerdict>();
        foreach (WindowCloseReason reason in AllReasons)
            foreach (bool exitRequested in new[] { false, true })
                foreach (bool isProgrammatic in new[] { false, true })
                    foreach (bool exitOnClose in new[] { false, true })
                        data.Add(reason, exitRequested, isProgrammatic, exitOnClose,
                            Expected(reason, exitRequested, isProgrammatic, exitOnClose));
        return data;
    }

    // Independent oracle expressed as the plan's collapsed table (dominance order),
    // NOT a copy of the production switch — kept structurally distinct so the test
    // can disagree with a mis-transcribed implementation.
    private static CloseVerdict Expected(
        WindowCloseReason reason, bool exitRequested, bool isProgrammatic, bool exitOnClose)
    {
        // Platform/framework teardown always allows the close.
        if (reason is WindowCloseReason.ApplicationShutdown
                   or WindowCloseReason.OSShutdown
                   or WindowCloseReason.OwnerWindowClosing)
            return CloseVerdict.AllowClose;

        // Remaining: WindowClosing or Undefined.
        if (exitRequested) return CloseVerdict.AllowClose;
        if (isProgrammatic) return CloseVerdict.AllowClose;
        if (exitOnClose) return CloseVerdict.ExitApplication;
        return CloseVerdict.HideToTray;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Decide_matches_the_transition_table(
        WindowCloseReason reason, bool exitRequested, bool isProgrammatic, bool exitOnClose,
        CloseVerdict expected)
    {
        Assert.Equal(expected, WindowClosePolicy.Decide(reason, isProgrammatic, exitOnClose, exitRequested));
    }

    [Fact]
    public void Table_covers_all_forty_combinations()
    {
        Assert.Equal(40, Cases().Count());
    }
}
