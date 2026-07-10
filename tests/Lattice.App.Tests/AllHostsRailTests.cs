using Lattice.App.Infrastructure;
using Lattice.App.Localization;
using Lattice.App.Tests.Fakes;
using Lattice.App.ViewModels;
using Lattice.Core;
using Lattice.Tests;
using Xunit;

namespace Lattice.App.Tests;

public class AllHostsRailTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}.json");
    private HostRegistry _registry = null!;
    private HostMonitorManager _manager = null!;
    private HostStore _store = null!;
    private ManualUiClock _clock = null!;

    public ValueTask InitializeAsync()
    {
        _registry = new HostRegistry(new LatticeConfig(5, []), _path);
        _manager = new HostMonitorManager(_registry, () => new FakeGuiRpcClient(), TimeProvider.System);
        _store = new HostStore(_registry, _manager, new ImmediateUiDispatcher());
        _clock = new ManualUiClock();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _manager.DisposeAsync();
        File.Delete(_path);
    }

    private ShellViewModel MakeShell() => new(_registry, _store, _clock, () => new FakeGuiRpcClient());

    [Fact]
    public void Rail_entries_lead_with_the_all_hosts_sentinel()
    {
        _registry.AddHost(TestData.MakeHostConfig());
        var shell = MakeShell();
        Assert.IsType<AllHostsRailItemViewModel>(shell.RailEntries[0]);
        Assert.IsType<HostRailItemViewModel>(shell.RailEntries[1]);
        Assert.Equal(2, shell.RailEntries.Count);
    }

    [Fact]
    public void Selecting_a_host_scopes_and_selecting_all_hosts_unscopes()
    {
        var host = TestData.MakeHostConfig();
        _registry.AddHost(host);
        var shell = MakeShell();
        shell.SelectedRailEntry = shell.RailEntries[1];
        Assert.Equal(host.Id, shell.Scope.HostId);
        shell.SelectedRailEntry = shell.RailEntries[0];
        Assert.True(shell.Scope.IsAllHosts);
    }

    [Fact]
    public void All_hosts_subtext_reports_partial_connectivity()
    {
        _registry.AddHost(TestData.MakeHostConfig(name: "a"));
        _registry.AddHost(TestData.MakeHostConfig(name: "b"));
        var shell = MakeShell();
        var all = (AllHostsRailItemViewModel)shell.RailEntries[0];
        // Both hosts Disconnected (manager not started): 0 of 2 connected.
        Assert.Equal(string.Format(Strings.AllHostsPartialFmt, 0, 2), all.Subtext);
    }

    [Fact]
    public void Removing_the_scoped_host_resets_scope_to_all_hosts()
    {
        var host = TestData.MakeHostConfig();
        _registry.AddHost(host);
        var shell = MakeShell();
        shell.SelectedRailEntry = shell.RailEntries[1];
        Assert.Equal(host.Id, shell.Scope.HostId);

        _registry.RemoveHost(host.Id);

        Assert.True(shell.Scope.IsAllHosts);
        Assert.Same(shell.RailEntries[0], shell.SelectedRailEntry);
    }
}
