using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.Input;
using Lattice.App.Localization;
using Lattice.Core;

namespace Lattice.App.Infrastructure;

/// <summary>
/// The thin shell for tray residency (issue #92): the ONLY place that touches the
/// TrayIcon, window visibility, and app shutdown. All decisions live in pure policy
/// (<see cref="Lattice.App.ViewModels.WindowClosePolicy"/>, <see cref="PollingCadencePolicy"/>);
/// this type only funnels them into framework calls.
///
/// Constructed in <c>App.OnFrameworkInitializationCompleted</c> INSIDE the desktop-lifetime
/// guard — the TrayIcon is code-constructed here, never declared in App.axaml, so a headless
/// test run (which loads the real App) never instantiates a platform tray on a backend that
/// has none (plan Part 1).
/// </summary>
public sealed class TrayResidencyController : IDisposable
{
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly Window _mainWindow;
    private readonly HostMonitorManager _manager;
    private readonly UiStateStore _uiState;
    private readonly TrayIcon _trayIcon;

    public TrayResidencyController(
        Application app,
        IClassicDesktopStyleApplicationLifetime desktop,
        Window mainWindow,
        HostMonitorManager manager,
        UiStateStore uiState)
    {
        _desktop = desktop;
        _mainWindow = mainWindow;
        _manager = manager;
        _uiState = uiState;

        // The app's lifetime is now owned by explicit exits (tray Exit, exit-on-close
        // verdict, or platform shutdown per F7) — never implicitly by the last window
        // closing, which is exactly the event close-to-tray cancels.
        _desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _trayIcon = new TrayIcon
        {
            // Assembly name is `Lattice` (Lattice.App.csproj <AssemblyName>), matching every
            // existing avares://Lattice/... URI — NOT the project name Lattice.App.
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Lattice/Assets/lattice.ico"))),
            ToolTipText = Strings.TrayToolTip,
            // Windows left-click fires Command; macOS click shows the menu (F3).
            Command = new RelayCommand(ShowWindow),
            Menu = new NativeMenu
            {
                Items =
                {
                    // Menu is "Show window" + "Exit", not the issue's literal "Show/Hide":
                    // a dynamic-header toggle relies on undocumented runtime NativeMenu
                    // mutation, and Hide is already served by the window close button
                    // (plan Part 5; owner may veto on hardware).
                    new NativeMenuItem { Header = Strings.TrayShowWindow, Command = new RelayCommand(ShowWindow) },
                    new NativeMenuItemSeparator(),
                    new NativeMenuItem { Header = Strings.TrayExit, Command = new RelayCommand(ExitApplication) },
                },
            },
        };

        TrayIcon.SetIcons(app, new TrayIcons { _trayIcon });
    }

    /// <summary>Set by <see cref="ExitApplication"/> / <see cref="NotifyExitingViaClose"/>; read
    /// by the close-policy shell so the shutdown-driven second <c>Closing</c> allows the close
    /// instead of re-hiding to the tray.</summary>
    public bool ExitRequested { get; private set; }

    /// <summary>The resolved close-to-tray preference, read LIVE from the store at decide time
    /// (never cached — honours the store's read-modify-write doctrine) and mapped through the
    /// platform default. The close policy receives this concrete bool.</summary>
    public bool ExitOnClose =>
        TrayResidencyDefaults.Resolve(_uiState.Load().ExitOnClose, TrayResidencyDefaults.Current);

    /// <summary>Restore the window with fresh data. The refresh burst starts its RPC
    /// round-trips BEFORE the first frame renders (Q1); showing is never gated on RPC
    /// completion — fresh snapshots land via the normal event path within ~one round-trip.
    /// Idempotent when already visible.</summary>
    public void ShowWindow()
    {
        _manager.SetWindowVisible(true);
        _manager.RequestRefreshAll();
        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    /// <summary>Hide the window and relax polling to the hidden-state floor.</summary>
    public void HideToTray()
    {
        _mainWindow.Hide();
        _manager.SetWindowVisible(false);
    }

    /// <summary>Tray "Exit" funnel: flag the intent, then request shutdown. The shutdown
    /// re-closes the window with reason <c>ApplicationShutdown</c>, which the close policy
    /// maps to <c>AllowClose</c> — no loop.</summary>
    public void ExitApplication()
    {
        ExitRequested = true;
        _desktop.Shutdown();
    }

    /// <summary>Belt-and-braces ordering for the exit-on-close verdict: sets
    /// <see cref="ExitRequested"/> before shutdown so the shutdown-driven second <c>Closing</c>
    /// short-circuits identically on platforms that report <c>Undefined</c> as the reason.</summary>
    public void NotifyExitingViaClose() => ExitRequested = true;

    public void Dispose() => _trayIcon.Dispose();
}
