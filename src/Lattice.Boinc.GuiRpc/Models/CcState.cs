using System.Xml.Linq;

namespace Lattice.Boinc.GuiRpc;

/// <summary>Full client state snapshot (get_state). Several MB on busy hosts — fetch once per connection, then poll deltas.</summary>
public sealed record CcState(
    VersionInfo CoreClientVersion,
    HostInfo? HostInfo,
    IReadOnlyList<Project> Projects,
    IReadOnlyList<App> Apps,
    IReadOnlyList<AppVersion> AppVersions,
    IReadOnlyList<Workunit> Workunits,
    IReadOnlyList<Result> Results)
{
    internal static CcState Parse(XElement e) => new(
        new VersionInfo(
            ParseHelpers.GetInt(e, "core_client_major_version"),
            ParseHelpers.GetInt(e, "core_client_minor_version"),
            ParseHelpers.GetInt(e, "core_client_release")),
        e.Element("host_info") is XElement host ? HostInfo.Parse(host) : null,
        [.. e.Elements("project").Select(Project.Parse)],
        [.. e.Elements("app").Select(App.Parse)],
        [.. e.Elements("app_version").Select(AppVersion.Parse)],
        [.. e.Elements("workunit").Select(Workunit.Parse)],
        [.. e.Elements("result").Select(Result.Parse)]);
}
