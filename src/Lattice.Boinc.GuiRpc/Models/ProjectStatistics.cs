using System.Xml.Linq;

namespace Lattice.Boinc.GuiRpc;

/// <summary>
/// Credit history for one attached project, from get_statistics. The daemon keeps a
/// <c>&lt;daily_statistics&gt;</c> record per day on disk (trimmed to save_stats_days);
/// each carries the day plus the four credit quantities the official Manager charts.
/// MasterUrl is the stable identity key, matching <see cref="Project.MasterUrl"/>.
/// </summary>
public sealed record ProjectStatistics(
    string MasterUrl,
    IReadOnlyList<DailyStatistics> Daily)
{
    internal static ProjectStatistics Parse(XElement e) => new(
        ParseHelpers.GetString(e, "master_url"),
        [.. e.Elements("daily_statistics").Select(DailyStatistics.Parse)]);
}

/// <summary>
/// One day's credit snapshot within a <see cref="ProjectStatistics"/> series.
/// <see cref="Day"/> is the day bucket the daemon emits as a Unix-time value; the four
/// credit fields are cumulative totals and exponential running averages (RAC) for the
/// user and this host, as last reported by the project server.
/// </summary>
public sealed record DailyStatistics(
    DateTimeOffset Day,
    double UserTotalCredit,
    double UserExpavgCredit,
    double HostTotalCredit,
    double HostExpavgCredit)
{
    internal static DailyStatistics Parse(XElement e) => new(
        // The daemon writes <day> as C-double seconds since the Unix epoch (client/project.cpp,
        // PROJECT::write_statistics). Convert via milliseconds to keep any fractional second,
        // matching ParseHelpers.GetTimestamp; a real record always carries a positive day.
        DateTimeOffset.FromUnixTimeMilliseconds((long)(ParseHelpers.GetDouble(e, "day") * 1000)),
        ParseHelpers.GetDouble(e, "user_total_credit"),
        ParseHelpers.GetDouble(e, "user_expavg_credit"),
        ParseHelpers.GetDouble(e, "host_total_credit"),
        ParseHelpers.GetDouble(e, "host_expavg_credit"));
}
