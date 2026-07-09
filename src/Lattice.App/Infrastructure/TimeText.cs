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
            < 60 => $"Updated {s}s ago",
            < 3600 => $"Updated {s / 60}m ago",
            _ => $"Updated {s / 3600}h ago",
        };
    }

    public static string RetryCountdown(DateTimeOffset nextAttemptAt, DateTimeOffset now, int attempt)
    {
        var s = Math.Max(0, (long)Math.Ceiling((nextAttemptAt - now).TotalSeconds));
        return $"Retrying in {s}s (attempt {attempt})";
    }
}
