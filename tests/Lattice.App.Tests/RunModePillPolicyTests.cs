using Lattice.App.ViewModels;
using Xunit;

namespace Lattice.App.Tests;

/// <summary>
/// M3 PR H S1 degrade rule: the snooze pill is the time form (A) until the host text is
/// too wide to sit beside it, then it degrades to the icon chip (B). One threshold;
/// tested at the name and subtext boundaries. (Estimate: name 7.0px/char, subtext
/// 5.8px/char; Form A fits while the wider estimate stays ≤ 150px.)
/// </summary>
public class RunModePillPolicyTests
{
    [Theory]
    // Typical connected+snoozed rows keep the time pill.
    [InlineData(7, 19, RunModePillForm.Time)]   // "mini-01" / "Connected · 0 tasks"
    [InlineData(4, 4, RunModePillForm.Time)]    // very short — well under the floor
    // Name boundary: 21 chars ≈ 147px (fits), 22 ≈ 154px (degrades).
    [InlineData(21, 10, RunModePillForm.Time)]
    [InlineData(22, 10, RunModePillForm.Chip)]
    // Subtext boundary: 25 chars ≈ 145px (fits), 26 ≈ 150.8px (degrades).
    [InlineData(10, 25, RunModePillForm.Time)]
    [InlineData(10, 26, RunModePillForm.Chip)]
    // A long hostname degrades regardless of a short subtext (the mock's case).
    [InlineData(24, 8, RunModePillForm.Chip)]
    public void Decide_picks_the_form_from_the_wider_host_text(int nameLen, int subtextLen, RunModePillForm expected) =>
        Assert.Equal(expected, RunModePillPolicy.Decide(nameLen, subtextLen));
}
