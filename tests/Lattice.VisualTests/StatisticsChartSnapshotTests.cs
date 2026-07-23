using System.Globalization;
using Avalonia.Headless.XUnit;
using Avalonia.Platform;
using Lattice.App.Aggregation;
using Lattice.App.Charting;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.SKCharts;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using SkiaSharp;
using Xunit;

namespace Lattice.VisualTests;

/// <summary>
/// The machine content gate for the Statistics chart (design contract §2, #148 two-layer gate):
/// renders the chart offscreen with LiveCharts' own SkiaSharp path (<see cref="SKCartesianChart"/>
/// — no Avalonia hosting) and pixel-compares against a committed baseline. This is the exact,
/// deterministic layer; the Avalonia-hosted page chrome rides the masked page-level tests + owner
/// eyeball. Determinism is pinned: en-US culture, the bundled Inter typeface on every label,
/// zero animation (the settled frame), and a fixed size — so the same series render byte-stable.
/// Baselines are macOS/Skia captures; the suite skips unless the visual-tests workflow opts in.
/// </summary>
[Trait("Category", "Visual")]
public class StatisticsChartSnapshotTests
{
    private const int Width = 900;
    private const int Height = 480;
    private static readonly DateTimeOffset Day0 = new(2026, 7, 14, 0, 0, 0, TimeSpan.Zero);

    // 4 metrics × 2 themes on the canonical 4-project, 9-point baseline (matches stats-light.png:
    // Einstein qual.1, Rosetta qual.2, WCG qual.3, LHC qual.4).
    public static IEnumerable<object[]> BaselineCases()
    {
        foreach (var (metricName, metric) in new[]
                 {
                     ("user_total", CreditMetric.UserTotal),
                     ("user_average", CreditMetric.UserAverage),
                     ("host_total", CreditMetric.HostTotal),
                     ("host_average", CreditMetric.HostAverage),
                 })
        foreach (var (themeName, theme) in new[]
                 {
                     ("light", StatisticsChartTheme.Light),
                     ("dark", StatisticsChartTheme.Dark),
                 })
            yield return [$"{metricName}_{themeName}", metric, theme];
    }

    [AvaloniaTheory]
    [MemberData(nameof(BaselineCases))]
    public Task Baseline(string name, CreditMetric metric, StatisticsChartTheme theme)
    {
        VisualGate.SkipUnlessEnabled();
        var png = Render(Canonical(), StatisticsChart.defaultVisible(Canonical()), metric, theme);
        return Verify(new MemoryStream(png), extension: "png").UseParameters(name);
    }

    [AvaloniaFact]
    public Task Overflow_top_six_of_twelve_light()
    {
        VisualGate.SkipUnlessEnabled();
        var histories = Overflow();
        var png = Render(histories, StatisticsChart.defaultVisible(histories), CreditMetric.UserTotal, StatisticsChartTheme.Light);
        return Verify(new MemoryStream(png), extension: "png");
    }

    [AvaloniaFact]
    public Task Density_ninety_points_pure_line_light()
    {
        VisualGate.SkipUnlessEnabled();
        var histories = Density();
        var png = Render(histories, StatisticsChart.defaultVisible(histories), CreditMetric.UserTotal, StatisticsChartTheme.Light);
        return Verify(new MemoryStream(png), extension: "png");
    }

    // ---- render ----------------------------------------------------------

    private static byte[] Render(
        FSharpList<ProjectHistory> histories, FSharpSet<string> visible, CreditMetric metric, StatisticsChartTheme theme)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        try
        {
            var specs = StatisticsChart.seriesFor(metric, visible, histories);
            var visual = StatisticsChartBuilder.Build(ListModule.ToArray(specs), theme, metric);
            PinFont(visual);

            var chart = new SKCartesianChart
            {
                Width = Width,
                Height = Height,
                // Bake the page canvas behind the transparent chart so the snapshot resembles
                // the page and gives labels/lines their real contrast (harness-only).
                Background = SKColor.Parse(theme == StatisticsChartTheme.Dark ? "#1F1F1F" : "#FFFFFF"),
                Series = visual.Series,
                XAxes = visual.XAxes,
                YAxes = visual.YAxes,
                // Settled frame: no animation state in a one-shot render.
                AnimationsSpeed = TimeSpan.Zero,
            };

            using var stream = new MemoryStream();
            chart.SaveImage(stream, SKEncodedImageFormat.Png, 100);
            return stream.ToArray();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    // Pin the bundled Inter typeface on every axis label paint so glyph geometry does not depend
    // on the runner's system font stack (VisualTests precedent). A FRESH typeface per render:
    // LiveCharts disposes a paint's SKTypeface when the chart is torn down, so a shared instance
    // would be disposed after the first render and NRE the next (from cached bytes, not I/O).
    private static void PinFont(StatisticsChartBuilder.ChartVisual visual)
    {
        foreach (var axis in visual.XAxes.Concat(visual.YAxes))
            if (axis is LiveChartsCore.SkiaSharpView.Axis { LabelsPaint: SolidColorPaint paint })
                paint.SKTypeface = SKTypeface.FromData(SKData.CreateCopy(InterBytes.Value));
    }

    private static readonly Lazy<byte[]> InterBytes = new(() =>
    {
        var assets = AssetLoader.GetAssets(new Uri("avares://Avalonia.Fonts.Inter"), null).ToList();
        var ttf = assets.FirstOrDefault(u => u.AbsolutePath.Contains("Regular") && u.AbsolutePath.EndsWith(".ttf"))
                  ?? assets.FirstOrDefault(u => u.AbsolutePath.EndsWith(".ttf"));
        using var s = AssetLoader.Open(ttf ?? throw new InvalidOperationException("Inter font asset not found."));
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    });

    // ---- fixtures --------------------------------------------------------

    private static ProjectHistory Hist(
        string url, string name, int ordinal, double rac, int days,
        double utBase, double utStep, double uaBase, double htBase, double htStep, double haBase)
    {
        var daily = Enumerable.Range(0, days).Select(i => new DailyCredit(
            Day0.AddDays(i),
            utBase + utStep * i,
            uaBase * (1 + 0.01 * i),
            htBase + htStep * i,
            haBase * (1 + 0.01 * i)));
        return new ProjectHistory(url, name, ordinal, rac, ListModule.OfSeq(daily));
    }

    // Canonical 4-project × 9-point baseline (the stats-light.png dataset).
    private static FSharpList<ProjectHistory> Canonical() => ListModule.OfSeq(
    [
        Hist("u0", "Einstein@Home", 0, 640, 9, 4_100_000, 200_000, 1_900, 500_000, 20_000, 640),
        Hist("u1", "Rosetta@home", 1, 210, 9, 1_350_000, 95_000, 900, 180_000, 6_000, 210),
        Hist("u2", "World Community Grid", 2, 300, 9, 3_000_000, 40_000, 1_200, 300_000, 8_000, 300),
        Hist("u3", "LHC@home", 3, 120, 9, 500_000, 7_000, 300, 50_000, 1_000, 120),
    ]);

    // 12 projects with RAC DESCENDING by ordinal, so the top-6-by-RAC are ordinals 0..5 and
    // render in the clean qualitative.1–6 palette (top6-overflow.png); the lower-RAC ordinals
    // 6..11 are the hidden overflow tail. 9 points each → markers, k-scale.
    private static FSharpList<ProjectHistory> Overflow() => ListModule.OfSeq(
        Enumerable.Range(0, 12).Select(i =>
        {
            double scale = 12 - i; // 12 (ordinal 0, highest RAC) down to 1
            return Hist($"o{i}", $"Project {i}", i, scale, 9,
                utBase: 300 * scale, utStep: 700 * scale,
                uaBase: 40 * scale, htBase: 60 * scale, htStep: 140 * scale, haBase: 20 * scale);
        }));

    // Two 90-point projects → pure line (marker rule, > 30 points).
    private static FSharpList<ProjectHistory> Density() => ListModule.OfSeq(
    [
        Hist("d0", "Einstein@Home", 0, 640, 90, 4_100_000, 20_000, 1_900, 300_000, 3_000, 500),
        Hist("d1", "Rosetta@home", 1, 210, 90, 1_350_000, 12_000, 900, 90_000, 1_500, 160),
    ]);
}
