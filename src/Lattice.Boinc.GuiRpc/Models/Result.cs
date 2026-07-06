using System.Xml.Linq;

namespace Lattice.Boinc.GuiRpc;

/// <summary>Live execution state of a running task, nested inside a result.</summary>
public sealed record ActiveTask(
    int ActiveTaskState,
    double FractionDone,
    double CurrentCpuTime,
    double ElapsedTime)
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
            elapsedTime);
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
    ActiveTask? ActiveTask)
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
            e.Element("active_task") is { } at ? ActiveTask.Parse(at) : null);
    }
}
