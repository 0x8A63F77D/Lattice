using System.Reflection;
using Xunit;

namespace Lattice.App.Tests;

/// <summary>
/// Structural pin for the issue #169 fix. The flake is a lost race for Avalonia's
/// process-global UI-thread claim: <c>HeadlessUnitTestSession</c> nulls it in
/// <c>Dispatcher.ResetBeforeUnitTests()</c> at the start of every <c>[AvaloniaFact]</c>
/// and re-claims it moments later on its own dispatch thread, while every
/// <c>AvaloniaObject</c> constructor claims it implicitly via
/// <c>Dispatcher.CurrentDispatcher</c>. Any test body running on another thread inside
/// that window can win the claim, after which the session's own
/// <c>Dispatcher.UIThread.VerifyAccess()</c> throws.
///
/// The fix is the absence of that other thread: no test in this assembly may run
/// concurrently with another. Nothing else in the suite would go red if the attribute
/// were dropped — the damage is a CI flake, not a local failure — so this asserts the
/// enforcement mechanism directly. See the attribute's comment in TestAppBuilder.cs for
/// the full mechanism and the rejected alternatives.
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
            "Lattice.App.Tests must carry [assembly: CollectionBehavior(DisableTestParallelization = true)]: "
            + "a test body on a parallel xunit worker thread can steal Avalonia's process-global UI-thread "
            + "claim while the headless session is rebuilding its application (issue #169).");
    }
}
