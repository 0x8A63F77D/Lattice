using System.Xml.Linq;
using Xunit;
using Lattice.Boinc.GuiRpc;

namespace Lattice.Tests;

public class CcStateTests
{
    private static CcState Load()
    {
        var reply = XElement.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "get_state.xml")));
        return CcState.Parse(reply.Element("client_state")!);
    }

    [Fact]
    public void Parses_core_client_version()
    {
        Assert.Equal(new VersionInfo(8, 0, 4), Load().CoreClientVersion);
    }

    [Fact]
    public void Parses_host_info()
    {
        HostInfo? host = Load().HostInfo;
        Assert.NotNull(host);
        Assert.Equal("crunchbox", host!.DomainName);
        Assert.Equal(16, host.NCpus);
        Assert.Equal("Linux Ubuntu", host.OsName);
    }

    [Fact]
    public void Parses_projects_with_flags()
    {
        var projects = Load().Projects;
        Assert.Equal(2, projects.Count);
        Assert.Equal("Einstein@Home", projects[0].ProjectName);
        Assert.Equal(1234567.89, projects[0].UserTotalCredit, precision: 2);
        Assert.False(projects[0].SuspendedViaGui);
        Assert.True(projects[1].SuspendedViaGui);
        Assert.True(projects[1].DontRequestMoreWork);
    }

    [Fact]
    public void Parses_apps_app_versions_workunits_results()
    {
        CcState state = Load();
        Assert.Single(state.Apps);
        Assert.Equal("Gravitational Wave search O3 MDF", state.Apps[0].UserFriendlyName);
        Assert.Single(state.AppVersions);
        Assert.Equal(218, state.AppVersions[0].VersionNum);
        Assert.Single(state.Workunits);
        Assert.Equal("einstein_O3MDF", state.Workunits[0].AppName);
        Assert.Single(state.Results);
        Assert.Equal(ResultState.FilesDownloaded, state.Results[0].State);
    }

    [Fact]
    public void Empty_client_state_yields_empty_lists_not_throws()
    {
        CcState state = CcState.Parse(XElement.Parse("<client_state/>"));
        Assert.Empty(state.Projects);
        Assert.Empty(state.Results);
        Assert.Null(state.HostInfo);
        Assert.Equal(new VersionInfo(0, 0, 0), state.CoreClientVersion);
    }

    [Fact]
    public void Parses_project_resource_share()
    {
        CcState state = Load();
        Assert.Equal(100.0, state.Projects[0].ResourceShare);
        Assert.Equal(50.0, state.Projects[1].ResourceShare);
    }
}
