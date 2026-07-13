using Avalonia.Controls;
using Avalonia.Styling;
using Lattice.App.Views;

namespace Lattice.VisualTests;

/// <summary>
/// Shared, deterministic capture setup for the view under the visual gate. Keeping the
/// fixture (control factory, pixel size, warmup) in one place means the snapshot gate
/// (<see cref="FirstRunViewVisualTests"/>) and the calibration harness
/// (<see cref="CalibrationHarness"/>) capture the exact same bytes.
/// </summary>
internal static class VisualFixture
{
    public const int Width = 440;
    public const int Height = 320;

    public static Control NewView() => new FirstRunView { Width = Width, Height = Height };

    public static byte[] Capture(ThemeVariant variant)
    {
        VisualWarmup.Ensure();
        return VisualCapture.CapturePng(NewView, variant);
    }
}

internal static class VisualWarmup
{
    static bool done;

    /// <summary>
    /// Headless-Skia populates per-theme render/glyph caches on first use, so the very first
    /// capture of a theme in a process differs from later ones — calibration measured the dark
    /// theme's first render at ~2628 differing pixels (mean-error ~1.5) while light was bit-stable.
    /// Rendering each theme once up front (discarded) warms those caches so every measured capture
    /// is deterministic regardless of test execution order. Idempotent per process; runs on the
    /// Avalonia UI thread (its callers are [AvaloniaFact] bodies).
    /// </summary>
    public static void Ensure()
    {
        if (done)
        {
            return;
        }

        done = true;
        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            _ = VisualCapture.CapturePng(VisualFixture.NewView, variant);
        }
    }
}
