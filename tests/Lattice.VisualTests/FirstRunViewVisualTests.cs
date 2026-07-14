using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Xunit;

namespace Lattice.VisualTests;

/// <summary>
/// The ONE representative view under the visual gate (issue #82 asks for exactly one; coverage
/// is a non-goal for this calibration spike).
///
/// FirstRunView is the design-2d first-run empty state: a vector icon + two text weights + an
/// accent button — a representative mix of the anti-aliased surfaces that actually drive render
/// nondeterminism (glyph AA is the dominant variable per #81) — yet fully deterministic
/// (static-string content, no live timestamps/percentages) and outside the blast radius of both
/// open reworks: the data-grid fidelity PR #74 (Tasks/Projects/Transfers/Event log) and the shell
/// hosts-rail + host-management rework #26. Its only view-model binding is a command; a null
/// DataContext renders every visible element.
///
/// One method per theme (rather than Verify.Avalonia's IncludeThemeVariant) captures Light and
/// Dark through the shared <see cref="VisualFixture"/>, so the gate and the calibration harness
/// render byte-identically, and the tolerant PNG comparer is the sole authority (it always runs
/// and emits a diff mask on failure).
/// </summary>
[Trait("Category", "Visual")]
public class FirstRunViewVisualTests
{
    [AvaloniaFact]
    public Task Render_light() => VerifyTheme(ThemeVariant.Light);

    [AvaloniaFact]
    public Task Render_dark() => VerifyTheme(ThemeVariant.Dark);

    static Task VerifyTheme(ThemeVariant variant)
    {
        VisualGate.SkipUnlessEnabled();
        var png = VisualFixture.Capture(variant);
        return Verify(new MemoryStream(png), extension: "png");
    }
}
