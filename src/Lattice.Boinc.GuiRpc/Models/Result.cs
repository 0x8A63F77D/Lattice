using System.Xml.Linq;

namespace Lattice.Boinc.GuiRpc;

/// <summary>Live execution state of a running task, nested inside a result.</summary>
/// <remarks>
/// The scheduler-state and wait flags feed the task-status mapping (BOINC's
/// <c>result_description</c>): they are what tells "Running" from "Waiting to
/// run" and name the memory/network waits. Written by the daemon inside
/// <c>&lt;active_task&gt;</c> (client/app.cpp, ACTIVE_TASK::write_gui); the
/// working-set-too-large flag ships under the legacy tag <c>&lt;too_large/&gt;</c>.
/// </remarks>
public sealed record ActiveTask(
    int ActiveTaskState,
    double FractionDone,
    double CurrentCpuTime,
    double ElapsedTime,
    // New fields default so existing positional constructions (tests, SampleHost)
    // keep compiling; Parse always sets them from the wire.
    SchedulerState SchedulerState = SchedulerState.Uninitialized,
    bool WssTooLarge = false,
    bool SwapTooLarge = false,
    bool NeedsShmem = false,
    bool WantNetwork = false)
{
    internal static ActiveTask Parse(XElement e)
    {
        double currentCpuTime = ParseHelpers.GetDouble(e, "current_cpu_time");
        double elapsedTime = ParseHelpers.GetDouble(e, "elapsed_time");
        // Old daemons report only CPU time; mirror the reference parser's
        // compatibility fallback (lib/gui_rpc_client_ops.cpp, RESULT::parse).
        if (currentCpuTime != 0 && elapsedTime == 0)
            elapsedTime = currentCpuTime;
        return new(
            ParseHelpers.GetInt(e, "active_task_state"),
            ParseHelpers.GetDouble(e, "fraction_done"),
            currentCpuTime,
            elapsedTime,
            (SchedulerState)ParseHelpers.GetInt(e, "scheduler_state"),
            // Legacy tag name: the daemon still emits <too_large/> for the
            // working-set (RSS) limit (client/app.cpp comment "backward compatibility").
            ParseHelpers.GetBool(e, "too_large"),
            ParseHelpers.GetBool(e, "swap_too_large"),
            ParseHelpers.GetBool(e, "needs_shmem"),
            ParseHelpers.GetBool(e, "want_network"));
    }
}

/// <summary>A task instance ("result" in BOINC vocabulary), from get_results or get_state.</summary>
public sealed record Result(
    string Name,
    string WorkunitName,
    string ProjectUrl,
    ResultState State,
    DateTimeOffset? ReportDeadline,
    bool ReadyToReport,
    bool SuspendedViaGui,
    double FinalCpuTime,
    double FinalElapsedTime,
    double EstimatedCpuTimeRemaining,
    int VersionNum,
    string PlanClass,
    int ExitStatus,
    ActiveTask? ActiveTask,
    // Fields the task-status mapping (BOINC's result_description) branches on,
    // beyond the run/upload state above. All default so existing positional
    // constructions (TestData.MakeResult, SampleHost) keep compiling; Parse
    // always sets them from the wire (client/result.cpp, RESULT::write_gui).
    // ProjectSuspendedViaGui: the owning project's user-suspend, stamped onto the
    // result by the daemon so no project join is needed.
    bool ProjectSuspendedViaGui = false,
    // Reporting terminal states for the FILES_UPLOADED/UPLOAD_FAILED branch.
    bool GotServerAck = false,
    // Scheduler backoff ("Postponed"): SchedulerWaitReason is daemon-supplied
    // free text (project-specific), shown verbatim — never branched on.
    bool SchedulerWait = false,
    string SchedulerWaitReason = "",
    bool NetworkWait = false,
    // Resource-usage string (e.g. "0.5 CPUs + 1 NVIDIA GPU"); read only to detect
    // GPU tasks (uses_gpu = contains "GPU") for the GPU-suspended branch.
    string Resources = "")
{
    internal static Result Parse(XElement e)
    {
        double finalCpuTime = ParseHelpers.GetDouble(e, "final_cpu_time");
        double finalElapsedTime = ParseHelpers.GetDouble(e, "final_elapsed_time");
        // Same compatibility fallback as above, for finished results.
        if (finalCpuTime != 0 && finalElapsedTime == 0)
            finalElapsedTime = finalCpuTime;
        return new(
            ParseHelpers.GetString(e, "name"),
            ParseHelpers.GetString(e, "wu_name"),
            ParseHelpers.GetString(e, "project_url"),
            (ResultState)ParseHelpers.GetInt(e, "state"),
            ParseHelpers.GetTimestamp(e, "report_deadline"),
            ParseHelpers.GetBool(e, "ready_to_report"),
            ParseHelpers.GetBool(e, "suspended_via_gui"),
            finalCpuTime,
            finalElapsedTime,
            ParseHelpers.GetDouble(e, "estimated_cpu_time_remaining"),
            ParseHelpers.GetInt(e, "version_num"),
            ParseHelpers.GetString(e, "plan_class"),
            ParseHelpers.GetInt(e, "exit_status"),
            e.Element("active_task") is { } at ? ActiveTask.Parse(at) : null,
            ParseHelpers.GetBool(e, "project_suspended_via_gui"),
            ParseHelpers.GetBool(e, "got_server_ack"),
            ParseHelpers.GetBool(e, "scheduler_wait"),
            ParseHelpers.GetString(e, "scheduler_wait_reason"),
            ParseHelpers.GetBool(e, "network_wait"),
            ParseHelpers.GetString(e, "resources"));
    }
}
