using Codeuctivity.ImageSharpCompare;

namespace Lattice.VisualTests;

/// <summary>
/// The metrics the visual gate is expressed in, computed on the same Codeuctivity engine the
/// tolerant comparer uses, so <see cref="TolerantPngComparer"/> and
/// <see cref="CalibrationHarness"/> measure the exact same thing.
/// </summary>
internal readonly record struct GateMetrics(double MeanError, int PixelErrorCount, double PixelErrorPercentage)
{
    /// <summary>
    /// Per-pixel color-shift tolerance (summed |ΔR|+|ΔG|+|ΔB|) below which a pixel is NOT
    /// counted as a difference. Calibration showed same-machine re-renders are not bit-identical:
    /// headless-Skia anti-aliasing nudges ~1–2% of pixels by a single LSB. Counting those (the
    /// default tolerance of 0 does) makes the pixel-error-count guard track AA noise instead of
    /// real change; this small band filters the noise while a genuine brush/layout change (whole
    /// pixels shifted by tens–hundreds) still registers.
    /// </summary>
    public const int PerPixelShiftTolerance = 16;

    /// <summary>Compares two PNGs. Throws <see cref="ImageSharpCompareException"/> on size mismatch.</summary>
    public static GateMetrics Compute(byte[] receivedPng, byte[] verifiedPng)
    {
        using var a = new MemoryStream(receivedPng);
        using var b = new MemoryStream(verifiedPng);
        var diff = ImageSharpCompare.CalcDiff(a, b, ResizeOption.DontResize, PerPixelShiftTolerance);
        return new GateMetrics(diff.MeanError, diff.PixelErrorCount, diff.PixelErrorPercentage);
    }
}
