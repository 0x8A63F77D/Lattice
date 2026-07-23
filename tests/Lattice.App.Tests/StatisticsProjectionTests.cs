using Lattice.App.Charting;
using Lattice.Boinc.GuiRpc;
using Xunit;

namespace Lattice.App.Tests;

public class StatisticsProjectionTests
{
    private static readonly DateTimeOffset Day0 = new(2026, 7, 14, 0, 0, 0, TimeSpan.Zero);

    private static ProjectStatistics Stats(string url) =>
        new(url, [new DailyStatistics(Day0, 1, 1, 1, 1)]);

    [Fact]
    public void Blank_project_name_falls_back_to_the_master_url()
    {
        var projects = new[] { new Project("https://p.org/", "", 0, 0, 0, 5, 100, false, false) };
        var histories = StatisticsProjection.FromProjects(projects, [Stats("https://p.org/")]);
        Assert.Equal("https://p.org/", Assert.Single(histories).Name);
    }

    [Fact]
    public void A_named_project_keeps_its_name_and_daemon_ordinal()
    {
        var projects = new[]
        {
            new Project("https://a.org/", "Alpha", 0, 0, 0, 5, 100, false, false),
            new Project("https://b.org/", "Beta", 0, 0, 0, 9, 100, false, false),
        };
        var histories = StatisticsProjection.FromProjects(projects, [Stats("https://a.org/"), Stats("https://b.org/")]);
        Assert.Equal("Beta", histories[1].Name);
        Assert.Equal(1, histories[1].Ordinal); // ordinal = daemon-list index, not RAC rank
    }
}
