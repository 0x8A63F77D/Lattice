namespace Lattice.Core;

/// <summary>
/// Pure cadence decision for per-project credit history (issue #148, get_statistics).
/// The daemon gains at most one <c>daily_statistics</c> row per day, so refetching every
/// poll tick (seconds cadence) is waste. HostMonitor consults this once per tick with the
/// time the connection last fetched statistics; the fetch itself and the timestamp bookkeeping
/// live in the monitor's loop. Mirrors <see cref="PollingCadencePolicy"/>'s value-level,
/// clock-driven role — it participates in no concurrency invariant.
/// </summary>
public static class StatisticsCadencePolicy
{
    /// <summary>
    /// Low-frequency refresh interval. Six hours holds staleness under a quarter-day for a
    /// once-daily dataset — the new row is picked up the same day it appears — at ~4 fetches
    /// per day per host, negligible beside the seconds-cadence steady-state poll.
    /// </summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(6);

    /// <summary>
    /// Whether a statistics fetch is due. <paramref name="lastFetchedAt"/> is <c>null</c>
    /// before the first fetch of a connection, so a fresh connect or reconnect always fetches;
    /// thereafter a fetch is due once <see cref="RefreshInterval"/> has elapsed.
    /// </summary>
    public static bool ShouldRefresh(DateTimeOffset? lastFetchedAt, DateTimeOffset now) =>
        lastFetchedAt is not { } last || now - last >= RefreshInterval;
}
