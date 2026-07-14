using Lattice.App.Infrastructure;
using Lattice.App.Tests.Fakes;
using Lattice.App.ViewModels;
using Lattice.Core;
using Lattice.Tests;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Lattice.App.Tests;

public class SettingsViewModelTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}.json");
    private readonly string _uiPath = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}-ui.json");
    private HostRegistry _registry = null!;
    private HostMonitorManager _manager = null!;
    private SettingsViewModel _settings = null!;

    public ValueTask InitializeAsync()
    {
        _registry = new HostRegistry(new LatticeConfig(5, []), _path);
        _manager = new HostMonitorManager(_registry, () => new FakeGuiRpcClient(), new FakeTimeProvider());
        _settings = new SettingsViewModel(_registry, () => new FakeGuiRpcClient(), new ThemePreference(new UiStateStore(_uiPath)));
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _manager.DisposeAsync();
        File.Delete(_path);
        File.Delete(_uiPath);
    }

    [Fact]
    public void Polling_interval_round_trips_through_the_registry()
    {
        Assert.Equal(5, _settings.PollingIntervalSeconds);
        _settings.PollingIntervalSeconds = 30;
        Assert.Equal(30, _registry.PollingIntervalSeconds);
        Assert.Equal([2, 5, 10, 30, 60], SettingsViewModel.AllowedPollingIntervals);
    }

    /// <summary>Turns the registry's config path into a directory: Save's rename onto it throws.</summary>
    private void MakeConfigPathUnwritable()
    {
        File.Delete(_path);
        Directory.CreateDirectory(_path);
    }

    private void RestoreConfigPath() => Directory.Delete(_path);

    [Fact]
    public void Polling_interval_persistence_failure_sets_error_and_keeps_old_value()
    {
        MakeConfigPathUnwritable();
        try
        {
            _settings.PollingIntervalSeconds = 30;

            Assert.NotNull(_settings.PollingError);
            Assert.Equal(5, _settings.PollingIntervalSeconds);
            Assert.Equal(5, _registry.PollingIntervalSeconds);
        }
        finally
        {
            RestoreConfigPath();
        }
    }
}
