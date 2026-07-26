using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using Lattice.Tests;
using Lattice.VisualTests;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

// Issue #169. Avalonia's UI-thread identity is a process-global, first-toucher-wins claim
// made implicitly by every AvaloniaObject constructor, and HeadlessUnitTestSession clears
// and re-claims it around each [AvaloniaFact] under the default PerTest isolation. A test
// body running on any other thread inside that window steals the claim and the session's
// next VerifyAccess throws "a different thread owns it". Serializing the assembly removes
// the concurrency the race needs. The full mechanism, evidence and rejected alternatives
// are documented on Lattice.App.Tests' copy of this attribute — keep the two in step.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Lattice.VisualTests;

/// <summary>
/// Headless app configuration for the visual-regression captures. Two things make this
/// different from <c>Lattice.App.Tests</c>'s builder, and both are load-bearing:
/// <list type="bullet">
///   <item><c>.UseSkia()</c> + <c>UseHeadlessDrawing = false</c> — the fake headless
///   drawing backend produces no pixels; only Skia does.</item>
///   <item>Inter is pinned as the default font family so glyph geometry does not depend
///   on the host's system font (San Francisco on macOS). This is test-render-path only;
///   the shipping app's font is unchanged (issue #82 / #81 open question 4).</item>
/// </list>
/// Headless <c>RenderScaling</c> defaults to 1.0; the capture size is therefore the
/// pinned control size in device pixels (asserted in <see cref="CalibrationHarness"/>).
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Lattice.App.App>()
            .UseSkia()
            .WithInterFont()
            .With(new FontManagerOptions { DefaultFamilyName = "fonts:Inter#Inter" })
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            // Issue #133: same reason as Lattice.App.Tests' builder. This assembly shows a real
            // ShellWindow too (MenuSeparatorVisualTests' hostrail case opens a rail row's menu),
            // so it needs the same guarantee that a rail row is realized. See HeadlessPaneMotion.
            .WithoutPaneWidthAnimation();
}
