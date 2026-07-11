namespace Lattice.App.ViewModels;

/// <summary>
/// Pure decision policy for whether a Tasks DataGrid column should be visible:
/// an explicit user preference (set via the overflow menu) always wins; absent
/// one, width-based breakpoints decide for the two columns that have them, and
/// every other column defaults to visible. No I/O, no state — TasksView's
/// code-behind is the only caller, and UiStateStore persistence (Task 12) can
/// feed a loaded preference into <paramref name="userPreference"/> without any
/// change here.
/// </summary>
public static class ColumnVisibilityPolicy
{
    /// <summary>Below this view width, the Elapsed column auto-hides (absent an override).</summary>
    public const double ElapsedMinWidth = 1100;

    /// <summary>Below this view width, the Application column auto-hides (absent an override).</summary>
    public const double ApplicationMinWidth = 1000;

    /// <summary>
    /// Decides whether the column identified by <paramref name="columnKey"/> should be visible.
    /// </summary>
    /// <param name="columnKey">One of the Tasks DataGrid's hideable column tags ("Elapsed",
    /// "Application", etc.); unrecognized keys fall through to the always-visible default.</param>
    /// <param name="viewWidth">Current width of the Tasks view.</param>
    /// <param name="userPreference">Null when the user has never explicitly toggled this
    /// column; otherwise the explicit show/hide choice, which always wins over breakpoints.</param>
    public static bool IsVisible(string columnKey, double viewWidth, bool? userPreference)
    {
        if (userPreference is { } explicitPreference)
            return explicitPreference;

        return columnKey switch
        {
            "Elapsed" => viewWidth >= ElapsedMinWidth,
            "Application" => viewWidth >= ApplicationMinWidth,
            _ => true,
        };
    }
}
