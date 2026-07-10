using Lattice.App.Infrastructure;
using Lattice.App.Localization;
using Xunit;

namespace Lattice.App.Tests;

public class TimeTextTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 9, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 3)]
    [InlineData(59, 59)]
    [InlineData(60, 1)]
    [InlineData(3599, 59)]
    [InlineData(3600, 1)]
    public void UpdatedAgo_formats_per_design_copy(int secondsAgo, int value)
    {
        var expected = secondsAgo switch
        {
            < 60 => string.Format(Strings.UpdatedSecondsFmt, value),
            < 3600 => string.Format(Strings.UpdatedMinutesFmt, value),
            _ => string.Format(Strings.UpdatedHoursFmt, value),
        };
        Assert.Equal(expected, TimeText.UpdatedAgo(Now.AddSeconds(-secondsAgo), Now));
    }

    [Fact]
    public void UpdatedAgo_clamps_future_timestamps_to_zero() =>
        Assert.Equal(string.Format(Strings.UpdatedSecondsFmt, 0), TimeText.UpdatedAgo(Now.AddSeconds(5), Now));

    [Theory]
    [InlineData(12.0, 3)]
    [InlineData(0.4, 1)]
    [InlineData(0.0, 2)]
    [InlineData(-5.0, 2)]
    public void RetryCountdown_ceils_and_clamps(double secondsAhead, int attempt)
    {
        var s = Math.Max(0, (long)Math.Ceiling((Now.AddSeconds(secondsAhead) - Now).TotalSeconds));
        var expected = string.Format(Strings.RailRetryingFmt, s, attempt);
        Assert.Equal(expected, TimeText.RetryCountdown(Now.AddSeconds(secondsAhead), Now, attempt));
    }
}
