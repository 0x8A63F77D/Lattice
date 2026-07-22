using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Lattice.App.Infrastructure;
using Lattice.App.ViewModels;
using Lattice.App.Views;
using Lattice.Boinc.GuiRpc;
using Lattice.Core;

namespace Lattice.App;

public partial class App : Application
{
    /// <summary>
    /// The single-instance guard, set by <see cref="Program"/> when this process is
    /// the primary (null otherwise — headless tests never run Program.Main, and a
    /// guard-Unavailable launch also leaves it null). Wired to a show-window
    /// activation callback once the main window exists, and disposed on Exit.
    /// PR C swaps the callback body to TrayResidencyController.ShowWindow.
    /// </summary>
    internal static SingleInstanceGuard? ActivationGuard { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Real composition happens only under the desktop lifetime; headless tests
        // build their own object graph and must not touch config or sockets.
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
#if DEBUG
            // Issue #95 isolation probe: LATTICE_COMBO_PROBE=bare replaces the shell
            // with a minimal window holding one plain ComboBox — no registry, no
            // monitors, no command bar — to separate FA popup cost from app context.
            if (ComboOpenProbe.Mode == "bare")
            {
                desktop.MainWindow = ComboOpenProbe.CreateBareWindow();
                base.OnFrameworkInitializationCompleted();
                return;
            }
#endif
            HostRegistry registry = LoadRegistryWithFallback(LatticeConfig.DefaultPath);
            Func<IGuiRpcClient> factory = () => new BoincGuiRpcClient();
#if DEBUG
            // DEBUG-only walkthrough aid (PR F): with LATTICE_SAMPLE_HOSTS set,
            // inject a canned multi-host fleet through the same registry/monitor
            // path as a real host, without touching the on-disk config. Off by
            // default and absent from Release entirely (see SampleHost).
            if (SampleHost.Enabled)
                (registry, factory) = SampleHost.Compose(registry, factory, TimeProvider.System.GetUtcNow());
#endif
            var manager = new HostMonitorManager(registry, factory, TimeProvider.System);
            var store = new HostStore(registry, manager, AvaloniaUiDispatcher.Instance);
            var clock = new DispatcherUiClock();
            var uiState = new UiStateStore();
            var shell = new ShellViewModel(registry, store, clock, uiState, factory, () => RestartApp(desktop));
            // Apply the persisted theme once, here at the composition root on the UI thread
            // (#101). ThemePreference construction is pure by design — it never touches the
            // UI-thread-affine Application.Current.RequestedThemeVariant — so this explicit
            // call is what themes the app on startup, before the first window renders.
            shell.ApplyInitialTheme();

            var shellWindow = new ShellWindow { DataContext = shell };
            // Provide the UI-state store to the data views for column-width
            // persistence (#120). The views come from DataTemplates (no ctor
            // seam), so the store rides an inherited attached property set once
            // on the window and resolved on every descendant grid at attach.
            ColumnWidthScope.SetStore(shellWindow, uiState);
            desktop.MainWindow = shellWindow;

            // Tray residency (#92): the controller owns the TrayIcon, hide/show/exit
            // funnels, and switches the app to OnExplicitShutdown. Code-constructed here
            // inside the desktop guard (never in App.axaml) so headless test runs never
            // instantiate a platform tray. The ShellWindow routes its close attempts
            // through the controller's policy shell.
            var tray = new TrayResidencyController(this, desktop, shellWindow, manager, uiState);
            shellWindow.AttachTray(tray);

            // Single-instance activation (#92): a secondary launch pings the primary
            // instead of starting a second app; surface the existing window through the
            // tray controller (which also un-hides from the tray and fires the refresh burst).
            if (ActivationGuard is { } guard)
                guard.SetActivationCallback(() => Dispatcher.UIThread.Post(tray.ShowWindow));

            // macOS Dock-icon click while all windows are hidden (F10): reopen. Null-guarded
            // via TryGetFeature — the feature is absent on Windows/Linux and headless backends.
            // Activated is a UI-thread lifetime event, so ShowWindow can be called directly.
            if (this.TryGetFeature<IActivatableLifetime>() is { } activatable)
                activatable.Activated += (_, e) =>
                {
                    if (e.Kind == ActivationKind.Reopen)
                        tray.ShowWindow();
                };

            desktop.Exit += (_, _) =>
            {
                tray.Dispose(); // remove the TrayIcon first, before the monitor teardown chain
                ActivationGuard?.Dispose(); // stop accepting activations, release the lock
                clock.Dispose();
                shell.Dispose();
                store.Dispose();
                // UiStateStore needs no teardown: preference saves are write-through
                // at change time and the store holds no open resources.
                // Process teardown: bounded blocking wait is the pragmatic exception
                // to the no-sync-over-async rule (DisposeAsync cancels the loops and
                // returns promptly; 5 s is a generous ceiling).
                manager.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
            };
            manager.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Relaunches the app to apply a new UI language (#147). The culture is read once at startup
    /// (x:Static resource lookups), so a running instance cannot switch live. The single-instance
    /// guard makes a naive relaunch fail: a child that starts while we still hold the lock would
    /// ping us (we'd surface the window) and exit. So release the guard FIRST — freeing the lock so
    /// the child acquires it and becomes primary — then spawn the replacement, then shut down. The
    /// child reads the new language from the already-persisted ui-state.json.
    /// </summary>
    private static void RestartApp(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (PlanRelaunch(Environment.GetEnvironmentVariable("APPIMAGE"), Environment.ProcessPath) is not { } plan)
            return; // unknown host path (never on a normal apphost launch) — no-op

        ActivationGuard?.Dispose();
        ActivationGuard = null; // the desktop.Exit handler also disposes it; null avoids double-dispose

        try
        {
            var psi = new ProcessStartInfo(plan.Exe) { UseShellExecute = false };
            if (plan.ExtractAndRun)
                // Force extract-and-run for the relaunched AppImage: it then self-extracts to a temp
                // dir and runs independent of BOTH FUSE availability (users may have launched us with
                // --appimage-extract-and-run on FUSE-less systems — Codex P2) and this instance's own
                // mount (which unmounts on exit — Codex P2). Works on FUSE and non-FUSE hosts alike.
                psi.Environment["APPIMAGE_EXTRACT_AND_RUN"] = "1";
            Process.Start(psi);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Couldn't relaunch — leave this instance running (now guard-less) rather than shutting
            // down into nothing; the user can retry or relaunch manually.
            return;
        }

        desktop.Shutdown();
    }

    /// <summary>How a language-restart should relaunch the app: the executable to start, and whether
    /// to force AppImage extract-and-run on it.</summary>
    internal readonly record struct RelaunchPlan(string Exe, bool ExtractAndRun);

    /// <summary>Pure relaunch decision (#147). Prefers <c>$APPIMAGE</c> — the AppImage runtime's path
    /// to the outer <c>.AppImage</c> — over <see cref="Environment.ProcessPath"/>, which inside a
    /// running AppImage is the in-mount <c>/tmp/.mount_*/usr/bin/Lattice</c> apphost. When relaunching
    /// the <c>.AppImage</c> the child MUST extract-and-run (see <see cref="RestartApp"/>) so it starts
    /// on FUSE-less hosts and outlives this instance's mount. Non-AppImage launches (Windows exe,
    /// macOS/Linux-tarball apphost) fall back to the process path with no extract-and-run. Returns
    /// <c>null</c> when neither path is known (caller no-ops). Internal + pure for a table test.</summary>
    internal static RelaunchPlan? PlanRelaunch(string? appImagePath, string? processPath) =>
        appImagePath is { } ai ? new RelaunchPlan(ai, ExtractAndRun: true)
        : processPath is { } pp ? new RelaunchPlan(pp, ExtractAndRun: false)
        : null;

    /// <summary>
    /// A broken config must not brick a monitoring app: quarantine the file and
    /// start fresh. But an unreadable file is not necessarily a corrupt one — if
    /// the quarantine move also fails (say, the same permission problem that broke
    /// the read), the file may still hold a valid host list, so the fresh registry
    /// binds to a sibling recovery path: no later Save may overwrite hosts that
    /// still exist on disk. Internal for tests.
    /// </summary>
    internal static HostRegistry LoadRegistryWithFallback(string path)
    {
        try
        {
            return HostRegistry.Load(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
            try
            {
                File.Move(path, $"{path}.corrupt-{stamp}");
            }
            catch
            {
                return new HostRegistry(LatticeConfig.Default, $"{path}.recovery-{stamp}");
            }
            return new HostRegistry(LatticeConfig.Default, path);
        }
    }
}

