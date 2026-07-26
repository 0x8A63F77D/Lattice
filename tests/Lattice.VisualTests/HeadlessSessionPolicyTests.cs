using System.Reflection;
using Xunit;

namespace Lattice.VisualTests;

/// <summary>
/// Structural pin for the issue #169 fix, mirroring the one in Lattice.App.Tests. Deliberately
/// NOT behind <see cref="VisualGate"/>: the visual captures skip outside the macOS visual-tests
/// workflow, but the parallelization guard must be asserted on every cross-platform CI run —
/// that is where the flake it prevents lives.
/// </summary>
public class HeadlessSessionPolicyTests
{
    [Fact]
    public void The_headless_test_assembly_runs_strictly_serially()
    {
        CollectionBehaviorAttribute? behavior = typeof(TestAppBuilder).Assembly
            .GetCustomAttribute<CollectionBehaviorAttribute>();

        Assert.True(
            behavior?.DisableTestParallelization,
            "Lattice.VisualTests must carry [assembly: CollectionBehavior(DisableTestParallelization = true)]: "
            + "a test body on a parallel xunit worker thread can steal Avalonia's process-global UI-thread "
            + "claim while the headless session is rebuilding its application (issue #169).");
    }

    // Issue #133. This assembly's builder must also opt out of the SplitView pane's width
    // animation: MenuSeparatorVisualTests' hostrail case shows a real ShellWindow and opens a
    // rail ROW's menu, so it needs the row realized. Measured 2 failures in 18 full-solution
    // runs before the opt-out, finding 0 dividers because the row never existed. Not behind
    // VisualGate for the same reason as the test above — the wiring is what must not be lost.
    [Fact]
    public void The_headless_test_assembly_opts_out_of_the_pane_width_animation()
    {
        _ = TestAppBuilder.BuildAvaloniaApp();

        Assert.True(
            Lattice.Tests.HeadlessPaneMotion.IsInstalled,
            "Lattice.VisualTests' app builder must call .WithoutPaneWidthAnimation(): the pane's "
            + "animated width otherwise latches an empty effective viewport on the host rail's "
            + "VirtualizingStackPanel, and the rail row a capture needs is never realized (issue #133).");
    }
}
