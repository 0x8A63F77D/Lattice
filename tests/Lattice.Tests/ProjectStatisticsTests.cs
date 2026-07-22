using System.Xml.Linq;
using Lattice.Boinc.GuiRpc;
using Xunit;

namespace Lattice.Tests;

public class ProjectStatisticsTests
{
    private static List<ProjectStatistics> Load(string fixture)
    {
        var reply = XElement.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", fixture)));
        return reply.Element("statistics")!.Elements("project_statistics").Select(ProjectStatistics.Parse).ToList();
    }

    [Fact]
    public void Parses_multi_project_history()
    {
        List<ProjectStatistics> stats = Load("get_statistics.xml");

        Assert.Equal(2, stats.Count);
        Assert.Equal("https://einsteinathome.org/", stats[0].MasterUrl);
        Assert.Equal("https://www.primegrid.com/", stats[1].MasterUrl);
        Assert.Equal(3, stats[0].Daily.Count);
        Assert.Equal(2, stats[1].Daily.Count);
    }

    [Fact]
    public void Parses_daily_fields_and_day_timestamp()
    {
        DailyStatistics first = Load("get_statistics.xml")[0].Daily[0];

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1750982400), first.Day);
        Assert.Equal(1000000.0, first.UserTotalCredit);
        Assert.Equal(3500.5, first.UserExpavgCredit);
        Assert.Equal(200000.0, first.HostTotalCredit);
        Assert.Equal(1200.25, first.HostExpavgCredit);
    }

    [Fact]
    public void Preserves_daily_order_within_a_project()
    {
        IReadOnlyList<DailyStatistics> days = Load("get_statistics.xml")[0].Daily;

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1750982400), days[0].Day);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1751068800), days[1].Day);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1751155200), days[2].Day);
        Assert.Equal(1009000.0, days[2].UserTotalCredit);
    }

    [Fact]
    public void Parses_single_project()
    {
        List<ProjectStatistics> stats = Load("get_statistics_single.xml");

        Assert.Single(stats);
        Assert.Equal("https://einsteinathome.org/", stats[0].MasterUrl);
        Assert.Equal(2, stats[0].Daily.Count);
    }

    [Fact]
    public void Parses_zero_projects_as_empty()
    {
        List<ProjectStatistics> stats = Load("get_statistics_empty.xml");

        Assert.Empty(stats);
    }

    [Fact]
    public void Tolerates_unknown_tags_reordering_and_missing_fields()
    {
        // The daemon's XML is not guaranteed strictly compliant and gains fields between
        // versions: unknown tags are ignored, field order is irrelevant, and a missing
        // credit field defaults to 0 (matching ParseHelpers.GetDouble).
        ProjectStatistics project = Assert.Single(Load("get_statistics_lenient.xml"));
        Assert.Equal("https://einsteinathome.org/", project.MasterUrl);

        DailyStatistics day = Assert.Single(project.Daily);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1750982400), day.Day);
        Assert.Equal(1000000.0, day.UserTotalCredit);
        Assert.Equal(3500.5, day.UserExpavgCredit);
        Assert.Equal(200000.0, day.HostTotalCredit);
        Assert.Equal(0.0, day.HostExpavgCredit); // absent in the fixture -> default
    }
}
