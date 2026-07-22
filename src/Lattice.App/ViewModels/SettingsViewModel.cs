using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    private readonly LanguagePreference _language;
    private readonly UiStateStore _uiState;
    private readonly Action? _restart;

    /// <param name="restart">Composition-root callback that relaunches the app (App.axaml.cs owns
    /// the process/single-instance-guard dance). Null off the desktop path (headless tests), which
    /// disables <see cref="RestartNowCommand"/>.</param>
    public SettingsViewModel(HostRegistry registry, Func<IGuiRpcClient> clientFactory, ThemePreference theme, LanguagePreference language, UiStateStore uiState, Action? restart = null)
    {
        _registry = registry;
        _clientFactory = clientFactory;
        _theme = theme;
        _language = language;
        _uiState = uiState;
        _restart = restart;
    }

    /// <summary>Exposed for the Add-host dialog, which registers into the same registry/factory.</summary>
    public HostRegistry Registry => _registry;
    public Func<IGuiRpcClient> ClientFactory => _clientFactory;

    public static IReadOnlyList<int> AllowedPollingIntervals => LatticeConfig.AllowedPollingIntervals;

    /// <summary>The running build's version, shown in the About section so a tester can quote
    /// exactly what they are running in a bug report. Sourced from the assembly's informational
    /// version — the release scripts stamp it via <c>-p:Version</c> (<c>LATTICE_VERSION</c>);
    /// an un-stamped local <c>dotnet run</c> shows the SDK default.</summary>
    public static string AppVersion { get; } = FormatVersion(
        typeof(SettingsViewModel).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>Trim the build-metadata suffix (<c>+&lt;commit&gt;</c>) that .NET appends to the
    /// informational version by default, leaving the plain SemVer a tester can read back.</summary>
    internal static string FormatVersion(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
            return "unknown";
        var plus = informationalVersion.IndexOf('+');
        return (plus >= 0 ? informationalVersion[..plus] : informationalVersion).Trim();
    }

    public static IReadOnlyList<AppTheme> AllThemes { get; } = [AppTheme.Light, AppTheme.Dark, AppTheme.System];

    /// <summary>Mirrors the shared <see cref="ThemePreference"/> for XAML binding; setting
    /// it routes through the single owner so app-wide theme state and persistence stay
    /// consistent (same shape as DensityPreference).</summary>
    public AppTheme SelectedTheme
    {
        get => _theme.Value;
        set { _theme.Set(value); OnPropertyChanged(); }
    }

    public static IReadOnlyList<AppLanguage> AllLanguages { get; } =
        [AppLanguage.System, AppLanguage.English, AppLanguage.Chinese];

    /// <summary>Shown once the language selection changes: x:Static resource lookups are
    /// read at load, so a new language only takes effect on restart (#147). Latches true on
    /// the first real change and stays visible — a persisted choice the user hasn't acted on.</summary>
    [ObservableProperty] private bool _showLanguageRestartHint;

    /// <summary>True when the composition root wired a restart callback (the desktop app);
    /// gates <see cref="RestartNowCommand"/> so the button no-ops off the desktop path.</summary>
    public bool CanRestart => _restart is not null;

    /// <summary>Relaunches the app to apply the new language. The actual process relaunch +
    /// single-instance-guard handoff lives at the composition root (App.axaml.cs); this only
    /// invokes it. Disabled when no restart callback is wired (headless).</summary>
    [RelayCommand(CanExecute = nameof(CanRestart))]
    private void RestartNow() => _restart?.Invoke();

    /// <summary>Mirrors the shared <see cref="LanguagePreference"/> for XAML binding; setting
    /// it routes through the single owner (persist only — language applies on next launch) and
    /// surfaces the restart hint. Same shape as <see cref="SelectedTheme"/>.</summary>
    public AppLanguage SelectedLanguage
    {
        get => _language.Value;
        set
        {
            if (value == _language.Value) return;
            _language.Set(value);
            ShowLanguageRestartHint = true;
            OnPropertyChanged();
        }
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
