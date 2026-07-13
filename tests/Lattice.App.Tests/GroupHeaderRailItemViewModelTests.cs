using Lattice.App.Aggregation;
using Lattice.App.Localization;
using Lattice.App.ViewModels;
using Xunit;

namespace Lattice.App.Tests;

public class GroupHeaderRailItemViewModelTests
{
    [Fact]
    public void Attention_header_formats_count_and_is_not_collapsible()
    {
        var vm = new GroupHeaderRailItemViewModel(RailTier.Attention, count: 3, expanded: true);
        Assert.Equal(string.Format(Strings.RailGroupAttentionFmt, 3), vm.Text);
        Assert.False(vm.IsCollapsible);   // Attention is always expanded
    }

    [Fact]
    public void Healthy_header_is_collapsible_and_raises_toggle_with_its_tier()
    {
        var vm = new GroupHeaderRailItemViewModel(RailTier.Healthy, count: 35, expanded: false);
        Assert.Equal(string.Format(Strings.RailGroupHealthyFmt, 35), vm.Text);
        Assert.True(vm.IsCollapsible);
        Assert.False(vm.Expanded);

        RailTier? toggled = null;
        vm.ToggleRequested += (_, tier) => toggled = tier;
        vm.ToggleCommand.Execute(null);
        Assert.Equal(RailTier.Healthy, toggled);
    }
}
