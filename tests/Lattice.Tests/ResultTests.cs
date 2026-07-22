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
        // <scheduler_state>2</scheduler_state> in the fixture → Scheduled; the
        // wait flags are absent, so they default false.
        Assert.Equal(SchedulerState.Scheduled, r.ActiveTask.SchedulerState);
        Assert.False(r.ActiveTask.WssTooLarge);
        Assert.False(r.ActiveTask.SwapTooLarge);
        Assert.False(r.ActiveTask.NeedsShmem);
        Assert.False(r.ActiveTask.WantNetwork);
        // Result-level status flags absent in the fixture → defaults.
        Assert.False(r.ProjectSuspendedViaGui);
        Assert.False(r.GotServerAck);
        Assert.False(r.SchedulerWait);
        Assert.Equal("", r.SchedulerWaitReason);
        Assert.False(r.NetworkWait);
        Assert.Equal("", r.Resources);
    }

    [Fact]
    public void Parses_active_task_status_flags_including_legacy_too_large_tag()
    {
        // The daemon writes the RSS-limit flag under the legacy tag <too_large/>
        // (client/app.cpp), NOT <wss_too_large/> — the parser must read that name.
        var e = XElement.Parse("""
            <result>
                <name>mem_waiting</name>
                <state>2</state>
                <active_task>
                    <active_task_state>9</active_task_state>
                    <scheduler_state>1</scheduler_state>
                    <too_large/>
                    <swap_too_large>1</swap_too_large>
                    <needs_shmem/>
                    <want_network/>
                </active_task>
            </result>
            """);
        Result r = Result.Parse(e);
        Assert.Equal(SchedulerState.Preempted, r.ActiveTask!.SchedulerState);
        Assert.True(r.ActiveTask.WssTooLarge);
        Assert.True(r.ActiveTask.SwapTooLarge);
        Assert.True(r.ActiveTask.NeedsShmem);
        Assert.True(r.ActiveTask.WantNetwork);
    }

    [Fact]
    public void Parses_result_level_status_flags()
    {
        var e = XElement.Parse("""
            <result>
                <name>postponed_gpu</name>
                <state>2</state>
                <project_suspended_via_gui/>
                <got_server_ack/>
                <scheduler_wait/>
                <scheduler_wait_reason>project backoff</scheduler_wait_reason>
                <network_wait/>
                <resources>0.5 CPUs + 1 NVIDIA GPU (device 0)</resources>
            </result>
            """);
        Result r = Result.Parse(e);
        Assert.True(r.ProjectSuspendedViaGui);
        Assert.True(r.GotServerAck);
        Assert.True(r.SchedulerWait);
        Assert.Equal("project backoff", r.SchedulerWaitReason);
        Assert.True(r.NetworkWait);
        Assert.Equal("0.5 CPUs + 1 NVIDIA GPU (device 0)", r.Resources);
        Assert.Null(r.ActiveTask);
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
