using System.ComponentModel;
using Lattice.App.Aggregation;
using Lattice.App.Infrastructure;
using Lattice.App.ViewModels;
using Xunit;

namespace Lattice.App.Tests;

public class ProjectSortDescriptionTests
{
    private static ProjectAttachment Att(
        string url = "http://p1/", string name = "P1", string host = "host-a",
        Guid? hostId = null, bool susp = false, bool noNew = false,
        double share = 100, double rac = 1234.567, double total = 99999.9, int tasks = 3) =>
        new(url, name, hostId ?? Guid.NewGuid(), host, tasks, share, rac, total, susp, noNew);

    // All 13 ProjectSort values (DefaultSort + 6 columns x 2 directions) — mirrors
    // the F# side's allProjectSorts in ProjectRowsTests.fs.
    private static readonly ProjectSortColumn[] AllColumns =
    [
        ProjectSortColumn.ByName, ProjectSortColumn.ByHostCount, ProjectSortColumn.ByShare,
        ProjectSortColumn.ByAvgCredit, ProjectSortColumn.ByTotalCredit, ProjectSortColumn.ByStatus,
    ];

    private static IEnumerable<ProjectSort> AllSorts()
    {
        yield return ProjectSort.DefaultSort;
        foreach (var column in AllColumns)
        {
            yield return ProjectSort.NewColumnSort(column, SortDirection.Ascending);
            yield return ProjectSort.NewColumnSort(column, SortDirection.Descending);
        }
    }

    private static string ExpectedPropertyPath(ProjectSortColumn column) =>
        column.Tag switch
        {
            ProjectSortColumn.Tags.ByName => nameof(ProjectSortColumn.ByName),
            ProjectSortColumn.Tags.ByHostCount => nameof(ProjectSortColumn.ByHostCount),
            ProjectSortColumn.Tags.ByShare => nameof(ProjectSortColumn.ByShare),
            ProjectSortColumn.Tags.ByAvgCredit => nameof(ProjectSortColumn.ByAvgCredit),
            ProjectSortColumn.Tags.ByTotalCredit => nameof(ProjectSortColumn.ByTotalCredit),
            ProjectSortColumn.Tags.ByStatus => nameof(ProjectSortColumn.ByStatus),
            _ => throw new InvalidOperationException("unreachable: closed DU"),
        };

    [Fact]
    public void PropertyPath_and_Direction_match_the_13_value_table()
    {
        foreach (var sort in AllSorts())
        {
            var description = ProjectSortDescription.For(sort);
            if (sort is ProjectSort.ColumnSort cs)
            {
                Assert.Equal(ExpectedPropertyPath(cs.column), description.PropertyPath);
                Assert.Equal(
                    cs.direction.IsDescending ? ListSortDirection.Descending : ListSortDirection.Ascending,
                    description.Direction);
            }
            else
            {
                Assert.Null(description.PropertyPath);
                Assert.Equal(ListSortDirection.Ascending, description.Direction);
            }
        }
    }

    [Fact]
    public void DefaultSort_has_no_property_path()
    {
        var description = ProjectSortDescription.For(ProjectSort.DefaultSort);
        Assert.False(description.HasPropertyPath);
    }

    [Fact]
    public void ColumnSort_has_a_property_path()
    {
        var description =
            ProjectSortDescription.For(ProjectSort.NewColumnSort(ProjectSortColumn.ByName, SortDirection.Ascending));
        Assert.True(description.HasPropertyPath);
    }

    [Fact]
    public void SwitchSortDirection_flips_ColumnSort_and_preserves_the_column()
    {
        var ascending =
            ProjectSortDescription.For(
                ProjectSort.NewColumnSort(ProjectSortColumn.ByTotalCredit, SortDirection.Ascending));
        var flipped = ascending.SwitchSortDirection();

        Assert.Equal(ListSortDirection.Descending, flipped.Direction);
        Assert.Equal(ascending.PropertyPath, flipped.PropertyPath);

        var flippedBack = flipped.SwitchSortDirection();
        Assert.Equal(ListSortDirection.Ascending, flippedBack.Direction);
        Assert.Equal(ascending.PropertyPath, flippedBack.PropertyPath);
    }

    [Fact]
    public void SwitchSortDirection_on_DefaultSort_returns_itself()
    {
        var description = ProjectSortDescription.For(ProjectSort.DefaultSort);
        Assert.Same(description, description.SwitchSortDirection());
    }

    [Fact]
    public void Comparer_matches_the_F_sharp_oracle_including_sign_symmetry()
    {
        var g = ProjectRows.compute(
            [Att(host: "host-a", rac: 5), Att(url: "http://p1/", host: "host-b", rac: 5, susp: true)])[0];
        var other = ProjectRows.compute([Att(url: "http://p2/", host: "host-a", rac: 9)])[0];

        var parentRow = new ProjectRow(
            ProjectRowKey.NewParentKey(g.MasterUrl), ProjectRowViewModel.Parent(g, isAllHostsScope: true));
        var childRow = new ProjectRow(
            ProjectRowKey.NewChildKey(g.MasterUrl, g.Attachments[0].HostId),
            ProjectRowViewModel.Child(g, g.Attachments[0]));
        var otherParentRow = new ProjectRow(
            ProjectRowKey.NewParentKey(other.MasterUrl), ProjectRowViewModel.Parent(other, isAllHostsScope: true));

        ProjectRow[] rows = [parentRow, childRow, otherParentRow];

        foreach (var sort in AllSorts())
        {
            var comparer = ProjectSortDescription.For(sort).Comparer;
            foreach (var x in rows)
            {
                foreach (var y in rows)
                {
                    var expected = ProjectRows.compareRows(sort, x.Data.SortKey, y.Data.SortKey);
                    Assert.Equal(expected, comparer.Compare(x, y));
                    Assert.Equal(Math.Sign(expected), -Math.Sign(comparer.Compare(y, x)));
                }
            }
        }
    }

    [Fact]
    public void Comparer_throws_on_a_non_ProjectRow_item()
    {
        var comparer = ProjectSortDescription.For(ProjectSort.DefaultSort).Comparer;
        Assert.Throws<InvalidOperationException>(() => comparer.Compare("not a row", "also not a row"));
    }
}
