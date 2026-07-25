using System.Reflection;
using Lattice.App.Aggregation;
using Lattice.App.Charting;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.Painting.Effects;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using SkiaSharp;
using Xunit;

namespace Lattice.App.Tests;

/// <summary>
/// Wiring guards for the shared chart renderer: the §2 pins the pixel gate would only catch
/// after a full render (Fill=null, 2px stroke, straight segments, Y-only gridlines, 0 baseline,
/// gaps → null points, colour-by-ordinal) plus the #170 gap split (which metrics get a dashed
/// bridge series, and what it looks like). Cheap structural asserts fail fast on a broken paint.
/// </summary>
public class StatisticsChartBuilderTests
{
    private static readonly DateTimeOffset Day0 = new(2026, 7, 14, 0, 0, 0, TimeSpan.Zero);

    private static SeriesPoint Point(int dayOffset, double? value) =>
        new(Day0.AddDays(dayOffset), value is { } v ? FSharpOption<double>.Some(v) : FSharpOption<double>.None);

    private static SeriesSpec Spec(string url, string name, int ordinal, params SeriesPoint[] points) =>
        new(url, name, ordinal, ListModule.OfSeq(points));

    private static SeriesSpec Ramp(string url, string name, int ordinal, int count) =>
        Spec(url, name, ordinal, [.. Enumerable.Range(0, count).Select(i => Point(i, i))]);

    [Fact]
    public void Palette_is_colour_by_ordinal_and_wraps_past_ten()
    {
        Assert.Equal(SKColor.Parse("#637CEF"), StatisticsPalette.SkColor(0));
        Assert.Equal(SKColor.Parse("#9373C0"), StatisticsPalette.SkColor(3)); // orchid, LHC in the mock
        Assert.Equal(StatisticsPalette.SkColor(0), StatisticsPalette.SkColor(10)); // wraps mod 10
    }

    [Fact]
    public void Every_line_pins_the_section_two_style()
    {
        var visual = StatisticsChartBuilder.Build([Ramp("a", "A", 0, 3)], StatisticsChartTheme.Light, CreditMetric.UserTotal);
        var line = Assert.IsType<LineSeries<DateTimePoint>>(Assert.Single(visual.Series));

        Assert.Null(line.Fill); // warning #2
        Assert.Equal(0, line.LineSmoothness); // straight segments
        Assert.Null(line.GeometryStroke); // solid marker, no ring
        var stroke = Assert.IsType<SolidColorPaint>(line.Stroke);
        Assert.Equal(2f, stroke.StrokeThickness);
        Assert.Equal(StatisticsPalette.SkColor(0), stroke.Color);
        var fill = Assert.IsType<SolidColorPaint>(line.GeometryFill);
        Assert.Equal(StatisticsPalette.SkColor(0), fill.Color);
    }

    [Fact]
    public void Marker_size_follows_the_longest_visible_series()
    {
        var small = StatisticsChartBuilder.Build([Ramp("a", "A", 0, 9)], StatisticsChartTheme.Light, CreditMetric.UserTotal);
        Assert.Equal(8d, ((LineSeries<DateTimePoint>)small.Series[0]).GeometrySize);

        var dense = StatisticsChartBuilder.Build([Ramp("a", "A", 0, 40)], StatisticsChartTheme.Light, CreditMetric.UserTotal);
        Assert.Equal(0d, ((LineSeries<DateTimePoint>)dense.Series[0]).GeometrySize);
    }

    [Fact]
    public void Gaps_become_null_valued_points_never_joined()
    {
        // An already-gap-filled spec (as F# seriesFor emits): days 0 and 2 real, day 1 a None.
        var visual = StatisticsChartBuilder.Build(
            [Spec("a", "A", 0, Point(0, 1), Point(1, null), Point(2, 3))], StatisticsChartTheme.Light, CreditMetric.UserTotal);
        var values = ((LineSeries<DateTimePoint>)visual.Series[0]).Values!.Cast<DateTimePoint>().ToList();
        Assert.Equal(3, values.Count);
        Assert.Equal([1d, null, 3d], values.Select(v => v.Value));
    }

    // ---- #170 gap rendering: dashed bridge for totals, hard break for averages ----------

    // Two gap runs: day 1 alone, then days 4-5, with an OBSERVED segment (2 → 3) between them.
    private static SeriesSpec Gapped() => Spec(
        "a", "A", 0,
        Point(0, 10), Point(1, null), Point(2, 12), Point(3, 13), Point(4, null), Point(5, null), Point(6, 16));

    private static List<LineSeries<DateTimePoint>> Lines(CreditMetric metric) =>
        [.. StatisticsChartBuilder.Build([Gapped()], StatisticsChartTheme.Light, metric)
            .Series.Cast<LineSeries<DateTimePoint>>()];

    [Theory]
    [InlineData(false, 3)] // UserTotal / HostTotal: the real series + one bridge per gap RUN
    [InlineData(true, 1)] // UserAverage / HostAverage: the real series alone, breaks intact
    public void Only_the_cumulative_metrics_add_bridge_series(bool average, int expectedSeries)
    {
        foreach (var metric in average
                     ? new[] { CreditMetric.UserAverage, CreditMetric.HostAverage }
                     : [CreditMetric.UserTotal, CreditMetric.HostTotal])
        {
            var lines = Lines(metric);
            Assert.Equal(expectedSeries, lines.Count);
            // The real series is untouched either way: every day still present, gaps still null.
            var real = lines[0].Values!.Cast<DateTimePoint>().ToList();
            Assert.Equal([10d, null, 12d, 13d, null, null, 16d], real.Select(v => v.Value));
        }
    }

    // dev-798 does not expose DashEffect's pattern publicly, so read it reflectively — asserting
    // only "it is A DashEffect" would let any pattern through. Fails loudly if the member moves.
    private static float[] DashArrayOf(DashEffect effect)
    {
        for (var t = effect.GetType(); t is not null; t = t.BaseType)
            if (t.GetProperty("DashArray", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) is { } p)
                return (float[])p.GetValue(effect)!;
        throw new InvalidOperationException("DashEffect's dash pattern member moved — update this probe.");
    }

    [Fact]
    public void A_bridge_carries_only_the_two_observed_endpoints_of_its_gap()
    {
        var bridges = Lines(CreditMetric.UserTotal).Skip(1)
            .Select(s => s.Values!.Cast<DateTimePoint>().Select(p => (p.DateTime, p.Value)).ToList())
            .ToList();

        Assert.Equal(
            [
                [(Day0.AddDays(0).UtcDateTime, 10d), (Day0.AddDays(2).UtcDateTime, 12d)],
                [(Day0.AddDays(3).UtcDateTime, 13d), (Day0.AddDays(6).UtcDateTime, 16d)],
            ],
            bridges);
    }

    [Fact]
    public void A_bridge_is_the_series_colour_dashed_with_no_markers_and_no_chrome()
    {
        var bridge = Lines(CreditMetric.HostTotal)[1]; // the first of the two bridges

        var stroke = Assert.IsType<SolidColorPaint>(bridge.Stroke);
        Assert.Equal(StatisticsPalette.SkColor(0), stroke.Color); // same colour as the real line
        Assert.Equal(2f, stroke.StrokeThickness);
        var dash = Assert.IsType<DashEffect>(stroke.PathEffect);
        Assert.Equal([4f, 4f], DashArrayOf(dash)); // the 4/4 idiom the hover guide already uses

        Assert.Null(bridge.Fill);
        Assert.Equal(0, bridge.LineSmoothness);
        Assert.Equal(0d, bridge.GeometrySize); // no markers on unobserved days
        Assert.Null(bridge.GeometryFill);
        Assert.Null(bridge.GeometryStroke);
        Assert.False(bridge.IsHoverable); // tooltip lists one entry per project
        Assert.False(bridge.IsVisibleAtLegend);
    }

    [Fact]
    public void A_gapless_history_gets_no_bridge_series_at_all()
    {
        var visual = StatisticsChartBuilder.Build([Ramp("a", "A", 0, 5)], StatisticsChartTheme.Light, CreditMetric.UserTotal);
        Assert.Single(visual.Series);
    }

    [Fact]
    public void Bridges_do_not_shift_the_marker_rule()
    {
        // 31 observed days with one gap → 31 real points > 30 → pure line, bridge series ignored.
        var points = Enumerable.Range(0, 33).Select(i => Point(i, i == 5 || i == 6 ? null : i)).ToArray();
        var visual = StatisticsChartBuilder.Build(
            [Spec("a", "A", 0, points)], StatisticsChartTheme.Light, CreditMetric.UserTotal);
        var lines = visual.Series.Cast<LineSeries<DateTimePoint>>().ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal(0d, lines[0].GeometrySize);
    }

    [Fact]
    public void Axes_put_gridlines_on_Y_only_with_a_zero_baseline()
    {
        var visual = StatisticsChartBuilder.Build([Ramp("a", "A", 0, 3)], StatisticsChartTheme.Light, CreditMetric.UserTotal);
        var x = Assert.IsType<Axis>(Assert.Single(visual.XAxes));
        var y = Assert.IsType<Axis>(Assert.Single(visual.YAxes));

        Assert.Null(x.SeparatorsPaint); // warning #3: no X gridlines
        Assert.NotNull(y.SeparatorsPaint); // Y gridlines present
        Assert.Equal(0d, y.MinLimit); // always include the 0 baseline
        Assert.Equal(12d, y.TextSize);
    }

    [Fact]
    public void Theme_switches_the_gridline_and_label_hexes()
    {
        var light = (SolidColorPaint)StatisticsChartBuilder
            .Build([Ramp("a", "A", 0, 3)], StatisticsChartTheme.Light, CreditMetric.UserTotal).YAxes[0].SeparatorsPaint!;
        var dark = (SolidColorPaint)StatisticsChartBuilder
            .Build([Ramp("a", "A", 0, 3)], StatisticsChartTheme.Dark, CreditMetric.UserTotal).YAxes[0].SeparatorsPaint!;

        Assert.Equal(SKColor.Parse("#E8E8E8"), light.Color);
        Assert.Equal(SKColor.Parse("#383838"), dark.Color);
    }
}
