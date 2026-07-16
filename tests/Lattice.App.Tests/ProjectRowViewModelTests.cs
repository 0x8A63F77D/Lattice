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

    [Fact]
    public void AllSame_non_active_status_renders_on_all_hosts_suffix()
    {
        var g = ProjectRows.compute([Att(susp: true), Att(host: "host-b", susp: true)])[0];
        var row = ProjectRowViewModel.Parent(g, isAllHostsScope: true);

        Assert.Equal("Suspended on all hosts", row.StatusText);
        Assert.Equal(ProjectStatusKind.Suspended, row.StatusKind);
    }

    [Fact]
    public void Child_rows_render_credits_tasks_and_share_fractions()
    {
        var g = ProjectRows.compute(
            [Att(share: 50, rac: 100.4, total: 1000.4, tasks: 3),
             Att(host: "host-b", share: 100, rac: 200.4, total: 2000.4, tasks: 5)])[0];

        var childA = ProjectRowViewModel.Child(g, g.Attachments[0]); // host-a, share 50
        Assert.Equal("100", childA.AvgCreditText);
        Assert.Equal("1000", childA.TotalCreditText);
        Assert.Equal("3 tasks", childA.TasksText);
        Assert.Equal(0.5, childA.ShareFraction);

        var childB = ProjectRowViewModel.Child(g, g.Attachments[1]); // host-b, share 100
        Assert.Equal("200", childB.AvgCreditText);
        Assert.Equal("2000", childB.TotalCreditText);
        Assert.Equal("5 tasks", childB.TasksText);
        Assert.Equal(1.0, childB.ShareFraction);

        var uniform = ProjectRows.compute([Att(), Att(host: "host-b")])[0];
        var parent = ProjectRowViewModel.Parent(uniform, isAllHostsScope: true);
        Assert.Equal(1.0, parent.ShareFraction);
        Assert.True(parent.ShowShareBar);
    }

    [Fact]
    public void Zero_uniform_share_renders_an_empty_share_bar()
    {
        // Zero-share backup project attached on every host: uniform share 0
        // must render an EMPTY track ("unknown -> empty track" idiom from the
        // Tasks progress bar), not a full bar labelled "0".
        var g = ProjectRows.compute([Att(share: 0), Att(host: "host-b", share: 0)])[0];

        var parent = ProjectRowViewModel.Parent(g, isAllHostsScope: true);
        Assert.Equal("0", parent.ShareText);
        Assert.True(parent.ShowShareBar);
        Assert.Equal(0.0, parent.ShareFraction);

        // Child factory already guards maxShare > 0 — pin it.
        Assert.Equal(0.0, ProjectRowViewModel.Child(g, g.Attachments[0]).ShareFraction);
        Assert.Equal(0.0, ProjectRowViewModel.Child(g, g.Attachments[1]).ShareFraction);
    }

    [Fact]
    public void Parent_and_child_rows_carry_the_ProjectRows_sort_key()
    {
        // The exact shape hierarchical sort (issue #57) hangs the grid comparer off:
        // Parent's SortKey is ProjectRows.parentKey(g) (Level = ParentRow); Child's is
        // ProjectRows.childKey(g, a) (Level = ChildRow(host)). Pinned via equality against
        // the F# oracle itself, not a hand-reconstructed shape, so it tracks GroupSortKey
        // field changes automatically.
        var g = ProjectRows.compute([Att(rac: 100.4), Att(host: "host-b", rac: 100.4)])[0];

        var parentRow = ProjectRowViewModel.Parent(g, isAllHostsScope: true);
        Assert.Equal(ProjectRows.parentKey(g), parentRow.SortKey);
        Assert.Equal(RowLevel.ParentRow, parentRow.SortKey.Level);

        var childRow = ProjectRowViewModel.Child(g, g.Attachments[0]);
        Assert.Equal(ProjectRows.childKey(g, g.Attachments[0]), childRow.SortKey);
        Assert.Equal(RowLevel.NewChildRow(g.Attachments[0].HostName, g.Attachments[0].HostId), childRow.SortKey.Level);
    }

    [Fact]
    public void Single_host_scope_renders_plain_status_text()
    {
        // Scoped groups contain only the selected host's attachment; "on all
        // hosts" would falsely claim something about every configured host.
        var active = ProjectRows.compute([Att()])[0];
        Assert.Equal("Active",
            ProjectRowViewModel.Parent(active, isAllHostsScope: false).StatusText);

        var suspended = ProjectRows.compute([Att(susp: true)])[0];
        Assert.Equal("Suspended",
            ProjectRowViewModel.Parent(suspended, isAllHostsScope: false).StatusText);
    }
}
