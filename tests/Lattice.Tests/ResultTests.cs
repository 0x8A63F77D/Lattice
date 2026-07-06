using System.Xml.Linq;
using Xunit;
using Lattice.Boinc.GuiRpc;

namespace Lattice.Tests;

public class ResultTests
{
    private static List<Result> Load()
    {
        var reply = XElement.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "get_results.xml")));
        return reply.Element("results")!.Elements("result").Select(Result.Parse).ToList();
    }

    [Fact]
    public void Parses_running_task_with_active_task()
    {
        Result r = Load()[0];
        Assert.Equal("h1_0437.60_O3aC01Cl1In0__O3MDFV2g_G34731_437.70Hz_19_1", r.Name);
        Assert.Equal("h1_0437.60_O3aC01Cl1In0__O3MDFV2g_G34731_437.70Hz_19", r.WorkunitName);
        Assert.Equal("https://einsteinathome.org/", r.ProjectUrl);
        Assert.Equal(ResultState.FilesDownloaded, r.State);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1752800000), r.ReportDeadline);
        Assert.False(r.ReadyToReport);
        Assert.False(r.SuspendedViaGui);
        Assert.Equal(19000.0, r.EstimatedCpuTimeRemaining);
        Assert.Equal(0.0, r.FinalElapsedTime);
        Assert.Equal(218, r.VersionNum);
        Assert.Equal("", r.PlanClass);
        Assert.NotNull(r.ActiveTask);
        Assert.Equal(0.421, r.ActiveTask!.FractionDone, precision: 6);
        Assert.Equal(3800.0, r.ActiveTask.ElapsedTime);
    }

    [Fact]
    public void Parses_finished_task_without_active_task()
    {
        Result r = Load()[1];
        Assert.Equal(ResultState.FilesUploaded, r.State);
        Assert.True(r.ReadyToReport);
        Assert.True(r.SuspendedViaGui);
        Assert.Null(r.ActiveTask);
        Assert.Equal(12000.5, r.FinalCpuTime);
        Assert.Equal(0.0, r.EstimatedCpuTimeRemaining);
        Assert.Equal(12000.5, r.FinalElapsedTime); // falls back to final_cpu_time (old-daemon compat)
        Assert.Equal(0, r.VersionNum);
        Assert.Equal("", r.PlanClass);
    }

    [Fact]
    public void Falls_back_to_cpu_time_when_elapsed_time_missing()
    {
        var e = XElement.Parse("""
            <result>
                <name>old_daemon_task</name>
                <final_cpu_time>100.5</final_cpu_time>
                <active_task>
                    <current_cpu_time>42.0</current_cpu_time>
                </active_task>
            </result>
            """);
        Result r = Result.Parse(e);
        Assert.Equal(100.5, r.FinalElapsedTime);
        Assert.Equal(42.0, r.ActiveTask!.ElapsedTime);
    }
}
