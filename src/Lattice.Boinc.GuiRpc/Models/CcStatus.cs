using System.Xml.Linq;

namespace Lattice.Boinc.GuiRpc;

/// <summary>
/// Cheap steady-state poll: run modes and suspend reasons (get_cc_status).
/// Per lane, the plain mode is the CURRENT (temp-aware) mode, the Perm mode is the
/// permanent setting, and the delay is the seconds remaining on a temporary override
/// (0 when none) — everything a "snoozed until" display needs.
/// </summary>
public sealed record CcStatus(
    RunMode TaskMode,
    RunMode GpuMode,
    RunMode NetworkMode,
    SuspendReason TaskSuspendReason,
    SuspendReason GpuSuspendReason,
    SuspendReason NetworkSuspendReason,
    RunMode TaskModePerm,
    double TaskModeDelaySeconds,
    RunMode GpuModePerm,
    double GpuModeDelaySeconds,
    RunMode NetworkModePerm,
    double NetworkModeDelaySeconds)
{
    internal static CcStatus Parse(XElement e) => new(
        (RunMode)ParseHelpers.GetInt(e, "task_mode"),
        (RunMode)ParseHelpers.GetInt(e, "gpu_mode"),
        (RunMode)ParseHelpers.GetInt(e, "network_mode"),
        (SuspendReason)ParseHelpers.GetInt(e, "task_suspend_reason"),
        (SuspendReason)ParseHelpers.GetInt(e, "gpu_suspend_reason"),
        (SuspendReason)ParseHelpers.GetInt(e, "network_suspend_reason"),
        (RunMode)ParseHelpers.GetInt(e, "task_mode_perm"),
        ParseHelpers.GetDouble(e, "task_mode_delay"),
        (RunMode)ParseHelpers.GetInt(e, "gpu_mode_perm"),
        ParseHelpers.GetDouble(e, "gpu_mode_delay"),
        (RunMode)ParseHelpers.GetInt(e, "network_mode_perm"),
        ParseHelpers.GetDouble(e, "network_mode_delay"));
}
