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

    // Design reference for this card (M4 Motion Demo, the interactive hover reference):
    //   date header : font "600 12px", colour #242424, 6px gap to the rows
    //   series rows : 12px, colour #424242, 10px swatch @3px radius
    // So the header is the SAME 12px as the rows — the design emphasises it with weight 600, not
    // with size. Weight is deliberately NOT replicated: in LiveCharts a label's weight comes from
    // its paint's SKTypeface, and (measured) SKTypeface.FromFamilyName only yields a 600 face when
    // given a PLATFORM-SPECIFIC family (".AppleSystemUIFont" → 600 on macOS; "Segoe UI" falls back
    // to Helvetica 400), and it returns a SHARED CACHED instance — which LiveCharts disposes on
    // chart teardown (see StatisticsChartSnapshotTests' typeface note). Hardcoding per-OS font
    // names plus a use-after-dispose risk is a bad trade for one bold line, so the header is
    // distinguished by size-parity + the 8px separator + left alignment instead.

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

    // Date-header padding. Top = 2 (not the default's 0): the card's interior padding is ~4px on
    // every side, and the last value row carries 2px of bottom padding — so a 0-top date header sat
    // 4px from the top edge while the last row sat 6px from the bottom. Matching the row's 2px
    // balances the two (owner eyeball round: "date too close to the top edge"). Bottom = 8 is the
    // date→table separator, unchanged from the default.
    internal const float DateHeaderTopPadding = 2f;
    internal const float DateHeaderBottomPadding = 8f;

    /// <summary>
    /// One composed row of the hover card — the pure input to <see cref="ComposeCard"/>, so the
    /// card's geometry is assertable without a live chart or a synthesised hover (test seam,
    /// InternalsVisibleTo). <paramref name="Name"/> is null when the series opts out of a label
    /// (<c>LiveCharts.IgnoreSeriesName</c>).
    /// </summary>
    internal readonly record struct CardRow(string? Name, string Value, SKColor Color);

    /// <summary>
    /// Faithful transcription of <see cref="SKDefaultTooltip"/>'s default layout (dev-798) with
    /// exactly two design deltas (contract §6, owner round-3 eyeball requests, greenlit on #167):
    /// <list type="number">
    /// <item>(a) the series marker is a rounded-rect swatch in the series' palette colour at the
    /// legend-chip metrics, not the default line miniature;</item>
    /// <item>(b) the date header (the X/secondary label) is LEFT-aligned to the table's left edge
    /// — the default centres it because the outer stack uses <see cref="Align.Middle"/>.</item>
    /// </list>
    /// Everything else — nearest-X content, the per-series row table, paddings, and crucially the
    /// label TEXT via <c>GetSecondaryToolTipText</c>/<c>GetPrimaryToolTipText</c> (so number/date
    /// formatting never forks from <see cref="StatisticsChartBuilder"/>) — is the default verbatim.
    /// This override only reads the hovered points; the composition lives in the pure
    /// <see cref="ComposeCard"/> so it can be geometry-asserted (AGENTS.md: visual fixes need
    /// end-state verification — a silently dropped <c>TextSize</c> shipped once, PR #167).
    /// </summary>
    protected override Layout<SkiaSharpDrawingContext> GetLayout(IEnumerable<ChartPoint> foundPoints, Chart chart)
    {
        var theme = chart.GetTheme();
        var textSize = (float)chart.View.TooltipTextSize;
        if (textSize < 0f) textSize = theme.TooltipTextSize;

        Paint textPaint = chart.View.TooltipTextPaint
            ?? theme.TooltipTextPaint
            ?? new SolidColorPaint(new SKColor(28, 49, 58));

        var (date, rows) = ReadPoints(foundPoints);
        return ComposeCard(date, rows, textSize, textPaint);
    }

    /// <summary>
    /// Reads the hovered points into the pure card model. The date header is the first point's
    /// secondary label (every hovered point shares the snapped day, §6 nearest-X) and is emitted at
    /// most once — the default would re-emit it for a second point when the first contributed no
    /// row, which cannot arise here since the Statistics formatters always yield a value.
    /// </summary>
    private static (string? Date, List<CardRow> Rows) ReadPoints(IEnumerable<ChartPoint> foundPoints)
    {
        string? date = null;
        var rows = new List<CardRow>();

        foreach (var point in foundPoints)
        {
            var series = point.Context.Series;

            var secondary = series.GetSecondaryToolTipText(point) ?? string.Empty;
            if (date is null && secondary != LiveCharts.IgnoreToolTipLabel)
                date = secondary;

            var value = series.GetPrimaryToolTipText(point) ?? string.Empty;
            if (value == LiveCharts.IgnoreToolTipLabel)
                continue;

            rows.Add(new CardRow(
                series.Name == LiveCharts.IgnoreSeriesName ? null : series.Name ?? string.Empty,
                value,
                SeriesColor(series)));
        }

        return (date, rows);
    }

    /// <summary>
    /// Composes the hover card: an optional date header stacked above a one-row-per-series table of
    /// (swatch, name, value). Pure — same inputs, same geometry — so the design's metrics are
    /// machine-checkable (<c>FluentChartTooltipCardTests</c>). Column order is LTR only: Lattice
    /// ships zh-CN + en, and the default's RTL mirroring branch reads an internal text setting with
    /// no public accessor.
    /// </summary>
    internal static StackLayout ComposeCard(
        string? dateText, IReadOnlyList<CardRow> rows, float textSize, Paint textPaint)
    {
        var maxWidth = (float)LiveCharts.DefaultSettings.MaxTooltipsAndLegendsLabelsWidth;

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

        if (dateText is not null)
        {
            stack.Children.Add(new LabelGeometry
            {
                Text = dateText,
                Paint = textPaint,
                // The chart's tooltip text size (12, set by the view) — one source of truth with the
                // rows, and the design's header size exactly. Must stay explicit: dropping it falls
                // back to the geometry default (0), which renders the date degenerately small.
                TextSize = textSize,
                Padding = new Padding(0, DateHeaderTopPadding, 0, DateHeaderBottomPadding),
                MaxWidth = maxWidth,
                VerticalAlign = Align.Start,
                HorizontalAlign = Align.Start,
            });
        }

        for (var row = 0; row < rows.Count; row++)
        {
            var (name, value, color) = rows[row];

            // (a): rounded-rect swatch in the series colour, replacing the default line miniature.
            table.AddChild(Swatch(color), row, 0);

            if (name is not null)
            {
                table.AddChild(new LabelGeometry
                {
                    Text = name,
                    Paint = textPaint,
                    TextSize = textSize,
                    Padding = new Padding(10, 0),
                    MaxWidth = maxWidth,
                    VerticalAlign = Align.Start,
                    HorizontalAlign = Align.Start,
                }, row, 1, Align.Start);
            }

            table.AddChild(new LabelGeometry
            {
                Text = value,
                Paint = textPaint,
                TextSize = textSize,
                Padding = new Padding(8, 2),
                MaxWidth = maxWidth,
                VerticalAlign = Align.Start,
                HorizontalAlign = Align.Start,
            }, row, 2, Align.End);
        }

        stack.Children.Add(table);
        return stack;
    }

    /// <summary>
    /// The series' palette colour. The Statistics chart's series are always
    /// <see cref="LineSeries{T}"/> of <see cref="DateTimePoint"/> (this tooltip is set only on that
    /// chart), each built by <see cref="StatisticsChartBuilder"/> with a
    /// <see cref="SolidColorPaint"/> stroke in the palette colour.
    /// </summary>
    private static SKColor SeriesColor(ISeries series) =>
        (series as LineSeries<DateTimePoint>)?.Stroke is SolidColorPaint stroke
            ? stroke.Color
            : new SKColor(0x60, 0x60, 0x60);

    /// <summary>
    /// A rounded-rect legend swatch (§4 metrics) filled with a fresh paint in the series colour —
    /// never the series' own paint, which tracks its own canvas geometries.
    /// </summary>
    private static RoundedRectangleGeometry Swatch(SKColor color) =>
        new()
        {
            Fill = new SolidColorPaint(color),
            Width = SwatchSize,
            Height = SwatchSize,
            BorderRadius = new LvcPoint(SwatchRadius, SwatchRadius),
            ClippingBounds = LvcRectangle.Empty,
        };
}
