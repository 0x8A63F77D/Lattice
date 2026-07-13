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

    // `store` is accepted for signature parity with call sites (ShellViewModel
    // wires the same three dependencies as the Add-host flow); the Hosts group
    // that consumed it was removed in this task.
    public SettingsViewModel(HostRegistry registry, HostStore store, Func<IGuiRpcClient> clientFactory, ThemePreference theme)
    {
        _registry = registry;
        _clientFactory = clientFactory;
        _theme = theme;
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
}
