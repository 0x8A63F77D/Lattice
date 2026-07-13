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
    // PLACEHOLDER tolerances — this spike is report-only. Final values are set from the
    // run-to-run / dev-vs-CI distributions the CalibrationHarness produces, mirroring the
    // Stryker #77 "calibrate, then flip the gate" cadence — never guessed as a break
    // threshold up front. These are seeded with generous headroom above the same-machine
    // run-to-run noise measured on the dev Mac (meanError ~0.05, count of supra-tolerance
    // pixels ~0), leaving a wide margin below a real change (meanError ~16, count ~10k).
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
             Visual diff exceeded tolerance (report-only gate):
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
