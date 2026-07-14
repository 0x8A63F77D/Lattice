using Xunit;

namespace Lattice.VisualTests;

internal static class VisualGate
{
    /// <summary>
    /// Visual captures are macOS-baseline-only and run solely in the dedicated report-only
    /// visual-tests workflow. The normal cross-platform <c>dotnet test</c> (ci.yml) leaves
    /// this env unset, so these tests SKIP there instead of failing on non-macOS renders —
    /// no ci.yml change and no OS-detection fragility. The workflow sets it on macOS.
    /// </summary>
    public static void SkipUnlessEnabled() =>
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("LATTICE_RUN_VISUAL_TESTS") == "1",
            "Visual regression tests run only in the macOS visual-tests workflow (set LATTICE_RUN_VISUAL_TESTS=1).");
}
