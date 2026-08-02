using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Avalonia.Media;
using Xunit;

namespace Lattice.VisualTests;

/// <summary>
/// The vocabulary shared by the two gates on the issue-#180 defect class —
/// <see cref="StatusCellAlignmentTests"/> (the four data cells) and
/// <see cref="ChromeAlignmentTests"/> (the command bars, the Computing button, the legend chips).
///
/// It lives in one place on purpose. The defect is a single invariant — a fixed-size MARK centred
/// beside a label must sit on the label's reference BAND, not on its line box — and the project's
/// rule is that a second instance of a pattern gets the machinery extracted rather than
/// transcribed. Everything here is what the two gates would otherwise each own a copy of: which
/// font families are worth probing, how a family is proved present, and how painted ink is
/// measured out of a captured frame.
/// </summary>
internal static class InkAlignment
{
    /// <summary>The arranged geometry is computed, not rasterised, so it is exact up to floating
    /// point; a thousandth of a pixel is slack for the arithmetic, not for the layout.</summary>
    public const double ArrangedTolerance = 0.001;

    /// <summary>
    /// TWO DEVICE PIXELS, in DIPs. Hinting grid-fits a glyph's top and bottom edges onto the device
    /// pixel grid independently and may shift the whole run as it does so, so each edge can move by
    /// up to a device pixel and the ink CENTRE these gates measure — their midpoint — inherits that
    /// same bound in each direction. The cap is the mechanism's, not a fit to the observations
    /// (#184 measured a worst case of 0.852 px @1x / 0.311 px @2x on macOS, 1.194 px @1x on
    /// Windows, whose hinting is the more aggressive; a one-device-pixel cap was tried first and
    /// was red on the Windows runner alone).
    /// </summary>
    public static double MaxInkDeviationDip(double scaling) => 2.0 / scaling;

    /// <summary>Families worth probing: metrically different from each other and from Inter. Ones
    /// this runner lacks are skipped (see <see cref="Resolve"/>); Inter is embedded, so at least it
    /// is always measured — and the arranged assertions are red pre-fix in Inter too, so the gates
    /// do not go vacuous on a runner with a bare font stack.</summary>
    public static readonly string[] FamilyNames =
    [
        "Inter", "Helvetica", "Helvetica Neue", ".AppleSystemUIFont", "Arial",
        "Verdana", "Georgia", "Courier New", "Times New Roman", "Menlo", "Trebuchet MS", "Segoe UI",
    ];

    /// <summary>The family, or null when this runner does not have it. A font manager that cannot
    /// find a family silently substitutes the default one — which would re-probe Inter under another
    /// name and manufacture a green.</summary>
    public static FontFamily? Resolve(string family)
    {
        var candidate = new FontFamily(family);
        if (!FontManager.Current.TryGetGlyphTypeface(new Typeface(candidate), out var typeface))
            return null;
        if (typeface.FamilyName == family)
            return candidate;
        return family.StartsWith('.') && typeface.FamilyName != FontManager.Current.DefaultFontFamily.Name
            ? candidate
            : null;
    }

    /// <summary>
    /// The ink box of <paramref name="content"/> drawn in <paramref name="text"/>'s own typeface and
    /// size. <c>BuildGeometry</c> is the only exact ink measurement Avalonia exposes: FontMetrics
    /// carries no cap height and <c>GlyphTypeface.TryGetGlyphMetrics</c> returns advances with a
    /// zeroed ink box.
    /// </summary>
    public static Rect InkOf(TextBlock text, string content) =>
        new FormattedText(content, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight), text.FontSize,
            Brushes.Black).BuildGeometry(default)!.Bounds;

    /// <summary>The modal (most common) colour over a device-pixel rectangle — the surface a mark
    /// is painted on, taken from the region the mark sits in rather than assumed from a token, so a
    /// probe inside a tinted pill measures against the pill's fill and not the bar's.</summary>
    public static (int r, int g, int b) Modal(PixelBuffer pixels, int x0, int x1, int y0, int y1)
    {
        var histogram = new Dictionary<(int, int, int), int>();
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
                histogram[pixels.Rgb(x, y)] = histogram.GetValueOrDefault(pixels.Rgb(x, y)) + 1;
        return histogram.MaxBy(entry => entry.Value).Key;
    }

    /// <summary>
    /// Sub-pixel ink extents and coverage centre for one element's column range. Coverage is a
    /// pixel's distance from the surface fill normalised by the strongest ink in the same columns,
    /// so a partly covered edge row's coverage IS the fraction of that row the mark covers — which
    /// is what makes the extents sub-pixel instead of integer-quantised. The centre is the midpoint
    /// of those extents (not the coverage centroid): the two elements have different ink densities,
    /// and a centroid would weigh a bold word's mass against a thin outline's.
    /// </summary>
    public static (double Top, double Bottom, double Centre) Extent(
        PixelBuffer pixels, (int r, int g, int b) background, Rect element, int y0, int y1, string what)
    {
        int x0 = (int)Math.Floor(element.Left), x1 = (int)Math.Ceiling(element.Right) - 1;

        int Distance(int x, int y)
        {
            var (r, g, b) = pixels.Rgb(x, y);
            return Math.Abs(r - background.r) + Math.Abs(g - background.g) + Math.Abs(b - background.b);
        }

        int strongest = 0;
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
                strongest = Math.Max(strongest, Distance(x, y));
        Assert.True(strongest > 0, $"{what} painted no ink in its own columns.");

        var rows = new double[y1 - y0 + 1];
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
                rows[y - y0] = Math.Max(rows[y - y0], Math.Clamp(Distance(x, y) / (double)strongest, 0, 1));

        // A floor above anti-aliasing noise but below any real glyph row (the pill gate's value).
        const double InkFloor = 0.10;
        int first = Array.FindIndex(rows, row => row > InkFloor);
        int last = Array.FindLastIndex(rows, row => row > InkFloor);
        Assert.True(first >= 0,
            $"no row in {what}'s columns x={x0}..{x1} cleared the {InkFloor:P0} ink floor, so its " +
            "extent cannot be measured — it painted nothing, or only anti-aliasing fringe.");

        double top = y0 + first + (1 - rows[first]), bottom = y0 + last + rows[last];
        return (top, bottom, (top + bottom) / 2);
    }

    /// <summary>A mark whose ink centre IS its box centre, so centring its BOX centres its ink.
    /// True of a <see cref="PathIcon"/> (Stretch=Uniform into a centred box, whatever the glyph's
    /// own asymmetry) and of the solid squares the legend chips use; NOT true of a progress rule or
    /// a themed control that carries padding of its own.</summary>
    public static bool IsGlyphSizedMark(Control control) =>
        control is PathIcon or Panel or Border
        // Both dimensions pinned at glyph scale AND roughly square. This is what separates a 12x12
        // swatch from the 56x3 progress rule, which is not a mark and whose seat against text is a
        // separate design question — the exclusion #180 already drew.
        && Sized(control.Width) && Sized(control.Height)
        && Math.Max(control.Width, control.Height) <= 2 * Math.Min(control.Width, control.Height);

    /// <summary>
    /// The glyph-sized box <paramref name="child"/> actually PAINTS, or null when it is not a mark
    /// at all. Issue #204 — the Statistics overflow row seats a <c>CheckBox</c>, not a bare box.
    ///
    /// TWO SHAPES QUALIFY. A child that IS a box — the legend swatch, a PathIcon — is its own mark.
    /// A child that WRAPS one resolves to the box its TEMPLATE paints: FluentAvalonia's CheckBox
    /// draws a 20x20 <c>NormalRectangle</c> inside a 32 px control box, and that square is the only
    /// thing the eye can seat on a band. Measuring the template's box rather than the control's is
    /// deliberate even though the two centres coincide here (measured: box centre 16.000, control
    /// centre 16.000) — the coincidence is the theme's to change, the painted square is not.
    ///
    /// WHAT THE WRAPPER MAY NOT DO is paint anything else: no text anywhere in its subtree, and
    /// exactly one glyph-sized box in it. That is what keeps a toolbar button, a combo box and a
    /// tinted pill — all of which carry content of their own, and whose boxes are theirs to lay out
    /// — out of the net, which is the same exclusion the panel sweep states in prose.
    /// </summary>
    public static Control? MarkOf(Control child)
    {
        if (IsGlyphSizedMark(child))
            return child;
        if (child is not TemplatedControl)
            return null;

        var painted = child.GetVisualDescendants().OfType<Control>().Where(c => c.IsVisible).ToList();
        if (painted.Any(c => c is TextBlock { Text.Length: > 0 }))
            return null;
        var marks = painted.Where(IsGlyphSizedMark).ToList();
        return marks.Count == 1 ? marks[0] : null;
    }

    private static bool Sized(double value) => value is >= 6 and <= 24;
}
