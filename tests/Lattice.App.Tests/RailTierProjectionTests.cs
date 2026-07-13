using Lattice.App.Aggregation;
using Lattice.App.ViewModels;
using Xunit;

namespace Lattice.App.Tests;

public class RailTierProjectionTests
{
    [Theory]
    [InlineData(RailState.Unreachable)]
    [InlineData(RailState.AuthFailed)]
    [InlineData(RailState.Retrying)]
    public void Problem_states_are_attention(RailState state) =>
        Assert.Equal(RailTier.Attention, RailTierProjection.From(state));

    [Theory]
    [InlineData(RailState.Connected)]
    [InlineData(RailState.Connecting)]
    public void Live_states_are_healthy(RailState state) =>
        Assert.Equal(RailTier.Healthy, RailTierProjection.From(state));
}
