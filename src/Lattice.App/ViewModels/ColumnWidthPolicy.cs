namespace Lattice.App.ViewModels;

/// <summary>
/// Pure decision core for DataGrid column-width persistence (issue #120): what
/// counts as a usable persisted width, how a per-view+per-column key is built,
/// and which measured widths are worth writing back. No I/O, no framework
/// types — ColumnWidthPersistence is the only caller and feeds it plain
/// dictionaries so the logic stays exhaustively unit-testable.
///
/// Keying mirrors the visibility preference (stable, non-localized column tags)
/// but namespaces every key by its view, because the four data grids share
/// column names ("Project", "Host", "State", "Progress") and a flat key space
/// would cross-wire them.
/// </summary>
public static class ColumnWidthPolicy
{
    /// <summary>Floor for a restorable pixel width. Matches the DataGrid's own
    /// default minimum column width; a narrower persisted value is treated as
    /// garbage (hand-edited config) and ignored rather than applied.</summary>
    public const double MinValidWidth = 20.0;

    /// <summary>Ceiling for a restorable pixel width — a generous bound that a
    /// real drag never reaches; anything larger is treated as garbage.</summary>
    public const double MaxValidWidth = 10000.0;

    /// <summary>
    /// A persisted width is usable only if it is finite and within the sane
    /// pixel band. This is the semantic guard that runs AFTER deserialization:
    /// non-numeric / NaN / Infinity JSON tokens are already rejected loudly by
    /// System.Text.Json (Load then falls back to defaults), so what reaches here
    /// is a syntactically valid double that may still be nonsense (≤0, negative,
    /// absurdly large from a hand edit). Such a value is ignored per-key, never
    /// applied — a single bad entry must not break layout or reset unrelated
    /// preferences.
    /// </summary>
    public static bool IsValidWidth(double width) =>
        double.IsFinite(width) && width >= MinValidWidth && width <= MaxValidWidth;

    /// <summary>Composite persistence key: "{viewKey}/{columnKey}".</summary>
    public static string Key(string viewKey, string columnKey) => $"{viewKey}/{columnKey}";

    /// <summary>
    /// Selects a width to apply on load: present in <paramref name="persisted"/>
    /// AND valid. An absent or garbage entry yields false, leaving the column at
    /// its XAML default.
    /// </summary>
    public static bool TryGetRestoreWidth(
        IReadOnlyDictionary<string, double> persisted, string key, out double width)
    {
        if (persisted.TryGetValue(key, out width) && IsValidWidth(width))
            return true;
        width = 0;
        return false;
    }

    /// <summary>
    /// The subset of <paramref name="measured"/> widths worth persisting: valid,
    /// and differing (beyond <paramref name="epsilon"/>) from the value the store
    /// already reflects for that key — which is the persisted value if one exists,
    /// otherwise the column's XAML default. This is what keeps a fresh render from
    /// writing every column's default (no persisted value + width == default ⇒ no
    /// change), while still recording a genuine resize, including a resize back to
    /// the default over a previously persisted value.
    /// </summary>
    public static Dictionary<string, double> ComputeWrites(
        IReadOnlyDictionary<string, double> measured,
        IReadOnlyDictionary<string, double> persisted,
        IReadOnlyDictionary<string, double> defaults,
        double epsilon = 0.5)
    {
        var writes = new Dictionary<string, double>();
        foreach (var (key, width) in measured)
        {
            if (!IsValidWidth(width))
                continue;
            double baseline = persisted.TryGetValue(key, out var saved)
                ? saved
                : defaults.GetValueOrDefault(key, width);
            if (Math.Abs(width - baseline) > epsilon)
                writes[key] = width;
        }
        return writes;
    }
}
