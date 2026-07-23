using System.Globalization;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Lattice.App.Aggregation;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using SkiaSharp;

namespace Lattice.App.Charting;

/// <summary>Which theme's chart hexes the paints are built with (contract §2).</summary>
public enum StatisticsChartTheme
{
    Light,
    Dark,
}

/// <summary>
/// Turns the pure <see cref="StatisticsChart"/> output into configured LiveCharts objects.
/// This is the ONE place the §2 hex → paint mapping lives (implementer warning #1: LiveCharts
/// paints are not <c>DynamicResource</c>-aware, so on a live theme switch the caller rebuilds
/// the visual here and reassigns — the brushes never follow the theme on their own). Shared
/// verbatim by the Avalonia <c>CartesianChart</c> page and the headless snapshot harness, so
/// the machine-gated content is identical in both.
/// </summary>
public static class StatisticsChartBuilder
{
    // Fluent 2 motion tokens, verbatim (contract §3, [HARD]): 200ms = --durationNormal;
    // the bezier is --curveDecelerateMid (data changes are "enter" motion → decelerate, no
    // bounce). Overriding the library's slow elastic default is the point. In dev-798
    // EasingFunctions.BuildCubicBezier is a Func<float,float,float,float,Func<float,float>>,
    // so invoking it with the four control points yields the Func<float,float> easing LiveCharts
    // wants — the contract's `BuildCubicBezier(0f,0f,0f,1f)` call is exact.
    public static readonly TimeSpan AnimationsSpeed = TimeSpan.FromMilliseconds(200);
    public static readonly Func<float, float> Easing = EasingFunctions.BuildCubicBezier(0f, 0f, 0f, 1f);

    // Tooltip snaps to the nearest day column and lists all visible series (§6); zoom/pan
    // is off in this batch (§6, [HARD]).
    public const FindingStrategy TooltipFindingStrategy = FindingStrategy.CompareOnlyX;
    public const ZoomAndPanMode ZoomMode = ZoomAndPanMode.None;

    /// <summary>The configured series and axes for one render.</summary>
    public sealed record ChartVisual(
        IReadOnlyList<ISeries> Series,
        ICartesianAxis[] XAxes,
        ICartesianAxis[] YAxes);

    /// <summary>
    /// Build the series and axes for the given visible series specs and theme. The marker
    /// size is decided once on the densest visible series (§2, warning #5) and applied to
    /// every line. All §2 pins are set here: 2px stroke, <c>Fill = null</c> (warning #2),
    /// <c>LineSmoothness = 0</c>, circle markers with a solid fill and no stroke, Y-axis-only
    /// gridlines (warning #3), a 0 baseline, and the compact labeler.
    /// </summary>
    public static ChartVisual Build(IReadOnlyList<SeriesSpec> specs, StatisticsChartTheme theme)
    {
        double marker = StatisticsChart.markerSize(ListModule.OfSeq(specs));
        var (gridHex, labelHex) = ThemeHexes(theme);
        var gridPaint = new SolidColorPaint(SKColor.Parse(gridHex)) { StrokeThickness = 1f };

        var series = specs.Select(spec => BuildSeries(spec, marker)).ToList<ISeries>();

        var yAxis = new Axis
        {
            // Y-only gridlines, 1px (§2 / warning #3).
            SeparatorsPaint = gridPaint,
            LabelsPaint = new SolidColorPaint(SKColor.Parse(labelHex)),
            TextSize = 12,
            // Compact labeler (§2). k/M are fixed literals; separators follow CurrentCulture.
            Labeler = v => StatisticsChart.compactLabel(v),
            // Always include the 0 baseline; the max auto-fits to the visible series.
            MinLimit = 0,
        };

        var xAxis = new Axis
        {
            // No X gridlines (§2 / warning #3).
            SeparatorsPaint = null,
            LabelsPaint = new SolidColorPaint(SKColor.Parse(labelHex)),
            TextSize = 12,
            // One position unit per day so the library spaces date labels sensibly; the
            // exact tick cadence is left to the library's automatic spacing (§2).
            UnitWidth = TimeSpan.FromDays(1).Ticks,
            Labeler = DateLabel,
        };

        return new ChartVisual(series, [xAxis], [yAxis]);
    }

    private static LineSeries<DateTimePoint> BuildSeries(SeriesSpec spec, double marker)
    {
        var color = StatisticsPalette.SkColor(spec.Ordinal);
        var points = ListModule
            .ToArray(spec.Points)
            .Select(p => new DateTimePoint(p.Day.UtcDateTime, ToNullable(p.Value)))
            .ToArray();

        return new LineSeries<DateTimePoint>
        {
            Name = spec.Name,
            Values = points,
            Stroke = new SolidColorPaint(color) { StrokeThickness = 2f },
            Fill = null, // warning #2: default is a translucent area fill.
            LineSmoothness = 0, // straight segments — never invent curvature between days.
            GeometrySize = marker,
            GeometryFill = new SolidColorPaint(color),
            GeometryStroke = null, // solid marker, no ring.
        };
    }

    /// <summary>Chart hexes per theme (§2): (Y gridline, axis label).</summary>
    private static (string Grid, string Label) ThemeHexes(StatisticsChartTheme theme) =>
        theme switch
        {
            StatisticsChartTheme.Light => ("#E8E8E8", "#616161"),
            StatisticsChartTheme.Dark => ("#383838", "#ADADAD"),
            _ => throw new ArgumentOutOfRangeException(nameof(theme), theme, null),
        };

    // The X axis value is a DateTime tick count (DateTimePoint). Numeric month-day matches
    // the reference renders ("07-14"); .NET has no culture "numeric month-day" standard
    // pattern, so the compact axis format is fixed while the contract's "date patterns follow
    // CurrentCulture" governs the tooltip's full date and number separators.
    private static string DateLabel(double ticks) =>
        new DateTime((long)ticks).ToString("MM-dd", CultureInfo.CurrentCulture);

    private static double? ToNullable(FSharpOption<double> value) =>
        FSharpOption<double>.get_IsNone(value) ? null : value.Value;
}
