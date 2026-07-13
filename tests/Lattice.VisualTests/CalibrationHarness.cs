using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using SixLabors.ImageSharp;
using Xunit;

namespace Lattice.VisualTests;

/// <summary>
/// Produces the calibration numbers issue #82 is about, for BOTH the RMSE metric (the
/// ImageMagick-style number the survey references) and the exact gate metrics the tolerant
/// comparer uses (<see cref="GateMetrics"/>: mean-error and supra-tolerance pixel-error-count):
/// <list type="number">
///   <item><b>run-to-run</b> on one machine (render_i vs render_0) — isolates within-environment
///   render nondeterminism;</item>
///   <item><b>vs committed baseline</b> (each render vs the verified PNG) — on CI this is ≈ 0
///   because the baseline is the source of truth; on a dev Mac it is the dev-vs-CI drift.</item>
/// </list>
/// Writes a markdown report to the artifacts dir for CI upload; asserts only sanity (finite
/// numbers, pinned pixel size), never a threshold — this is a report-only measurement.
/// </summary>
[Trait("Category", "Visual")]
public class CalibrationHarness
{
    const int Repeats = 8;

    [AvaloniaFact]
    public void Measure_run_to_run_and_vs_baseline()
    {
        VisualGate.SkipUnlessEnabled();

        var report = new StringBuilder();
        report.AppendLine($"# Visual calibration — {EnvironmentDescription}");
        report.AppendLine();
        report.AppendLine($"- Repeats: {Repeats} renders per theme");
        report.AppendLine($"- Fixture: FirstRunView, {VisualFixture.Width}×{VisualFixture.Height}, RenderScaling 1.0, Inter pinned, caches warmed");
        report.AppendLine($"- Gate metrics use per-pixel shift tolerance {GateMetrics.PerPixelShiftTolerance} (summed |ΔRGB|)");
        report.AppendLine();

        MeasureTheme("light", ThemeVariant.Light, report);
        MeasureTheme("dark", ThemeVariant.Dark, report);

        var dir = VisualPaths.ArtifactsDir;
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"calibration-{RuntimeTag}.md"), report.ToString());
    }

    static void MeasureTheme(string theme, ThemeVariant variant, StringBuilder report)
    {
        var renders = new List<byte[]>(Repeats);
        for (var i = 0; i < Repeats; i++)
        {
            renders.Add(VisualFixture.Capture(variant));
        }

        // Pin geometry: every capture is exactly the expected device-pixel size.
        foreach (var png in renders)
        {
            using var img = Image.Load(png);
            Assert.Equal(VisualFixture.Width, img.Width);
            Assert.Equal(VisualFixture.Height, img.Height);
        }

        report.AppendLine($"## {theme}");
        report.AppendLine();

        // (i) run-to-run: render_i vs render_0
        var runToRun = renders.Skip(1).Select(r => (r, renders[0])).ToList();
        report.AppendLine("**Run-to-run** (render_i vs render_0):");
        report.AppendLine();
        report.AppendLine(MetricsTable(runToRun));
        report.AppendLine();

        // (ii) vs committed baseline
        var baselinePath = Path.Combine(
            VisualPaths.SourceDir(), $"FirstRunViewVisualTests.Render_{theme}.verified.png");
        if (File.Exists(baselinePath))
        {
            var baseline = File.ReadAllBytes(baselinePath);
            var vsBaseline = renders.Select(r => (r, baseline)).ToList();
            report.AppendLine("**Vs committed baseline** (each render vs verified PNG):");
            report.AppendLine();
            report.AppendLine(MetricsTable(vsBaseline));
        }
        else
        {
            report.AppendLine(
                $"_No committed baseline at {Path.GetFileName(baselinePath)} yet — vs-baseline unavailable this run._");
        }

        report.AppendLine();
    }

    // One table with a row per metric (RMSE, mean-error, supra-tolerance pixel count), each
    // summarized as a min/mean/max/stddev distribution over the given (received, reference) pairs.
    static string MetricsTable(IReadOnlyList<(byte[] Received, byte[] Reference)> pairs)
    {
        var rmse = new List<double>();
        var meanError = new List<double>();
        var pixelCount = new List<double>();
        foreach (var (received, reference) in pairs)
        {
            rmse.Add(RmseMetric.Rmse(received, reference));
            var m = GateMetrics.Compute(received, reference);
            meanError.Add(m.MeanError);
            pixelCount.Add(m.PixelErrorCount);
            Assert.True(double.IsFinite(rmse[^1]), "RMSE must be finite (images share dimensions)");
        }

        var sb = new StringBuilder();
        sb.AppendLine($"| metric | n | min | mean | max | stddev |");
        sb.AppendLine($"|--------|---|-----|------|-----|--------|");
        sb.AppendLine(Row("RMSE (0..1)", rmse, Sci));
        sb.AppendLine(Row("mean-error (0..765)", meanError, Fixed4));
        sb.Append(Row("pixels > shift-tol", pixelCount, Int0));
        return sb.ToString();
    }

    static string Row(string label, IReadOnlyList<double> xs, Func<double, string> fmt)
    {
        if (xs.Count == 0)
        {
            return $"| {label} | 0 | – | – | – | – |";
        }

        var mean = xs.Average();
        var stddev = Math.Sqrt(xs.Select(x => (x - mean) * (x - mean)).Average());
        return $"| {label} | {xs.Count} | {fmt(xs.Min())} | {fmt(mean)} | {fmt(xs.Max())} | {fmt(stddev)} |";
    }

    static string Sci(double v) => v.ToString("E3", CultureInfo.InvariantCulture);
    static string Fixed4(double v) => v.ToString("F4", CultureInfo.InvariantCulture);
    static string Int0(double v) => v.ToString("F1", CultureInfo.InvariantCulture);

    static string EnvironmentDescription =>
        $"{RuntimeInformation.OSDescription} / {RuntimeInformation.ProcessArchitecture} / CI={Environment.GetEnvironmentVariable("CI") ?? "false"}";

    static string RuntimeTag =>
        $"{(Environment.GetEnvironmentVariable("CI") == "true" ? "ci" : "local")}-{RuntimeInformation.ProcessArchitecture}"
            .ToLowerInvariant();
}
