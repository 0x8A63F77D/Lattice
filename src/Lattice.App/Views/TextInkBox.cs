using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Lattice.App.Views;

/// <summary>
/// Collapses a text element's layout box onto the INK box of the digits it shows, so that a
/// sibling icon centred in the same panel is centred on the glyphs the eye sees rather than on
/// a line box whose height is an accident of the font's ascent/descent metrics (issue #176).
///
/// WHY THIS EXISTS. <c>VerticalAlignment="Center"</c> centres each child's LAYOUT box
/// independently. A <see cref="Avalonia.Controls.TextBlock"/>'s box is a line box — ascent +
/// descent + line gap — while digits have no descenders, so the glyph ink does not sit at the
/// centre of that box. How far off it sits is a per-font constant, which is why "centre both
/// boxes" aligns in one font and misaligns in the next: measured in the snooze pill, the pause
/// bars sat 0.84 px below the digits in Helvetica (the family Avalonia's Skia font manager
/// returns as the macOS platform default, i.e. what the app actually ships with) and 1.31 px off
/// in Courier New, while in Inter — the family the visual-test harness pins — the same layout is
/// aligned to 0.03 px. Tuning a Margin against one font just moves that coincidence.
///
/// THE INVARIANT. After the returned margin is applied, the element's layout box IS the ink box
/// of <see cref="DigitBand"/> at the element's typeface and size. Two consequences follow by
/// construction, in any font: centring the box centres the ink, and a container sized to that box
/// (the pill's Border padding) is symmetric about the ink.
///
/// The band is measured from a FIXED digit repertoire, not from the live text, so a clock ticking
/// from "14:30" to "14:31" cannot resize the pill by the fraction of a pixel that separates one
/// digit's overshoot from another's.
///
/// Consumers must switch layout rounding OFF on the panel that centres the pair. The margin is
/// fractional by nature, and rounding each child's arrange position back to a whole pixel is the
/// second half of the same defect (it is what put the icon and the text box 0.5 px apart in
/// Helvetica and .AppleSystemUIFont).
/// </summary>
public sealed class TextInkCollapseConverter : IMultiValueConverter
{
    public static readonly TextInkCollapseConverter Instance = new();

    /// <summary>The glyph repertoire a "HH:mm" time can draw. Its ink box is the band the pill
    /// centres on — fixed, so the measurement does not change as the clock ticks.</summary>
    public const string DigitBand = "0123456789:";

    /// <summary>Binding order: FontFamily, FontSize, FontWeight, FontStyle — the four properties
    /// that move glyph ink relative to the line box. Anything unresolved (template init hands out
    /// <see cref="AvaloniaProperty.UnsetValue"/>) degrades to a zero margin, i.e. the plain
    /// line-box layout, rather than throwing.</summary>
    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values is [FontFamily family, double size, FontWeight weight, FontStyle style]
            ? CollapseMargin(new Typeface(family, style, weight), size)
            : default(Thickness);

    /// <summary>
    /// The margin that turns a text element's line box into the ink box of <see cref="DigitBand"/>.
    /// Negative where the line box overhangs the ink (the usual case: ascent above the digits,
    /// descent below them); positive on the bottom for a face whose figures descend past the
    /// baseline far enough to leave the line box (old-style figures, e.g. Georgia).
    /// </summary>
    public static Thickness CollapseMargin(Typeface typeface, double fontSize)
    {
        if (!(fontSize > 0) || double.IsNaN(fontSize) || double.IsInfinity(fontSize))
            return default;

        var text = new FormattedText(
            DigitBand, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, fontSize, Brushes.Black);

        // BuildGeometry is the only exact ink measurement Avalonia exposes: FontMetrics carries no
        // cap height, and GlyphTypeface.TryGetGlyphMetrics returns advances with a zeroed ink box.
        // Null means the run produced no outlines (a font with no digit coverage) — leave the box
        // alone rather than collapsing it to nothing.
        if (text.BuildGeometry(default)?.Bounds is not { Height: > 0 } ink)
            return default;

        return new Thickness(0, -ink.Top, 0, -(text.Height - ink.Bottom));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
