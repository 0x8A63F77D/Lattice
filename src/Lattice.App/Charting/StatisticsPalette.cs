using Avalonia.Media;
using SkiaSharp;

namespace Lattice.App.Charting;

/// <summary>
/// The Fluent UI charting <c>DataVizPalette</c> qualitative.1–10 series colours (design
/// contract §2), identical in light and dark. This is the SINGLE home of the series hex:
/// the chart line/marker paints (SkiaSharp) and the legend chip swatches (Avalonia) both
/// read it, so no second copy can drift (implementer warnings #1/#7). A series' colour is
/// a pure function of its <em>daemon-list ordinal</em> — never its visibility rank — so
/// toggling a legend chip never recolours a line.
/// </summary>
public static class StatisticsPalette
{
    // qualitative.1–6 are the visible-at-once set (the ≤6 cap keeps colours from
    // repeating); 7–10 continue the official palette if a later batch raises the cap.
    // Never invent colours beyond these (contract §2).
    private static readonly string[] Hex =
    [
        "#637CEF", // 1 Cornflower
        "#E3008C", // 2 Hot pink
        "#2AA0A4", // 3 Teal
        "#9373C0", // 4 Orchid
        "#13A10E", // 5 Light green
        "#3A96DD", // 6 Light blue
        "#CA5010", // 7
        "#57811B", // 8
        "#B146C2", // 9
        "#AE8C00", // 10
    ];

    /// <summary>
    /// Palette slot for a project ordinal, wrapping modulo the palette length.
    /// <para>KNOWN LIMITATION (issue #171, raised by review on PR #167): on a host with &gt;10
    /// projects two visible series whose ordinals differ by a multiple of 10 get the SAME colour.
    /// The ≤6 visible cap does NOT prevent this — it bounds how many series show, not how far
    /// apart their ordinals are — so the contract §2 claim "the cap means colours never repeat"
    /// is unsound past ten projects. Nor is it rare: an 11-project host defaults to the top 6 by
    /// RAC, and if RAC order is unrelated to list order the chance ordinals 0 and 10 are both
    /// visible is C(9,4)/C(11,6) = 3/11 ≈ 27%.</para>
    /// <para>It is left as-is deliberately: past ten projects the contract's rules are jointly
    /// unsatisfiable — colour must be a pure function of ordinal (toggling never recolours),
    /// colours must never repeat, and no colour may be invented. Something has to give, and which
    /// one is a design call, tracked on #171. Negative ordinals never occur but are folded for
    /// totality.</para>
    /// </summary>
    public static int Slot(int ordinal) => ((ordinal % Hex.Length) + Hex.Length) % Hex.Length;

    /// <summary>SkiaSharp colour for the chart line and marker paints.</summary>
    public static SKColor SkColor(int ordinal) => SKColor.Parse(Hex[Slot(ordinal)]);

    /// <summary>Avalonia colour for the legend chip swatch (same hex as the line).</summary>
    public static Color Color(int ordinal) => Avalonia.Media.Color.Parse(Hex[Slot(ordinal)]);

    /// <summary>Solid brush for the legend chip swatch.</summary>
    public static IBrush Brush(int ordinal) => new SolidColorBrush(Color(ordinal));
}
