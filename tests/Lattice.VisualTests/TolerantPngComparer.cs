using System.Security.Cryptography;
using Codeuctivity.ImageSharpCompare;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using VerifyTests;
// Both Codeuctivity and Verify define a CompareResult; the comparer returns Verify's.
using CompareResult = VerifyTests.CompareResult;

namespace Lattice.VisualTests;

/// <summary>
/// The tolerant PNG comparer for the visual gate. Verify's default PNG comparison is
/// hash/byte-exact and would fail on anti-aliasing jitter, so we replace it.
///
/// Issue #81 named "Verify.ImageSharp.Compare (Codeuctivity)". Reading that wrapper's
/// source, its registered comparer applies a single <c>AbsoluteError</c> threshold and
/// emits no diff image — it cannot express the dual-tolerance shape #82 asks for. So we
/// register our own comparer directly on the same engine (<c>Codeuctivity.ImageSharpCompare</c>),
/// implementing the two-part gate — a mean-error band (absorbs AA jitter) plus a
/// pixel-error-count guard (so a small localized change is not averaged away) — and
/// emitting a diff-mask PNG artifact on failure. Both metrics use
/// <see cref="GateMetrics.PerPixelShiftTolerance"/> so single-LSB AA noise is not counted.
/// </summary>
internal static class TolerantPngComparer
{
    // CALIBRATED tolerances — set from the #82 calibration measurements (comment 2026-07-13),
    // now that the gate is ENFORCING (visual-tests.yml no longer continue-on-error). Both sit
    // comfortably above the observed cross-machine drift and far below a real change, per the
    // Stryker #77 "calibrate, then flip" cadence:
    //   - same-runner run-to-run (after cache warmup): meanError 0, supra-tolerance count 0.
    //   - dev-Mac vs CI-Mac drift (the floor the gate must never trip on): meanError ≤ 0.0111,
    //     supra-tolerance count ≤ 38 px (of 140,800), across both themes and two macOS versions.
    //   - a real change (the #82 falsification demo): meanError ~12, ~7,000 px — ~3 orders louder.
    // MeanErrorThreshold 1.0 is the top of the owner's stated 0.1–1.0 band: ~90x above the ≤0.011
    // drift, ~12x below a real change — the upper edge maximizes margin against future runner-image
    // drift while staying well under any genuine regression. PixelErrorCountGuard 400 is "low
    // hundreds": ~10x above the ≤38-px drift, ~17x below the ~7,000-px real-change signal.
    public const double MeanErrorThreshold = 1.0;   // mean abs error per pixel, summed over RGB (0..765)
    public const int PixelErrorCountGuard = 400;    // max pixels differing by > per-pixel shift tolerance

    public static void Register() =>
        VerifierSettings.RegisterStreamComparer("png", (received, verified, _) => Compare(received, verified));

    static Task<CompareResult> Compare(Stream received, Stream verified)
    {
        // GateMetrics.Compute consumes its streams and we want a second read for the diff-mask
        // artifact, so buffer both up front.
        var receivedBytes = ReadAll(received);
        var verifiedBytes = ReadAll(verified);

        GateMetrics diff;
        try
        {
            diff = GateMetrics.Compute(receivedBytes, verifiedBytes);
        }
        catch (ImageSharpCompareException exception)
        {
            // Dimension mismatch — a structural change, never "within tolerance".
            return Task.FromResult(CompareResult.NotEqual(exception.Message));
        }

        var withinMean = diff.MeanError <= MeanErrorThreshold;
        var withinCount = diff.PixelErrorCount <= PixelErrorCountGuard;
        if (withinMean && withinCount)
        {
            return Task.FromResult(CompareResult.Equal);
        }

        var maskPath = TryWriteDiffMask(receivedBytes, verifiedBytes);
        var message =
            $"""
             Visual diff exceeded tolerance:
               meanError       = {diff.MeanError:F4} (threshold {MeanErrorThreshold})
               pixelErrorCount = {diff.PixelErrorCount} (guard {PixelErrorCountGuard}, shift tol {GateMetrics.PerPixelShiftTolerance})
               pixelErrorPct   = {diff.PixelErrorPercentage:F4}%
             {(maskPath is null ? "(diff mask not written)" : $"diff mask: {maskPath}")}
             """;
        return Task.FromResult(CompareResult.NotEqual(message));
    }

    static string? TryWriteDiffMask(byte[] receivedBytes, byte[] verifiedBytes)
    {
        try
        {
            using var a = new MemoryStream(receivedBytes);
            using var b = new MemoryStream(verifiedBytes);
            using Image mask = ImageSharpCompare.CalcDiffMaskImage(a, b);
            var dir = VisualPaths.ArtifactsDir;
            Directory.CreateDirectory(dir);
            // Name by a stable hash of the baseline so the two theme masks never collide
            // and re-runs overwrite rather than accumulate.
            var hash = Convert.ToHexString(SHA256.HashData(verifiedBytes))[..8].ToLowerInvariant();
            var path = Path.Combine(dir, $"diff-{hash}.png");
            mask.Save(path, new PngEncoder());
            return path;
        }
        catch (Exception)
        {
            return null;
        }
    }

    static byte[] ReadAll(Stream stream)
    {
        if (stream.CanSeek)
        {
            stream.Position = 0;
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
