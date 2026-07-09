using Lattice.App.Infrastructure;
using Lattice.App.Tests.Fakes;
using Lattice.App.ViewModels;
using Lattice.Core;
using Lattice.Tests;
using Xunit;

namespace Lattice.App.Tests;

public class SettingsViewModelTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}.json");
    private HostRegistry _registry = null!;
    private HostMonitorManager _manager = null!;
    private HostStore _store = null!;
    private SettingsViewModel _settings = null!;

    public ValueTask InitializeAsync()
    {
        _registry = new HostRegistry(new LatticeConfig(5, []), _path);
        _manager = new HostMonitorManager(_registry, () => new FakeGuiRpcClient(), TimeProvider.System);
        _store = new HostStore(_registry, _manager, new ImmediateUiDispatcher());
        _settings = new SettingsViewModel(_registry, _store, () => new FakeGuiRpcClient());
        _store.Changed += (_, _) => _settings.Reconcile();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _manager.DisposeAsync();
        File.Delete(_path);
    }

    private HostSettingsItemViewModel AddHost(string name = "office-pc")
    {
        _registry.AddHost(TestData.MakeHostConfig(name: name));
        return _settings.Hosts[^1];
    }

    [Fact]
    public void Save_persists_edited_fields_to_the_registry()
    {
        var item = AddHost();
        item.Address = "192.168.1.40";
        item.PortText = "31417";
        item.Password = "different";

        item.SaveCommand.Execute(null);

        var saved = Assert.Single(_registry.Hosts);
        Assert.Equal("192.168.1.40", saved.Address);
        Assert.Equal(31417, saved.Port);
        Assert.Equal("different", saved.Password);
        Assert.Null(item.ValidationError);
    }

    [Theory]
    [InlineData("", "31416")]
    [InlineData("localhost", "not-a-port")]
    [InlineData("localhost", "0")]
    [InlineData("localhost", "70000")]
    public void Save_rejects_invalid_input_without_touching_the_registry(string address, string portText)
    {
        var item = AddHost();
        var before = _registry.Hosts[0];
        item.Address = address;
        item.PortText = portText;

        item.SaveCommand.Execute(null);

        Assert.NotNull(item.ValidationError);
        Assert.Equal(before, _registry.Hosts[0]);
    }

    [Fact]
    public async Task Test_connection_reports_success_with_daemon_version()
    {
        var item = AddHost();
        await item.TestConnectionCommand.ExecuteAsync(null);
        Assert.Equal("Connected — BOINC 8.2.0", item.TestResultText);
    }

    [Fact]
    public async Task Test_connection_reports_refused_password_without_echoing_it()
    {
        // A settings VM whose client factory refuses every password: the item
        // inherits the factory through SettingsViewModel's constructor.
        var refusing = new SettingsViewModel(_registry, _store,
            () => new FakeGuiRpcClient { OnAuthorize = _ => Task.FromResult(false) });
        _registry.AddHost(TestData.MakeHostConfig(password: "sekrit-pw"));
        refusing.Reconcile();
        var item = refusing.Hosts[^1];

        await item.TestConnectionCommand.ExecuteAsync(null);

        Assert.NotNull(item.TestResultText);
        Assert.DoesNotContain("sekrit-pw", item.TestResultText);
        Assert.Contains("refused", item.TestResultText);
    }

    [Fact]
    public void Auth_failed_host_exposes_error_state_and_actionable_text()
    {
        var item = AddHost(name: "office-pc");
        _store.Hosts[0].Status = new ConnectionStatus(
            item.HostId, HostConnectionState.AuthFailed, 1, null, "unauthorized", null);
        item.RefreshFromEntry();

        Assert.True(item.HasAuthError);
        Assert.Equal("The host refused this password. Check the gui_rpc_auth.cfg on office-pc.", item.AuthErrorText);
    }

    [Fact]
    public void Remove_deletes_from_registry()
    {
        var item = AddHost();
        _settings.Remove(item.HostId);
        Assert.Empty(_registry.Hosts);
        Assert.Empty(_settings.Hosts);
    }

    [Fact]
    public void Polling_interval_round_trips_through_the_registry()
    {
        Assert.Equal(5, _settings.PollingIntervalSeconds);
        _settings.PollingIntervalSeconds = 30;
        Assert.Equal(30, _registry.PollingIntervalSeconds);
        Assert.Equal([2, 5, 10, 30, 60], SettingsViewModel.AllowedPollingIntervals);
    }
}
