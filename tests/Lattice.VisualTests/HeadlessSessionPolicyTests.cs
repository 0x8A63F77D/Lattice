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
}
