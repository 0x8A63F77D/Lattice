using Lattice.App.Infrastructure;
using Xunit;

namespace Lattice.App.Tests;

public class TimeTextTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 9, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, "Updated 0s ago")]
    [InlineData(3, "Updated 3s ago")]
    [InlineData(59, "Updated 59s ago")]
    [InlineData(60, "Updated 1m ago")]
    [InlineData(3599, "Updated 59m ago")]
    [InlineData(3600, "Updated 1h ago")]
    public void UpdatedAgo_formats_per_design_copy(int secondsAgo, string expected) =>
        Assert.Equal(expected, TimeText.UpdatedAgo(Now.AddSeconds(-secondsAgo), Now));

    [Fact]
    public void UpdatedAgo_clamps_future_timestamps_to_zero() =>
        Assert.Equal("Updated 0s ago", TimeText.UpdatedAgo(Now.AddSeconds(5), Now));

    [Theory]
    [InlineData(12.0, 3, "Retrying in 12s (attempt 3)")]
    [InlineData(0.4, 1, "Retrying in 1s (attempt 1)")]
    [InlineData(0.0, 2, "Retrying in 0s (attempt 2)")]
    [InlineData(-5.0, 2, "Retrying in 0s (attempt 2)")]
    public void RetryCountdown_ceils_and_clamps(double secondsAhead, int attempt, string expected) =>
        Assert.Equal(expected, TimeText.RetryCountdown(Now.AddSeconds(secondsAhead), Now, attempt));
}
