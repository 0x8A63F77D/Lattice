using Avalonia.Media;
using SkiaSharp;

namespace Lattice.App.Charting;

/// <summary>
/// The Fluent UI charting <c>DataVizPalette</c> qualitative.1–10 series colours (design
/// contract §2), identical in light and dark. This is the SINGLE home of the series hex:
/// the chart line/marker paints (SkiaSharp) and the legend chip swatches (Avalonia) both
/// read it, so no second copy can drift (implementer warnings #1/#7).
/// <para>This class is a pure lookup table, indexed by the palette SLOT a visible series
/// holds. It does not decide which series gets which slot — that is
/// <see cref="SeriesColors"/>' job (issue #171). The class used to fold a project's
/// daemon ordinal into the palette itself, which is precisely how two projects past the
/// tenth came to own one colour before any visibility decision was made.</para>
/// </summary>
public static class StatisticsPalette
{
    // qualitative.1–10, the official set. Never invent colours beyond these (contract §2);
    // the ≤6 visible cap stays below this length so every series on the chart holds its own.
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
    /// How many slots the palette has. Pinned equal to <c>SeriesColors.paletteSize</c>, which is
    /// what the allocator allocates within (guarded by a test).
    /// </summary>
    public static int SlotCount => Hex.Length;

    /// <summary>
    /// The hex for a palette slot. Throws on an out-of-range slot rather than wrapping: wrapping
    /// is exactly the silent aliasing of issue #171, and the only ints that reach here are
    /// allocator output, which is in range by construction.
    /// </summary>
    private static string HexFor(int slot) =>
        slot >= 0 && slot < Hex.Length
            ? Hex[slot]
            : throw new ArgumentOutOfRangeException(
                nameof(slot), slot, $"Palette slot must be in [0, {Hex.Length}); slots come from SeriesColors.allocate.");

    /// <summary>SkiaSharp colour for the chart line and marker paints.</summary>
    public static SKColor SkColor(int slot) => SKColor.Parse(HexFor(slot));

    /// <summary>Avalonia colour for the legend chip swatch (same hex as the line).</summary>
    public static Color Color(int slot) => Avalonia.Media.Color.Parse(HexFor(slot));

    /// <summary>Solid brush for the legend chip swatch of a VISIBLE series.</summary>
    public static IBrush Brush(int slot) => new SolidColorBrush(Color(slot));
}
