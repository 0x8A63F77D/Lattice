using Lattice.App.Localization;

namespace Lattice.App.Infrastructure;

/// <summary>
/// Display strings for the once-per-second UiClock tick. Copy matches
/// docs/design/m2/README.md verbatim. Pure — trivially testable.
/// </summary>
public static class TimeText
{
    public static string UpdatedAgo(DateTimeOffset updatedAt, DateTimeOffset now)
    {
        var s = Math.Max(0, (long)(now - updatedAt).TotalSeconds);
        return s switch
        {
            < 60 => string.Format(Strings.UpdatedSecondsFmt, s),
            < 3600 => string.Format(Strings.UpdatedMinutesFmt, s / 60),
            _ => string.Format(Strings.UpdatedHoursFmt, s / 3600),
        };
    }

    public static string RetryCountdown(DateTimeOffset nextAttemptAt, DateTimeOffset now, int attempt)
    {
        var s = Math.Max(0, (long)Math.Ceiling((nextAttemptAt - now).TotalSeconds));
        return string.Format(Strings.RailRetryingFmt, s, attempt);
    }

    /// <summary>Compact duration for Elapsed/Remaining cells: 45s · 3m 20s · 2h 05m · 1d 02h.</summary>
    public static string Duration(double seconds)
    {
        var s = (long)Math.Max(0, seconds);
        return s switch
        {
            < 60 => $"{s}s",
            < 3600 => $"{s / 60}m {s % 60:00}s",
            < 86400 => $"{s / 3600}h {s % 3600 / 60:00}m",
            _ => $"{s / 86400}d {s % 86400 / 3600:00}h",
        };
    }
}
