using System.Collections.Generic;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Drawing;
using LiveChartsCore.Drawing.Layouts;
using LiveChartsCore.Kernel;
using LiveChartsCore.Measure;
using LiveChartsCore.Painting;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Drawing;
using LiveChartsCore.SkiaSharpView.Drawing.Geometries;
using LiveChartsCore.SkiaSharpView.Drawing.Layouts;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.SKCharts;
using SkiaSharp;

namespace Lattice.App.Charting;

/// <summary>
/// The chart tooltip aligned to the design's Fluent hover card (contract §3/§6). LiveCharts'
/// <see cref="SKDefaultTooltip"/> ships a heavy drop shadow and a slow, elastic (overshooting)
/// show animation with no public knobs, both of which violate the Fluent motion rules. We keep
/// the default tooltip wholesale (nearest-X behaviour, content, colours) and retune motion +
/// shadow here, plus override <see cref="GetLayout"/> for two design details the default can't
/// express: a rounded-rect legend swatch in the series colour, and a left-aligned date header.
/// The surface/text COLOURS stay theme-dependent and are set on the chart by the view.
/// </summary>
internal sealed class FluentChartTooltip : SKDefaultTooltip
{
    // Soft Fluent-style shadow (§6 "shadow8"): a small downward offset, a modest Gaussian blur,
    // low-alpha black — a fraction of the library default's weight.
    private static readonly LvcDropShadow Soft = new(0f, 2f, 5f, 5f, new LvcColor(0, 0, 0, 40));

    // Legend-chip swatch metrics (§4): a 12px square, 3px corner radius — matched to the page's
    // legend chips so the tooltip's series marker reads as the same token.
    private const float SwatchSize = 12f;
    private const float SwatchRadius = 3f;

    public FluentChartTooltip()
    {
        // Fluent 2 motion (§3 [HARD]): 200ms, decelerate cubic-bezier — no bounce. The library
        // default is slow with an elastic overshoot, which the Fluent no-bounce rule forbids.
        Easing = EasingFunctions.BuildCubicBezier(0f, 0f, 0f, 1f);
        AnimationsSpeed = System.TimeSpan.FromMilliseconds(200);
    }

    // The base re-applies its own shadow in the draw pipeline, so set the light shadow both before
    // the measure/draw and after each show.
    public override LvcSize Measure()
    {
        Geometry.DropShadow = Soft;
        return base.Measure();
    }

    public override void Show(IEnumerable<ChartPoint> foundPoints, Chart chart)
    {
        base.Show(foundPoints, chart);
        Geometry.DropShadow = Soft;
    }

    /// <summary>
    /// Faithful transcription of <see cref="SKDefaultTooltip"/>'s default layout (dev-798) with
    /// exactly two design deltas (contract §6, owner round-3 eyeball requests, greenlit on #167):
    /// <list type="number">
    /// <item>(a) the series marker is a rounded-rect swatch in the series' palette colour at the
    /// legend-chip metrics, not the default line miniature;</item>
    /// <item>(b) the date header (the X/secondary label) is LEFT-aligned to the table's left edge
    /// — the default centres it because the outer stack uses <see cref="Align.Middle"/>.</item>
    /// </list>
    /// Everything else — nearest-X content, the per-series row table, paddings, RTL column order,
    /// and crucially the label TEXT via <c>GetSecondaryToolTipText</c>/<c>GetPrimaryToolTipText</c>
    /// (so number/date formatting never forks from <see cref="StatisticsChartBuilder"/>) — is the
    /// default verbatim.
    /// </summary>
    protected override Layout<SkiaSharpDrawingContext> GetLayout(IEnumerable<ChartPoint> foundPoints, Chart chart)
    {
        var theme = chart.GetTheme();
        var textSize = (float)chart.View.TooltipTextSize;
        if (textSize < 0f) textSize = theme.TooltipTextSize;

        Paint textPaint = chart.View.TooltipTextPaint
            ?? theme.TooltipTextPaint
            ?? new SolidColorPaint(new SKColor(28, 49, 58));

        var stack = new StackLayout
        {
            Orientation = ContainerOrientation.Vertical,
            // (b): default is Align.Middle (centres the date header over the table); Start pins it
            // to the table's left edge.
            HorizontalAlignment = Align.Start,
            VerticalAlignment = Align.Middle,
        };
        var table = new TableLayout
        {
            HorizontalAlignment = Align.Middle,
            VerticalAlignment = Align.Middle,
        };

        var maxWidth = (float)LiveCharts.DefaultSettings.MaxTooltipsAndLegendsLabelsWidth;
        var row = 0;
        foreach (var point in foundPoints)
        {
            var series = point.Context.Series;

            // Date header (secondary label = the hovered day), once, above the rows.
            if (row == 0 && (series.GetSecondaryToolTipText(point) ?? string.Empty) != LiveCharts.IgnoreToolTipLabel)
            {
                stack.Children.Add(new LabelGeometry
                {
                    Text = series.GetSecondaryToolTipText(point) ?? string.Empty,
                    Paint = textPaint,
                    TextSize = textSize,
                    Padding = new Padding(0, 0, 0, 8),
                    MaxWidth = maxWidth,
                    VerticalAlign = Align.Start,
                    HorizontalAlign = Align.Start,
                });
            }

            var valueText = series.GetPrimaryToolTipText(point) ?? string.Empty;
            // Lattice ships LTR-only (zh-CN + en); the default's RTL column-mirroring branch has no
            // public accessor and no Lattice audience, so the LTR column order is inlined.
            const bool isRTL = false;
            if (valueText != LiveCharts.IgnoreToolTipLabel)
            {
                // (a): rounded-rect swatch in the series colour, replacing the default miniature.
                table.AddChild(Swatch(series), row, isRTL ? 3 : 0);

                if (series.Name != LiveCharts.IgnoreSeriesName)
                {
                    table.AddChild(new LabelGeometry
                    {
                        Text = series.Name ?? string.Empty,
                        Paint = textPaint,
                        TextSize = textSize,
                        Padding = new Padding(10, 0),
                        MaxWidth = maxWidth,
                        VerticalAlign = Align.Start,
                        HorizontalAlign = Align.Start,
                    }, row, 1, isRTL ? Align.End : Align.Start);
                }

                table.AddChild(new LabelGeometry
                {
                    Text = valueText,
                    Paint = textPaint,
                    TextSize = textSize,
                    Padding = new Padding(8, 2),
                    MaxWidth = maxWidth,
                    VerticalAlign = Align.Start,
                    HorizontalAlign = Align.Start,
                }, row, isRTL ? 0 : 2, Align.End);

                row++;
            }
        }

        stack.Children.Add(table);
        return stack;
    }

    /// <summary>
    /// A rounded-rect legend swatch (§4 metrics) filled with the series' own palette colour. The
    /// Statistics chart's series are always <see cref="LineSeries{T}"/> of <see cref="DateTimePoint"/>
    /// (this tooltip is set only on that chart), each built by <see cref="StatisticsChartBuilder"/>
    /// with a <see cref="SolidColorPaint"/> stroke in the palette colour; we read that colour and
    /// fill a fresh paint (never the series' own paint, which tracks its own canvas geometries).
    /// </summary>
    private static RoundedRectangleGeometry Swatch(ISeries series)
    {
        var color = (series as LineSeries<DateTimePoint>)?.Stroke is SolidColorPaint stroke
            ? stroke.Color
            : new SKColor(0x60, 0x60, 0x60);

        return new RoundedRectangleGeometry
        {
            Fill = new SolidColorPaint(color),
            Width = SwatchSize,
            Height = SwatchSize,
            BorderRadius = new LvcPoint(SwatchRadius, SwatchRadius),
            ClippingBounds = LvcRectangle.Empty,
        };
    }
}
