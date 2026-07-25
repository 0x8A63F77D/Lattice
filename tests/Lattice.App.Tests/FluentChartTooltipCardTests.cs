using LiveChartsCore.Drawing;
using LiveChartsCore.Measure;
using LiveChartsCore.Painting;
using LiveChartsCore.SkiaSharpView.Drawing;
using LiveChartsCore.SkiaSharpView.Drawing.Geometries;
using LiveChartsCore.SkiaSharpView.Drawing.Layouts;
using LiveChartsCore.SkiaSharpView.Painting;
using Lattice.App.Charting;
using SkiaSharp;
using Xunit;

namespace Lattice.App.Tests;

/// <summary>
/// End-state geometry gate for the Statistics hover card (PR #167, Codex P1). The chart's pixel
/// snapshots never install this tooltip and never synthesise a hover, so the card's metrics had NO
/// machine coverage — a silently dropped <c>TextSize</c> (whose motion-property default is 0)
/// rendered the date degenerately small with all 904 tests green, and only the owner's eye caught
/// it. AGENTS.md: a visual fix without end-state verification is not done.
///
/// These assert the composed geometry — sizes, paddings, alignments, swatch shape and colour — which
/// is what the design contract pins; they are deterministic and need no live chart, because
/// <see cref="FluentChartTooltip.ComposeCard"/> is pure.
/// </summary>
public class FluentChartTooltipCardTests
{
    private const float TextSize = 12f;
    private static readonly SKColor Blue = new(0x4F, 0x6B, 0xED);
    private static readonly SKColor Pink = new(0xE3, 0x00, 0x8C);

    private static Paint TextPaint() => new SolidColorPaint(new SKColor(0x24, 0x24, 0x24));

    private static StackLayout Card(string? date, params FluentChartTooltip.CardRow[] rows) =>
        FluentChartTooltip.ComposeCard(date, rows, TextSize, TextPaint());

    private static LabelGeometry Header(StackLayout card) =>
        Assert.IsType<LabelGeometry>(card.Children[0]);

    private static TableLayout Table(StackLayout card) =>
        Assert.IsType<TableLayout>(card.Children[^1]);

    private static IDrawnElement<SkiaSharpDrawingContext> Cell(TableLayout table, int row, int column) =>
        Assert.Single(table.Cells, c => c.Row == row && c.Column == column).Drawable;

    [Fact]
    public void Date_header_renders_at_the_tooltip_text_size()
    {
        // The regression this suite exists for: the header must carry an explicit TextSize.
        // BaseLabelGeometry.TextSize defaults to 0, so a dropped assignment is invisible to
        // every other gate.
        var header = Header(Card("2026-07-22", new FluentChartTooltip.CardRow("Einstein@Home", "4,182,000", Blue)));

        Assert.Equal("2026-07-22", header.Text);
        Assert.Equal(TextSize, header.TextSize);
    }

    [Fact]
    public void Date_header_top_gap_matches_the_last_rows_bottom_gap()
    {
        // Owner eyeball: the header sat closer to the top edge than the last row sat to the bottom.
        // The row's bottom padding is the reference the header's top padding must match.
        var card = Card("2026-07-22", new FluentChartTooltip.CardRow("Einstein@Home", "4,182,000", Blue));

        var header = Header(card);
        var value = Assert.IsType<LabelGeometry>(Cell(Table(card), 0, 2));

        Assert.Equal(value.Padding.Bottom, header.Padding.Top);
        Assert.Equal(FluentChartTooltip.DateHeaderTopPadding, header.Padding.Top);
        // The date→table separator is unchanged from the library default.
        Assert.Equal(FluentChartTooltip.DateHeaderBottomPadding, header.Padding.Bottom);
    }

    [Fact]
    public void Date_header_is_left_aligned_not_centred()
    {
        // Delta (b): the default centres the header because the outer stack is Align.Middle.
        var card = Card("2026-07-22", new FluentChartTooltip.CardRow("Einstein@Home", "4,182,000", Blue));

        Assert.Equal(Align.Start, card.HorizontalAlignment);
        Assert.Equal(Align.Start, Header(card).HorizontalAlign);
        Assert.Equal(ContainerOrientation.Vertical, card.Orientation);
    }

    [Fact]
    public void Series_marker_is_a_rounded_square_in_the_series_colour()
    {
        // Delta (a): a legend-chip swatch (12px / 3px radius), not the default line miniature.
        var card = Card("2026-07-22", new FluentChartTooltip.CardRow("Einstein@Home", "4,182,000", Blue));

        var swatch = Assert.IsType<RoundedRectangleGeometry>(Cell(Table(card), 0, 0));
        Assert.Equal(12f, swatch.Width);
        Assert.Equal(12f, swatch.Height);
        Assert.Equal(3f, swatch.BorderRadius.X);
        Assert.Equal(3f, swatch.BorderRadius.Y);
        Assert.Equal(Blue, Assert.IsType<SolidColorPaint>(swatch.Fill).Color);
    }

    [Fact]
    public void Every_row_carries_the_series_colour_name_and_value_at_the_text_size()
    {
        var card = Card(
            "2026-07-22",
            new FluentChartTooltip.CardRow("Einstein@Home", "4,182,000", Blue),
            new FluentChartTooltip.CardRow("Rosetta@home", "1,504,220", Pink));

        var table = Table(card);

        Assert.Equal(Blue, Assert.IsType<SolidColorPaint>(
            Assert.IsType<RoundedRectangleGeometry>(Cell(table, 0, 0)).Fill).Color);
        Assert.Equal(Pink, Assert.IsType<SolidColorPaint>(
            Assert.IsType<RoundedRectangleGeometry>(Cell(table, 1, 0)).Fill).Color);

        var name = Assert.IsType<LabelGeometry>(Cell(table, 1, 1));
        var value = Assert.IsType<LabelGeometry>(Cell(table, 1, 2));
        Assert.Equal("Rosetta@home", name.Text);
        Assert.Equal("1,504,220", value.Text);
        Assert.Equal(TextSize, name.TextSize);
        Assert.Equal(TextSize, value.TextSize);

        // Only ONE header for a multi-series hover: every point shares the snapped day (§6).
        Assert.Single(card.Children.OfType<LabelGeometry>());
    }

    [Fact]
    public void Values_are_right_aligned_so_the_numeric_column_lines_up()
    {
        var card = Card(
            "2026-07-22",
            new FluentChartTooltip.CardRow("Einstein@Home", "4,182,000", Blue),
            new FluentChartTooltip.CardRow("LHC@home", "512", Pink));

        var table = Table(card);
        Assert.All([0, 1], row =>
            Assert.Equal(Align.End, Assert.Single(
                table.Cells, c => c.Row == row && c.Column == 2).HorizontalAlign));
    }

    [Fact]
    public void A_hover_with_no_date_renders_only_the_row_table()
    {
        var card = Card(null, new FluentChartTooltip.CardRow("Einstein@Home", "4,182,000", Blue));

        Assert.Single(card.Children);
        Assert.IsType<TableLayout>(card.Children[0]);
    }

    [Fact]
    public void A_series_that_opts_out_of_a_name_keeps_its_swatch_and_value()
    {
        // LiveCharts.IgnoreSeriesName maps to a null CardRow.Name — the label is omitted, but the
        // swatch and the value must still render.
        var card = Card("2026-07-22", new FluentChartTooltip.CardRow(null, "4,182,000", Blue));

        var table = Table(card);
        Assert.IsType<RoundedRectangleGeometry>(Cell(table, 0, 0));
        Assert.Equal("4,182,000", Assert.IsType<LabelGeometry>(Cell(table, 0, 2)).Text);
        Assert.DoesNotContain(table.Cells, c => c.Column == 1);
    }
}
