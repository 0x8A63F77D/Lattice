using Lattice.App.Infrastructure;
using Xunit;

namespace Lattice.App.Tests;

public class ControlFailureSurfaceTests
{
    [Fact]
    public void Report_opens_with_the_given_texts()
    {
        var surface = new ControlFailureSurface();
        Assert.False(surface.IsOpen);

        surface.Report("Suspend failed", "host unreachable");

        Assert.True(surface.IsOpen);
        Assert.Equal("Suspend failed", surface.Title);
        Assert.Equal("host unreachable", surface.Message);
    }

    [Fact]
    public void Newer_report_replaces_the_older_one()
    {
        var surface = new ControlFailureSurface();
        surface.Report("Suspend failed", "first");
        surface.Report("Abort failed", "second");

        Assert.True(surface.IsOpen);
        Assert.Equal("Abort failed", surface.Title);
        Assert.Equal("second", surface.Message);
    }

    [Fact]
    public void Clear_closes_and_a_new_report_reopens()
    {
        var surface = new ControlFailureSurface();
        surface.Report("Suspend failed", "boom");
        surface.Clear();
        Assert.False(surface.IsOpen);

        surface.Report("Resume failed", "again");
        Assert.True(surface.IsOpen);
    }
}
