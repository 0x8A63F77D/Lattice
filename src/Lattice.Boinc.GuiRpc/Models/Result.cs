using System.Xml.Linq;

namespace Lattice.Boinc.GuiRpc;

/// <summary>Live execution state of a running task, nested inside a result.</summary>
public sealed record ActiveTask(
    int ActiveTaskState,
    double FractionDone,
    double CurrentCpuTime,
    double ElapsedTime)
{
    internal static ActiveTask Parse(XElement e) => new(
        ParseHelpers.GetInt(e, "active_task_state"),
        ParseHelpers.GetDouble(e, "fraction_done"),
        ParseHelpers.GetDouble(e, "current_cpu_time"),
        ParseHelpers.GetDouble(e, "elapsed_time"));
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
    int ExitStatus,
    ActiveTask? ActiveTask)
{
    internal static Result Parse(XElement e) => new(
        ParseHelpers.GetString(e, "name"),
        ParseHelpers.GetString(e, "wu_name"),
        ParseHelpers.GetString(e, "project_url"),
        (ResultState)ParseHelpers.GetInt(e, "state"),
        ParseHelpers.GetTimestamp(e, "report_deadline"),
        ParseHelpers.GetBool(e, "ready_to_report"),
        ParseHelpers.GetBool(e, "suspended_via_gui"),
        ParseHelpers.GetDouble(e, "final_cpu_time"),
        ParseHelpers.GetInt(e, "exit_status"),
        e.Element("active_task") is { } at ? ActiveTask.Parse(at) : null);
}
