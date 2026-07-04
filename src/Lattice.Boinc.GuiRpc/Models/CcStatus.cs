using System.Xml.Linq;

namespace Lattice.Boinc.GuiRpc;

/// <summary>Cheap steady-state poll: run modes and suspend reasons (get_cc_status).</summary>
public sealed record CcStatus(
    RunMode TaskMode,
    RunMode GpuMode,
    RunMode NetworkMode,
    SuspendReason TaskSuspendReason,
    SuspendReason GpuSuspendReason,
    SuspendReason NetworkSuspendReason)
{
    internal static CcStatus Parse(XElement e) => new(
        (RunMode)ParseHelpers.GetInt(e, "task_mode"),
        (RunMode)ParseHelpers.GetInt(e, "gpu_mode"),
        (RunMode)ParseHelpers.GetInt(e, "network_mode"),
        (SuspendReason)ParseHelpers.GetInt(e, "task_suspend_reason"),
        (SuspendReason)ParseHelpers.GetInt(e, "gpu_suspend_reason"),
        (SuspendReason)ParseHelpers.GetInt(e, "network_suspend_reason"));
}
