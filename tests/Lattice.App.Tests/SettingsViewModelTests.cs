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
    private UiStateStore _uiStore = null!;

    public ValueTask InitializeAsync()
    {
        _registry = new HostRegistry(new LatticeConfig(5, []), _path);
        _manager = new HostMonitorManager(_registry, () => new FakeGuiRpcClient(), new FakeTimeProvider());
        _uiStore = new UiStateStore(_uiPath);
        _settings = new SettingsViewModel(_registry, () => new FakeGuiRpcClient(), new ThemePreference(_uiStore), _uiStore);
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

    [Fact]
    public void CloseToTray_defaults_to_the_current_platforms_resolved_inverse()
    {
        // No stored ExitOnClose yet ⇒ platform default; displayed toggle is its inverse.
        bool expectedDefault = !TrayResidencyDefaults.ExitOnCloseDefault(TrayResidencyDefaults.Current);
        Assert.Equal(expectedDefault, _settings.CloseToTray);
    }

    [Fact]
    public void CloseToTray_persists_the_inverse_as_a_concrete_bool()
    {
        _settings.CloseToTray = true;   // "keep in tray" ⇒ ExitOnClose = false
        Assert.False(_uiStore.Load().ExitOnClose);
        Assert.True(_settings.CloseToTray);

        _settings.CloseToTray = false;  // "exit on close" ⇒ ExitOnClose = true
        Assert.True(_uiStore.Load().ExitOnClose);
        Assert.False(_settings.CloseToTray);
    }

    [Fact]
    public void FullSpeedHiddenPolling_round_trips_through_the_registry()
    {
        Assert.False(_settings.FullSpeedHiddenPolling);
        _settings.FullSpeedHiddenPolling = true;
        Assert.True(_registry.FullSpeedHiddenPolling);
        Assert.True(_settings.FullSpeedHiddenPolling);
    }

    [Fact]
    public void FullSpeedHiddenPolling_persistence_failure_sets_error_and_keeps_old_value()
    {
        MakeConfigPathUnwritable();
        try
        {
            _settings.FullSpeedHiddenPolling = true;

            Assert.NotNull(_settings.PollingError);
            Assert.False(_settings.FullSpeedHiddenPolling);
            Assert.False(_registry.FullSpeedHiddenPolling);
        }
        finally
        {
            RestoreConfigPath();
        }
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

    [Fact]
    public void App_version_is_a_non_empty_string_for_the_about_surface()
    {
        // The About section quotes this verbatim in bug reports, so it must always
        // resolve to something a tester can read back — never blank.
        Assert.False(string.IsNullOrWhiteSpace(SettingsViewModel.AppVersion));
    }

    [Theory]
    [InlineData("0.2.0-alpha.1", "0.2.0-alpha.1")]           // clean SemVer passes through
    [InlineData("0.2.0-alpha.1+abc1234", "0.2.0-alpha.1")]   // build metadata (+commit) is trimmed
    [InlineData("1.0.0+deadbeef", "1.0.0")]
    [InlineData("  1.2.3  ", "1.2.3")]                        // whitespace is trimmed
    [InlineData(null, "unknown")]
    [InlineData("", "unknown")]
    [InlineData("   ", "unknown")]
    public void FormatVersion_strips_build_metadata_and_falls_back_when_absent(string? raw, string expected)
    {
        Assert.Equal(expected, SettingsViewModel.FormatVersion(raw));
    }
}
