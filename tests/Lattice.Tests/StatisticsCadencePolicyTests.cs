using Lattice.Core;
using Xunit;

namespace Lattice.Tests;

public class StatisticsCadencePolicyTests
{
    private static readonly DateTimeOffset Anchor = new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Fetch_is_due_before_the_first_fetch()
    {
        // A null last-fetch time is a fresh connection: connect/reconnect must always fetch.
        Assert.True(StatisticsCadencePolicy.ShouldRefresh(null, Anchor));
    }

    [Fact]
    public void Fetch_is_not_due_within_the_interval()
    {
        DateTimeOffset last = Anchor;
        DateTimeOffset now = Anchor + StatisticsCadencePolicy.RefreshInterval - TimeSpan.FromSeconds(1);
        Assert.False(StatisticsCadencePolicy.ShouldRefresh(last, now));
    }

    [Fact]
    public void Fetch_is_due_exactly_at_the_interval_boundary()
    {
        // Boundary is inclusive (>=): pins the comparison against a >-mutant.
        DateTimeOffset last = Anchor;
        DateTimeOffset now = Anchor + StatisticsCadencePolicy.RefreshInterval;
        Assert.True(StatisticsCadencePolicy.ShouldRefresh(last, now));
    }

    [Fact]
    public void Fetch_is_due_after_the_interval()
    {
        DateTimeOffset last = Anchor;
        DateTimeOffset now = Anchor + StatisticsCadencePolicy.RefreshInterval + TimeSpan.FromHours(1);
        Assert.True(StatisticsCadencePolicy.ShouldRefresh(last, now));
    }

    [Fact]
    public void Refresh_interval_is_six_hours()
    {
        Assert.Equal(TimeSpan.FromHours(6), StatisticsCadencePolicy.RefreshInterval);
    }
}
