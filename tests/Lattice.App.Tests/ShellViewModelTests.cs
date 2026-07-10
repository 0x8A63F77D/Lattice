using Lattice.App.Infrastructure;
using Lattice.App.Localization;
using Lattice.App.Tests.Fakes;
using Lattice.App.ViewModels;
using Lattice.Core;
using Lattice.Tests;
using Xunit;

namespace Lattice.App.Tests;

public class ShellViewModelTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}.json");
    private HostRegistry _registry = null!;
    private HostMonitorManager _manager = null!;
    private HostStore _store = null!;
    private ManualUiClock _clock = null!;
    private ShellViewModel _shell = null!;

    public ValueTask InitializeAsync()
    {
        _registry = new HostRegistry(new LatticeConfig(5, []), _path);
        _manager = new HostMonitorManager(_registry, () => new FakeGuiRpcClient(), TimeProvider.System);
        _store = new HostStore(_registry, _manager, new ImmediateUiDispatcher());
        _clock = new ManualUiClock();
        _shell = new ShellViewModel(_registry, _store, _clock, () => new FakeGuiRpcClient());
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _manager.DisposeAsync();
        File.Delete(_path);
    }

    [Fact]
    public void Has_four_views_and_starts_on_tasks()
    {
        Assert.Equal([Strings.NavTasks, Strings.NavProjects, Strings.NavTransfers, Strings.NavEventLog],
            _shell.Views.Select(v => v.Title).ToArray());
        Assert.Same(_shell.Views[0], _shell.SelectedView);
        Assert.Same(_shell.Views[0].Page, _shell.CurrentPage);
    }

    [Fact]
    public void First_run_flag_follows_host_count()
    {
        Assert.False(_shell.HasHosts);
        _registry.AddHost(TestData.MakeHostConfig());
        Assert.True(_shell.HasHosts);
    }

    [Fact]
    public void Rail_items_reconcile_with_registry_changes()
    {
        var a = TestData.MakeHostConfig(name: "a");
        var b = TestData.MakeHostConfig(name: "b");
        _registry.AddHost(a);
        _registry.AddHost(b);
        Assert.Equal(2, _shell.HostItems.Count);

        _registry.RemoveHost(a.Id);
        Assert.Equal("b", Assert.Single(_shell.HostItems).Name);
    }

    [Fact]
    public void Removing_a_host_disposes_its_rail_item_clock_subscription()
    {
        var host = TestData.MakeHostConfig();
        _registry.AddHost(host);
        Assert.Equal(1, _clock.SubscriberCount);

        _registry.RemoveHost(host.Id);
        Assert.Equal(0, _clock.SubscriberCount);
    }

    [Fact]
    public void Scope_defaults_to_all_hosts_and_survives_view_switches()
    {
        var host = TestData.MakeHostConfig();
        _registry.AddHost(host);
        Assert.True(_shell.Scope.IsAllHosts);

        _shell.Scope = new ScopeSelection(host.Id);
        _shell.SelectedView = _shell.Views[2];

        Assert.Equal(host.Id, _shell.Scope.HostId);
        Assert.Same(_shell.Views[2].Page, _shell.CurrentPage);
    }

    [Fact]
    public void SelectView_command_switches_by_index()
    {
        _shell.SelectViewCommand.Execute("3");
        Assert.Same(_shell.Views[3], _shell.SelectedView);
    }

    [Fact]
    public void NavigateToSettings_shows_settings_page_and_expands_target_host()
    {
        var host = TestData.MakeHostConfig();
        _registry.AddHost(host);

        _shell.NavigateToSettings(host.Id);

        Assert.Same(_shell.Settings, _shell.CurrentPage);
        Assert.True(_shell.Settings.Hosts.Single(h => h.HostId == host.Id).IsExpanded);
    }
}
