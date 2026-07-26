using AggProjectOp = Lattice.App.Aggregation.ProjectOp;
using AggTaskOp = Lattice.App.Aggregation.TaskOp;
using GuiProjectOp = Lattice.Boinc.GuiRpc.ProjectOp;
using GuiTaskOp = Lattice.Boinc.GuiRpc.TaskOp;

namespace Lattice.App.ViewModels;

/// <summary>
/// The single App.Aggregation → GuiRpc adapter for M3 task and project control
/// ops (#192). Before this existed, each command handed the confirmation policy
/// an <c>Agg*Op</c> and the control service a <c>Gui*Op</c> as two independent
/// arguments, so a mispairing was expressible at 7 call sites: pairing
/// <c>TaskSuspend</c> with <c>GuiTaskOp.Abort</c> classifies as <c>Instant</c>
/// (no confirmation dialog) while the wire aborts the task — computed work lost
/// silently. The op call paths now take the policy op ALONE and derive the wire
/// op here, which is what makes the mispair unrepresentable rather than merely
/// asserted-against.
///
/// Placement: <c>App.Aggregation</c> is GuiRpc-free by module rule and
/// <c>Lattice.Core</c> cannot reference <c>App.Aggregation</c> (dependency
/// direction), so the App/Core seam is the one place the two namespaces
/// legitimately meet — the arrangement adjudicated on #139, which also rules out
/// interposing a mirror-type layer. This generalizes the ModeLane adapter
/// (<c>HostRailItemViewModel.ToGuiLane</c>) that already sits at that seam.
///
/// Totality: F# nullary DU cases surface in C# as <c>Is*</c> discriminators, so
/// C# cannot exhaustiveness-check the match the way F# would; the trailing throw
/// is the total guard for a value a well-formed DU never produces (the
/// C#-over-F#-DU analogue of the CS8524 discipline, same as <c>ToGuiLane</c>).
/// The compile-time gap is closed by test instead: <c>ControlOpWireTests</c>
/// enumerates the DU's union cases reflectively and fails on any case this
/// adapter does not map, so adding an op without mapping it is caught by the
/// machine, not by review discipline.
/// </summary>
internal static class ControlOpWire
{
    internal static GuiTaskOp ToWire(AggTaskOp op) =>
        op.IsTaskSuspend ? GuiTaskOp.Suspend
        : op.IsTaskResume ? GuiTaskOp.Resume
        : op.IsTaskAbort ? GuiTaskOp.Abort
        : throw new ArgumentOutOfRangeException(nameof(op), op, "unmapped task control op");

    internal static GuiProjectOp ToWire(AggProjectOp op) =>
        op.IsProjectSuspend ? GuiProjectOp.Suspend
        : op.IsProjectResume ? GuiProjectOp.Resume
        : op.IsProjectUpdate ? GuiProjectOp.Update
        : op.IsProjectDetach ? GuiProjectOp.Detach
        : throw new ArgumentOutOfRangeException(nameof(op), op, "unmapped project control op");
}
