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
            var shell = new ShellViewModel(registry, store, clock, uiState, factory);

            var shellWindow = new ShellWindow { DataContext = shell };
            desktop.MainWindow = shellWindow;

            // Single-instance activation (#92): a secondary launch pings the primary
            // instead of starting a second app; surface the existing window. Until
            // PR C, "surface" is Show()+Activate(); PR C routes this through the tray
            // controller (which also un-hides from the tray).
            if (ActivationGuard is { } guard)
            {
                guard.SetActivationCallback(() => Dispatcher.UIThread.Post(() =>
                {
                    shellWindow.Show();
                    shellWindow.Activate();
                }));
            }

            desktop.Exit += (_, _) =>
            {
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

