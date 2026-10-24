namespace Lattice.VisualTests;

/// <summary>
/// The metrics the visual gate is expressed in. <see cref="TolerantPngComparer"/> and
/// <see cref="CalibrationHarness"/> both call <see cref="Compute"/>, so they measure the exact
/// same thing.
/// </summary>
/// <remarks>
/// The definitions are those of <c>Codeuctivity.ImageSharpCompare</c> 4.1.390 <c>CalcDiff</c>,
/// the engine the gate was calibrated on (#82) and replaced by this SkiaSharp implementation
/// in #222 (its ImageSharp dependency failed the NuGet audit). A differential run over every
/// committed baseline showed identical counts, mean errors and gate verdicts; see the #222 PR.
/// Per pixel, the shift is |ΔR| + |ΔG| + |ΔB| (alpha ignored, 0..765). A pixel whose shift is
/// strictly greater than <see cref="PerPixelShiftTolerance"/> counts as an error, and only
/// those pixels' shifts add into the mean; pixels at or below the tolerance contribute nothing.
/// </remarks>
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

    /// <summary>
    /// Compares two PNGs. <see cref="MeanError"/> is the summed supra-tolerance shift divided by
    /// the pixel count; <see cref="PixelErrorPercentage"/> is the error count as a percentage of
    /// it. Throws <see cref="ImageSizeMismatchException"/> if the images differ in size.
    /// </summary>
    public static GateMetrics Compute(byte[] receivedPng, byte[] verifiedPng)
    {
        var (received, verified) = DecodeSameSize(receivedPng, verifiedPng);

        long absoluteError = 0;
        var pixelErrorCount = 0;
        for (var i = 0; i < received.Rgba.Length; i += 4)
        {
            var shift = Shift(received.Rgba, verified.Rgba, i);
            if (shift > PerPixelShiftTolerance)
            {
                absoluteError += shift;
                pixelErrorCount++;
            }
        }

        var quantity = received.Width * received.Height;
        return new GateMetrics(
            (double)absoluteError / quantity,
            pixelErrorCount,
            (double)pixelErrorCount / quantity * 100);
    }

    /// <summary>
    /// The diff-mask image for a failed comparison: each pixel holds the per-channel absolute
    /// difference (|ΔR|, |ΔG|, |ΔB|) of the two inputs, unfiltered by the shift tolerance, so
    /// black means identical and brighter means a bigger change. Same output as Codeuctivity's
    /// <c>CalcDiffMaskImage</c> at its default tolerance of 0, which is what the gate emitted
    /// before #222. Throws <see cref="ImageSizeMismatchException"/> if the images differ in size.
    /// </summary>
    public static PngRaster DiffMask(byte[] receivedPng, byte[] verifiedPng)
    {
        var (received, verified) = DecodeSameSize(receivedPng, verifiedPng);

        var mask = new byte[received.Rgba.Length];
        for (var i = 0; i < mask.Length; i += 4)
        {
            mask[i] = (byte)Math.Abs(received.Rgba[i] - verified.Rgba[i]);
            mask[i + 1] = (byte)Math.Abs(received.Rgba[i + 1] - verified.Rgba[i + 1]);
            mask[i + 2] = (byte)Math.Abs(received.Rgba[i + 2] - verified.Rgba[i + 2]);
            mask[i + 3] = byte.MaxValue;
        }

        return PngRaster.FromRgba(received.Width, received.Height, mask);
    }

    static (PngRaster Received, PngRaster Verified) DecodeSameSize(byte[] receivedPng, byte[] verifiedPng)
    {
        var received = PngRaster.Decode(receivedPng);
        var verified = PngRaster.Decode(verifiedPng);
        if (!received.HasSameSize(verified))
        {
            throw new ImageSizeMismatchException(received, verified);
        }

        return (received, verified);
    }

    // |ΔR| + |ΔG| + |ΔB| of the pixel starting at byte offset i; alpha (i + 3) is ignored.
    static int Shift(byte[] a, byte[] b, int i) =>
        Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]);
}

/// <summary>The two compared images differ in size — a structural change, never "within tolerance".</summary>
internal sealed class ImageSizeMismatchException(PngRaster received, PngRaster verified)
    : Exception(
        $"Size of images differ: received {received.Width}×{received.Height}, verified {verified.Width}×{verified.Height}.");
