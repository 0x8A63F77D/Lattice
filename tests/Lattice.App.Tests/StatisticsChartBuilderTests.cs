using Lattice.App.Aggregation;
using Lattice.App.Charting;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using SkiaSharp;
using Xunit;

namespace Lattice.App.Tests;

/// <summary>
/// Wiring guards for the shared chart renderer: the §2 pins the pixel gate would only catch
/// after a full render (Fill=null, 2px stroke, straight segments, Y-only gridlines, 0 baseline,
/// gaps → null points, colour-by-ordinal). Cheap structural asserts fail fast on a broken paint.
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
