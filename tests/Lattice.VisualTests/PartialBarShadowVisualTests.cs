using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Linq;
using FluentAvalonia.UI.Controls;
using Xunit;

namespace Lattice.VisualTests;

/// <summary>
/// Rendered end-state gate for the partial-bar card's elevation (#119, Codex P2 on PR #134):
/// the wiring assertion in <c>HeaderFrameGapTests</c> proves the template part CARRIES a
/// non-empty <c>BoxShadow</c>; this proves the shadow actually DARKENS pixels outside the
/// card — a clipped, non-rendered, or empty-token shadow passes the wiring check but fails
/// here.
///
/// Probing posture (PixelProbe canon): the card's own warning FILL over same-tinted rows is
/// the canonical un-probeable pair, and asserting exact colours inside a blur gradient would
/// be runner-fragile — so this test does NEITHER. It renders the open card over a solid,
/// known backdrop and asserts a RELATIVE delta: the backdrop just below the card's bottom
/// edge (inside the shadow's key-offset band) is darker than the same backdrop far below
/// (clear of the ~24px shadow extent: 8 offset + 16 blur). Inequality-with-margin over flat
/// regions is deterministic in headless Skia; shadow STRENGTH/feel stays the owner's gate.
///
/// NOT env-gated — same cross-runner rationale as <see cref="SelectionTintVisualTests"/>.
/// </summary>
[Trait("Category", "Visual")]
public class PartialBarShadowVisualTests
{
    [AvaloniaFact]
    public void Open_card_casts_a_rendered_shadow_light() => AssertShadowRenders(ThemeVariant.Light, Color.Parse("#FFFFFF"));

    [AvaloniaFact]
    public void Open_card_casts_a_rendered_shadow_dark() => AssertShadowRenders(ThemeVariant.Dark, Color.Parse("#292929"));

    private static void AssertShadowRenders(ThemeVariant variant, Color backdrop)
    {
        // Warm the theme's Skia caches once (first render of a variant differs); discard the
        // frame immediately (VisualCapture's Avalonia #19611 note).
        var warm = Render(variant, backdrop, out _, out var warmFrame);
        warmFrame.Dispose();
        warm.Close();

        var window = Render(variant, backdrop, out var bar, out var frame);
        using (frame)
        {
            var buf = PixelBuffer.From(frame);
            var origin = bar.TranslatePoint(new Point(0, 0), window)!.Value;
            int centerX = (int)(origin.X + bar.Bounds.Width / 2);
            int bottom = (int)(origin.Y + bar.Bounds.Height);

            // 6px below the card: inside the key shadow's offset band (offset 8, blur 16).
            var near = buf.Rgb(centerX, bottom + 6);
            // 64px below: clear of the ~24px shadow extent — plain backdrop.
            var far = buf.Rgb(centerX, bottom + 64);

            window.Close();

            // Sanity: the far sample really is the untouched backdrop (a mispositioned card
            // would silently turn both samples into card pixels and fake the delta).
            int farDelta = Math.Abs(far.r - backdrop.R) + Math.Abs(far.g - backdrop.G) + Math.Abs(far.b - backdrop.B);
            Assert.True(farDelta <= PixelProbe.Tolerance,
                $"[{variant}] far sample should be the plain backdrop {backdrop}, sampled {PixelProbe.Hex(far)}");

            // The rendered shadow must darken the near band. Margin 3 (not the 8-unit colour
            // tolerance): on the dark backdrop the max possible darkening is small in absolute
            // RGB (#292929 scaled by ~0.3 alpha ≈ 12), but flat-region rendering is
            // deterministic here, so a small strict margin is stable.
            int darkening = (far.r - near.r) + (far.g - near.g) + (far.b - near.b);
            Assert.True(darkening > 3,
                $"[{variant}] the card must cast a rendered shadow below its bottom edge: near {PixelProbe.Hex(near)} vs far {PixelProbe.Hex(far)} (darkening {darkening})");
        }
    }

    private static Window Render(ThemeVariant variant, Color backdrop, out FAInfoBar bar, out Bitmap frame)
    {
        Application.Current!.RequestedThemeVariant = variant;

        var infoBar = new FAInfoBar
        {
            IsOpen = true,
            Severity = FAInfoBarSeverity.Warning,
            Title = "Partial results.",
            Message = "1 of 4 hosts aren't reachable — tasks below cover 3 hosts.",
        };
        infoBar.Classes.Add("partialBar");

        var window = new Window
        {
            Width = 900,
            Height = 300,
            Background = new SolidColorBrush(backdrop),
            Content = new Panel { Children = { infoBar } },
        };
        window.Show();
        window.Measure(new Size(900, 300));
        window.Arrange(new Rect(0, 0, 900, 300));
        Dispatcher.UIThread.RunJobs();

        bar = infoBar;
        frame = window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("No rendered frame captured.");
        return window;
    }
}
