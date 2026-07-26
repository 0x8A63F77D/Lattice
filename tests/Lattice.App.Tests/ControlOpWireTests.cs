using Lattice.App.ViewModels;
using Microsoft.FSharp.Reflection;
using Xunit;
using AggProjectOp = Lattice.App.Aggregation.ProjectOp;
using AggTaskOp = Lattice.App.Aggregation.TaskOp;
using GuiProjectOp = Lattice.Boinc.GuiRpc.ProjectOp;
using GuiTaskOp = Lattice.Boinc.GuiRpc.TaskOp;

namespace Lattice.App.Tests;

/// <summary>
/// Gates for the policy-op → wire-op adapter (#192). The adapter exists so the
/// Tasks/Projects call paths take the policy op ALONE and derive the wire op:
/// that is what makes a classify/wire mispairing unrepresentable (it is a
/// compile error — <c>RunTaskOpAsync</c> has no wire-op parameter to mispair).
///
/// What these tests add on top of that: F# nullary DU cases surface in C# as
/// <c>Is*</c> discriminators, so the adapter's C# match cannot be
/// exhaustiveness-checked by the compiler the way an F# match would be. The
/// totality tests below enumerate the DU's union cases REFLECTIVELY rather than
/// listing them by hand, so adding an op case without mapping it fails here
/// instead of surviving to a runtime throw. The bijection tests close the other
/// direction: a new wire enum value no policy op can reach is also a red.
/// </summary>
public class ControlOpWireTests
{
    private static IReadOnlyList<T> UnionCasesOf<T>() =>
        [.. FSharpType.GetUnionCases(typeof(T), null)
            .Select(c => (T)FSharpValue.MakeUnion(c, [], null))];

    // --- totality: every policy op has a wire op ------------------------------

    [Fact]
    public void Every_task_policy_op_maps_to_a_wire_op()
    {
        var cases = UnionCasesOf<AggTaskOp>();
        Assert.NotEmpty(cases);

        // No try/catch: an unmapped case throws ArgumentOutOfRangeException out of
        // the adapter and fails the test naming the op, which is the diagnostic.
        var mapped = cases.Select(ControlOpWire.ToWire).ToArray();

        Assert.Equal(cases.Count, mapped.Distinct().Count());
    }

    [Fact]
    public void Every_project_policy_op_maps_to_a_wire_op()
    {
        var cases = UnionCasesOf<AggProjectOp>();
        Assert.NotEmpty(cases);

        var mapped = cases.Select(ControlOpWire.ToWire).ToArray();

        Assert.Equal(cases.Count, mapped.Distinct().Count());
    }

    // --- the other direction: no wire op is unreachable ------------------------

    [Fact]
    public void Every_task_wire_op_is_reachable_from_some_policy_op()
    {
        var reachable = UnionCasesOf<AggTaskOp>().Select(ControlOpWire.ToWire).ToHashSet();

        Assert.Equal(Enum.GetValues<GuiTaskOp>().ToHashSet(), reachable);
    }

    [Fact]
    public void Every_project_wire_op_is_reachable_from_some_policy_op()
    {
        var reachable = UnionCasesOf<AggProjectOp>().Select(ControlOpWire.ToWire).ToHashSet();

        Assert.Equal(Enum.GetValues<GuiProjectOp>().ToHashSet(), reachable);
    }

    // --- the mapping table itself ---------------------------------------------
    // Totality + bijection still admit a SWAP (suspend↔resume keeps both). These
    // pin the semantic content arm by arm; the VM wire-string tests
    // (TasksViewModelControlTests / ProjectsViewModelControlTests) pin the same
    // pairs end-to-end through the real control service.

    [Theory]
    [InlineData(nameof(AggTaskOp.TaskSuspend), GuiTaskOp.Suspend)]
    [InlineData(nameof(AggTaskOp.TaskResume), GuiTaskOp.Resume)]
    [InlineData(nameof(AggTaskOp.TaskAbort), GuiTaskOp.Abort)]
    public void Task_op_maps_to_its_wire_op(string policyCase, GuiTaskOp expected)
    {
        var op = UnionCasesOf<AggTaskOp>().Single(c => c.ToString() == policyCase);

        Assert.Equal(expected, ControlOpWire.ToWire(op));
    }

    [Theory]
    [InlineData(nameof(AggProjectOp.ProjectSuspend), GuiProjectOp.Suspend)]
    [InlineData(nameof(AggProjectOp.ProjectResume), GuiProjectOp.Resume)]
    [InlineData(nameof(AggProjectOp.ProjectUpdate), GuiProjectOp.Update)]
    [InlineData(nameof(AggProjectOp.ProjectDetach), GuiProjectOp.Detach)]
    public void Project_op_maps_to_its_wire_op(string policyCase, GuiProjectOp expected)
    {
        var op = UnionCasesOf<AggProjectOp>().Single(c => c.ToString() == policyCase);

        Assert.Equal(expected, ControlOpWire.ToWire(op));
    }
}
