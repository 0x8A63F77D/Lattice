using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Lattice.App.Localization;

namespace Lattice.App.Views;

/// <summary>
/// Collapses a text element's layout box onto the INK box of a fixed reference BAND, so that a
/// sibling icon centred in the same panel is centred on the glyphs the eye sees rather than on
/// a line box whose height is an accident of the font's ascent/descent metrics (issues #176, #180).
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
/// THE INVARIANT. After the returned margin is applied, the element's layout box IS the ink box of
/// the converter's BAND at the element's typeface and size. Two consequences follow by
/// construction, in any font: centring the box centres the band, and a container sized to that box
/// (the pill's Border padding) is symmetric about it.
///
/// The band is always a FIXED glyph run, never the live text. For the pill that keeps a clock
/// ticking from "14:30" to "14:31" from resizing it by the fraction of a pixel that separates one
/// digit's overshoot from another's; for the status cells (issue #180) it is what keeps the icon
/// still while the status word changes under it — see <see cref="Words"/>.
///
/// Consumers must switch layout rounding OFF on the panel that centres the pair. The margin is
/// fractional by nature, and rounding each child's arrange position back to a whole pixel is the
/// second half of the same defect (it is what put the icon and the text box 0.5 px apart in
/// Helvetica and .AppleSystemUIFont).
/// </summary>
public sealed class TextInkCollapseConverter : IMultiValueConverter
{
    /// <summary>The digit converter: the band is the digit repertoire (see <see cref="DigitBand"/>).
    /// Used by the snooze pill and by any cell whose text is figures only — the Tasks Deadline cell,
    /// whose "MM-dd HH:mm" is formatted with the invariant culture and so is digits in every
    /// locale. Named <c>Instance</c> because it was the only one when the pill introduced it
    /// (#176/#179); the name is kept so that pill markup is not disturbed.</summary>
    public static readonly TextInkCollapseConverter Instance = new(() => DigitBand);

    /// <summary>
    /// The status cells' converter: the band is the one the UI's own script reads against — the
    /// cap band for Latin, the ideograph em band for Han/Kana/Hangul (see <see cref="WordBandFor"/>).
    ///
    /// WHY A DIFFERENT BAND FROM THE PILL'S (issue #180). The pill shows digits, so its own ink IS
    /// a fixed band. A status cell shows WORDS, and the words change: "Running" has a descender
    /// where "Active" has none, so a band read off the live text would re-centre the cell on every
    /// status change — measured under exactly that mutation, 1.24 px of vertical shift between
    /// "Active" and "Suspended" (it moves whichever of the two is not the panel's tallest child, so
    /// at 12 px text beside a 12 px icon it is the LABEL that slides). A twitching cell traded for
    /// a mis-centred one. The band here is therefore independent of the displayed string: it is a
    /// fixed reference glyph, and it is the one the eye aligns against.
    ///
    /// WHY CAP HEIGHT AND NOT X-HEIGHT, for Latin. Every status string is sentence case ("Running",
    /// "No new tasks"), so the tallest thing on the line at the icon's shoulder is a capital;
    /// centring a 12 px glyph on the cap band is the same rule Fluent/Material/Carbon all state for
    /// icon-beside-label. An x-height band would seat the icon ~0.6 px lower and let its box
    /// overhang the ascenders on both sides, which reads as a sunk icon next to a capital.
    /// </summary>
    /// <remarks>The reference string is a status these very cells display, pulled from the same
    /// resource set at the same moment, so its script IS the labels' script whatever resolved.</remarks>
    public static readonly TextInkCollapseConverter Words = new(() => WordBandFor(Strings.TaskStateRunning));

    /// <summary>The glyph repertoire a "HH:mm" time can draw. Its ink box is the band the pill
    /// centres on — fixed, so the measurement does not change as the clock ticks.</summary>
    public const string DigitBand = "0123456789:";

    /// <summary>
    /// The cap-height reference: baseline to cap top, measured on the one letter that defines it.
    /// "H" is flat-topped and flat-footed, so its ink box IS the cap band — a round letter (O, S)
    /// would add the type designer's overshoot at both ends, and a lowercase one would measure the
    /// x-height band instead.
    /// </summary>
    public const string CapBand = "H";

    /// <summary>
    /// The reference for scripts with no cap height, whose glyphs instead fill the em square:
    /// Han, Kana, Hangul. "国" is a full-width ideograph with flat top and bottom strokes, so its
    /// ink is the band those scripts read against.
    /// </summary>
    public const string IdeographBand = "国";

    /// <summary>
    /// The band a WORD label is read against, given <paramref name="resolvedLabel"/> — any ONE
    /// string from the same resource set the cells display.
    ///
    /// THIS IS NOT COSMETIC (Codex P2 on PR #184). Lattice ships a zh-CN UI whose statuses are Han
    /// ("运行中", "活动中", "传输中"). Those have no cap height, and — measured in Helvetica at 12 px,
    /// the shipping face — they do not even share the Latin line box: "H" reports a line height of
    /// 12.000 while "运行中" falls back to a CJK face and reports 16.800. Collapsing a 16.8 px line
    /// box by a margin derived from a 12.0 px one puts the label nowhere near the icon.
    ///
    /// WHY A RESOLVED STRING AND NOT THE UI CULTURE (Codex P2, round 2). The culture is not the
    /// question — the SCRIPT THAT RESOLVED is. With the language preference on System,
    /// <see cref="Lattice.App.Infrastructure.LanguageCulture.Resolve"/> deliberately leaves the OS
    /// culture in place, and the app ships only neutral-English and zh-CN resource sets: a ja-JP,
    /// ko-KR or zh-TW machine therefore displays the ENGLISH statuses while its culture reads as
    /// CJK. Branching on the culture would hand those users an ideograph band for Latin labels —
    /// the very mismatch this method exists to remove, in the other direction. Asking a string that
    /// actually came out of the resource set cannot get that wrong.
    ///
    /// This still does not make the band depend on the LABEL: the caller passes a fixed reference
    /// string, not the status on screen, so the band cannot change as the status changes — the
    /// invariant the whole mechanism exists to hold.
    /// </summary>
    public static string WordBandFor(string resolvedLabel) =>
        resolvedLabel.Any(c => char.GetUnicodeCategory(c) == UnicodeCategory.OtherLetter)
            ? IdeographBand
            : CapBand;

    private readonly Func<string> _band;

    private TextInkCollapseConverter(Func<string> band) => _band = band;

    /// <summary>The fixed glyph run whose ink box this converter collapses the text's box onto.
    /// Fixed with respect to the LABEL — a converter may still resolve it from the UI culture, which
    /// does not change while the app runs.</summary>
    public string Band => _band();

    /// <summary>Binding order: FontFamily, FontSize, FontWeight, FontStyle — the four properties
    /// that move glyph ink relative to the line box. Anything unresolved (template init hands out
    /// <see cref="AvaloniaProperty.UnsetValue"/>) degrades to a zero margin, i.e. the plain
    /// line-box layout, rather than throwing.</summary>
    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values is [FontFamily family, double size, FontWeight weight, FontStyle style]
            ? CollapseMargin(Band, new Typeface(family, style, weight), size)
            : default(Thickness);

    /// <summary>
    /// The margin that turns a text element's line box into the ink box of <paramref name="band"/>.
    /// Negative where the line box overhangs the ink (the usual case: ascent above the band, descent
    /// below it); positive on the bottom for a band that descends past the baseline far enough to
    /// leave the line box (old-style figures, e.g. Georgia's).
    /// </summary>
    public static Thickness CollapseMargin(string band, Typeface typeface, double fontSize)
    {
        if (!(fontSize > 0) || double.IsNaN(fontSize) || double.IsInfinity(fontSize))
            return default;

        var text = new FormattedText(
            band, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, fontSize, Brushes.Black);

        // BuildGeometry is the only exact ink measurement Avalonia exposes: FontMetrics carries no
        // cap height, and GlyphTypeface.TryGetGlyphMetrics returns advances with a zeroed ink box.
        // Null means the run produced no outlines (a font that covers neither the digits nor the
        // cap letter) — leave the box alone rather than collapsing it to nothing.
        if (text.BuildGeometry(default)?.Bounds is not { Height: > 0 } ink)
            return default;

        return new Thickness(0, -ink.Top, 0, -(text.Height - ink.Bottom));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
