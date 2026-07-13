using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using Lattice.VisualTests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

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
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
