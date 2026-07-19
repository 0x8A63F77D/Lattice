using CommunityToolkit.Mvvm.ComponentModel;
using Lattice.App.Infrastructure;
using Lattice.App.Localization;
using Lattice.Boinc.GuiRpc;
using Lattice.Core;

namespace Lattice.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly HostRegistry _registry;
    private readonly Func<IGuiRpcClient> _clientFactory;
    private readonly ThemePreference _theme;
    private readonly UiStateStore _uiState;

    public SettingsViewModel(HostRegistry registry, Func<IGuiRpcClient> clientFactory, ThemePreference theme, UiStateStore uiState)
    {
        _registry = registry;
        _clientFactory = clientFactory;
        _theme = theme;
        _uiState = uiState;
    }

    /// <summary>Exposed for the Add-host dialog, which registers into the same registry/factory.</summary>
    public HostRegistry Registry => _registry;
    public Func<IGuiRpcClient> ClientFactory => _clientFactory;

    public static IReadOnlyList<int> AllowedPollingIntervals => LatticeConfig.AllowedPollingIntervals;

    public static IReadOnlyList<AppTheme> AllThemes { get; } = [AppTheme.Light, AppTheme.Dark, AppTheme.System];

    /// <summary>Mirrors the shared <see cref="ThemePreference"/> for XAML binding; setting
    /// it routes through the single owner so app-wide theme state and persistence stay
    /// consistent (same shape as DensityPreference).</summary>
    public AppTheme SelectedTheme
    {
        get => _theme.Value;
        set { _theme.Set(value); OnPropertyChanged(); }
    }

    /// <summary>Inline error under the polling expander when persisting the interval fails.</summary>
    [ObservableProperty] private string? _pollingError;

    public int PollingIntervalSeconds
    {
        get => _registry.PollingIntervalSeconds;
        set
        {
            if (value != _registry.PollingIntervalSeconds)
            {
                PollingError = RegistryGuard.TryMutate(() => _registry.SetPollingInterval(value)) is { } error
                    ? string.Format(Strings.SettingsIntervalSaveFailedFmt, error)
                    : null;
                // On failure the registry kept its old value; re-raising snaps the
                // ComboBox back so the UI never shows an interval that isn't live.
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Close-to-tray toggle (issue #92). Displayed state is the INVERSE of the
    /// resolved <see cref="UiState.ExitOnClose"/>: ON = "closing keeps Lattice in the tray".
    /// Read resolves null (pre-#92 / never-chosen) through the platform default
    /// (<see cref="TrayResidencyDefaults"/>); set stores a concrete bool so the choice is
    /// pinned thereafter. Same store-then-reraise shape as the theme toggle — reload-fresh
    /// via <see cref="UiStateStore.Update"/> honours the read-modify-write doctrine.</summary>
    public bool CloseToTray
    {
        get => !TrayResidencyDefaults.Resolve(_uiState.Load().ExitOnClose, TrayResidencyDefaults.Current);
        set
        {
            _uiState.Update(s => s with { ExitOnClose = !value });
            OnPropertyChanged();
        }
    }

    /// <summary>Full-speed polling while hidden (issue #92). OFF floors hidden-window
    /// polling at 30 s (<see cref="Lattice.Core.PollingCadencePolicy"/>). Persisted in the
    /// Core registry beside the interval; mirrors <see cref="PollingIntervalSeconds"/>'s
    /// RegistryGuard.TryMutate + re-raise-on-failure shape (a failed save snaps the toggle
    /// back to the live value).</summary>
    public bool FullSpeedHiddenPolling
    {
        get => _registry.FullSpeedHiddenPolling;
        set
        {
            if (value != _registry.FullSpeedHiddenPolling)
            {
                PollingError = RegistryGuard.TryMutate(() => _registry.SetFullSpeedHiddenPolling(value)) is { } error
                    ? string.Format(Strings.SettingsIntervalSaveFailedFmt, error)
                    : null;
                OnPropertyChanged();
            }
        }
    }
}
