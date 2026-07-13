using Lattice.App.Infrastructure;
using Lattice.App.Localization;
using Lattice.App.Tests.Fakes;
using Lattice.App.ViewModels;
using Lattice.Core;
using Lattice.Tests;
using Microsoft.Extensions.Time.Testing;
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
        _manager = new HostMonitorManager(_registry, () => new FakeGuiRpcClient(), new FakeTimeProvider());
        _store = new HostStore(_registry, _manager, new ImmediateUiDispatcher());
        _clock = new ManualUiClock();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _manager.DisposeAsync();
        File.Delete(_path);
    }

    private ShellViewModel MakeShell() => new(_registry, _store, _clock,
        new UiStateStore(Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}-ui.json")),
        () => new FakeGuiRpcClient());

    [Fact]
    public void Rail_entries_lead_with_the_all_hosts_sentinel()
    {
        // A lone host now renders as SingleHost (no sentinel); two hosts + a tall viewport
        // give a Flat rail: sentinel at 0, host rows following.
        _registry.AddHost(TestData.MakeHostConfig(name: "a"));
        _registry.AddHost(TestData.MakeHostConfig(name: "b"));
        var shell = MakeShell();
        shell.SetRailViewportHeight(1000.0);
        Assert.IsType<AllHostsRailItemViewModel>(shell.RailEntries[0]);
        Assert.IsType<HostRailItemViewModel>(shell.RailEntries[1]);
        Assert.Equal(3, shell.RailEntries.Count);
    }

    [Fact]
    public void Selecting_a_host_scopes_and_selecting_all_hosts_unscopes()
    {
        var host = TestData.MakeHostConfig(name: "a");
        _registry.AddHost(host);
        _registry.AddHost(TestData.MakeHostConfig(name: "b"));
        var shell = MakeShell();
        shell.SetRailViewportHeight(1000.0);   // Flat: [0]=sentinel, [1]=host "a"
        shell.SelectHostScope(host.Id);        // the click gesture's VM entry point
        Assert.Equal(host.Id, shell.Scope.HostId);
        shell.SelectAllHostsScope();
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
        // Three hosts so that after removing the scoped one, two remain → still Flat with the
        // sentinel leading (removing down to one host would flip to SingleHost, no sentinel).
        var host = TestData.MakeHostConfig(name: "a");
        _registry.AddHost(host);
        _registry.AddHost(TestData.MakeHostConfig(name: "b"));
        _registry.AddHost(TestData.MakeHostConfig(name: "c"));
        var shell = MakeShell();
        shell.SetRailViewportHeight(1000.0);   // Flat: [0]=sentinel, [1]=host "a"
        shell.SelectHostScope(host.Id);
        Assert.Equal(host.Id, shell.Scope.HostId);

        _registry.RemoveHost(host.Id);

        Assert.True(shell.Scope.IsAllHosts);
        Assert.Same(shell.RailEntries[0], shell.SelectedRailEntry);
    }
}
