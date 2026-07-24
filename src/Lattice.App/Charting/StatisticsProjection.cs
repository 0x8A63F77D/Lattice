using Lattice.App.Aggregation;
using Lattice.Boinc.GuiRpc;
using Microsoft.FSharp.Collections;

namespace Lattice.App.Charting;

/// <summary>
/// The single GuiRpc → F# projection for the Statistics chart: joins a host's daemon project
/// list to its credit history into <see cref="ProjectHistory"/> records. Ordinal is the
/// project's index in the daemon project list (the stable colour key, §2); RAC is the live
/// per-host <c>HostExpavgCredit</c>. Only projects with at least one daily record are chartable.
/// Shared by the ViewModel and the snapshot harness so both see identical histories.
/// </summary>
public static class StatisticsProjection
{
    public static List<ProjectHistory> FromProjects(
        IReadOnlyList<Project> projects, IReadOnlyList<ProjectStatistics> statistics)
    {
        var statsByUrl = new Dictionary<string, ProjectStatistics>();
        foreach (var s in statistics)
            statsByUrl.TryAdd(s.MasterUrl, s);

        var histories = new List<ProjectHistory>();
        var seen = new HashSet<string>();
        for (int ordinal = 0; ordinal < projects.Count; ordinal++)
        {
            var project = projects[ordinal];
            // Lenient-parse guard: a malformed reply can carry the same master_url twice. Keep the
            // first (its ordinal is the colour key), like ProjectRows.compute — else one BOINC
            // project would render duplicate chips/series and inflate the project count.
            if (!seen.Add(project.MasterUrl))
                continue;
            if (!statsByUrl.TryGetValue(project.MasterUrl, out var stats) || stats.Daily.Count == 0)
                continue;
            var daily = stats.Daily
                .Select(d => new DailyCredit(d.Day, d.UserTotalCredit, d.UserExpavgCredit, d.HostTotalCredit, d.HostExpavgCredit))
                .ToList();
            // A blank ProjectName (BOINC can report one) would render an empty legend chip and
            // series label — fall back to the MasterUrl, as ProjectRows.compute's DisplayName does.
            var name = string.IsNullOrEmpty(project.ProjectName) ? project.MasterUrl : project.ProjectName;
            histories.Add(new ProjectHistory(
                project.MasterUrl, name, ordinal, project.HostExpavgCredit, ListModule.OfSeq(daily)));
        }

        return histories;
    }
}
