using Lattice.Core;
using Xunit;

namespace Lattice.Tests;

public class PollingCadencePolicyTests
{
    // The transition table from plan Part 3b, exhaustively:
    //  - window visible OR full-speed-hidden  => configured (unchanged), for every interval
    //  - hidden AND not full-speed            => max(configured, 30)
    // AllowedPollingIntervals = 2, 5, 10, 30, 60.
    [Theory]
    // window visible: always configured, regardless of full-speed flag.
    [InlineData(2, true, false, 2)]
    [InlineData(5, true, false, 5)]
    [InlineData(10, true, false, 10)]
    [InlineData(30, true, false, 30)]
    [InlineData(60, true, false, 60)]
    [InlineData(2, true, true, 2)]
    [InlineData(5, true, true, 5)]
    [InlineData(10, true, true, 10)]
    [InlineData(30, true, true, 30)]
    [InlineData(60, true, true, 60)]
    // hidden but full-speed on: floor removed, configured passes through.
    [InlineData(2, false, true, 2)]
    [InlineData(5, false, true, 5)]
    [InlineData(10, false, true, 10)]
    [InlineData(30, false, true, 30)]
    [InlineData(60, false, true, 60)]
    // hidden, not full-speed: floored to 30 below the floor, unchanged at/above it.
    [InlineData(2, false, false, 30)]
    [InlineData(5, false, false, 30)]
    [InlineData(10, false, false, 30)]
    [InlineData(30, false, false, 30)]
    [InlineData(60, false, false, 60)]
    public void EffectiveIntervalSeconds_matches_transition_table(
        int configured, bool windowVisible, bool fullSpeedHidden, int expected)
    {
        Assert.Equal(expected,
            PollingCadencePolicy.EffectiveIntervalSeconds(configured, windowVisible, fullSpeedHidden));
    }

    [Fact]
    public void HiddenFloorSeconds_is_30()
    {
        Assert.Equal(30, PollingCadencePolicy.HiddenFloorSeconds);
    }
}
