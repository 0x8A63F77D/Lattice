using Lattice.App.Aggregation;
using Lattice.App.ViewModels;
using Xunit;

namespace Lattice.App.Tests;

public class ProjectRowViewModelTests
{
    private static ProjectAttachment Att(
        string url = "http://p1/", string name = "P1", string host = "host-a",
        Guid? hostId = null, bool susp = false, bool noNew = false,
        double share = 100, double rac = 1234.567, double total = 99999.9, int tasks = 3) =>
        new(url, name, hostId ?? Guid.NewGuid(), host, tasks, share, rac, total, susp, noNew);

    [Fact]
    public void Parent_row_renders_aggregates()
    {
        var g = ProjectRows.compute([Att(rac: 100.4), Att(host: "host-b", rac: 100.4)])[0];
        var row = ProjectRowViewModel.Parent(g, isAllHostsScope: true);

        Assert.True(row.IsParent);
        Assert.Equal("P1", row.Name);
        Assert.Equal("http://p1/", row.MasterUrl);
        Assert.Equal("2", row.HostsText);
        Assert.Equal("201", row.AvgCreditText);        // sum, rounded, invariant
        Assert.Equal(ProjectRowKey.NewParentKey("http://p1/"), row.Key); // DU structural equality
    }

    [Fact]
    public void Varies_share_renders_range_on_parent_and_bars_on_children_only()
    {
        var g = ProjectRows.compute([Att(share: 50), Att(host: "host-b", share: 100)])[0];
        var parent = ProjectRowViewModel.Parent(g, isAllHostsScope: true);
        Assert.Equal("Varies · 50–100", parent.ShareText);
        Assert.False(parent.ShowShareBar);

        var child = ProjectRowViewModel.Child(g, g.Attachments[0]);
        Assert.True(child.ShowShareBar);
        Assert.False(child.IsParent);
        Assert.Equal("host-a", child.Name);
    }

    [Fact]
    public void Status_tiers_render_per_design()
    {
        var same = ProjectRows.compute([Att()])[0];
        Assert.Equal("Active on all hosts",
            ProjectRowViewModel.Parent(same, true).StatusText);

        var dev = ProjectRows.compute([Att(susp: true), Att(host: "host-b")])[0];
        Assert.Equal("Suspended · 1/2 hosts",
            ProjectRowViewModel.Parent(dev, true).StatusText);

        var mixed = ProjectRows.compute([Att(susp: true), Att(host: "host-b", noNew: true)])[0];
        Assert.Equal("Mixed · 1 suspended · 1 no new tasks",
            ProjectRowViewModel.Parent(mixed, true).StatusText);
    }
}
