using Lattice.App.ViewModels;
using Xunit;

namespace Lattice.App.Tests;

/// <summary>
/// Transition table for <see cref="ColumnVisibilityPolicy"/>: user preference always wins;
/// absent one, width-based breakpoints decide for Elapsed/Application only.
/// </summary>
public class ColumnVisibilityPolicyTests
{
    private const double Wide = 1280;    // >= both breakpoints
    private const double Narrow = 1050;  // hides Elapsed, keeps Application
    private const double Narrower = 900; // hides both

    [Theory]
    [InlineData("Elapsed", Wide, null, true)]
    [InlineData("Elapsed", Narrow, null, false)]
    [InlineData("Elapsed", Narrower, null, false)]
    [InlineData("Elapsed", Wide, true, true)]
    [InlineData("Elapsed", Wide, false, false)]
    [InlineData("Elapsed", Narrow, true, true)]
    [InlineData("Elapsed", Narrow, false, false)]
    [InlineData("Elapsed", Narrower, true, true)]
    [InlineData("Elapsed", Narrower, false, false)]
    [InlineData("Application", Wide, null, true)]
    [InlineData("Application", Narrow, null, true)]
    [InlineData("Application", Narrower, null, false)]
    [InlineData("Application", Wide, true, true)]
    [InlineData("Application", Wide, false, false)]
    [InlineData("Application", Narrow, true, true)]
    [InlineData("Application", Narrow, false, false)]
    [InlineData("Application", Narrower, true, true)]
    [InlineData("Application", Narrower, false, false)]
    // An unaffected column (no breakpoint rule) stays visible at any width absent
    // a preference, and still follows an explicit preference when one is set.
    [InlineData("Project", Wide, null, true)]
    [InlineData("Project", Narrow, null, true)]
    [InlineData("Project", Narrower, null, true)]
    [InlineData("Project", Narrower, true, true)]
    [InlineData("Project", Narrower, false, false)]
    public void IsVisible_matches_the_transition_table(
        string columnKey, double viewWidth, bool? userPreference, bool expected)
    {
        Assert.Equal(expected, ColumnVisibilityPolicy.IsVisible(columnKey, viewWidth, userPreference));
    }
}
