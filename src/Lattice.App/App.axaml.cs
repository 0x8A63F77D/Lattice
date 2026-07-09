using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Lattice.App.Infrastructure;
using Lattice.App.ViewModels;
using Lattice.App.Views;
using Lattice.Boinc.GuiRpc;
using Lattice.Core;

namespace Lattice.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Real composition happens only under the desktop lifetime; headless tests
        // build their own object graph and must not touch config or sockets.
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            HostRegistry registry = LoadRegistryWithFallback(LatticeConfig.DefaultPath);
            Func<IGuiRpcClient> factory = () => new BoincGuiRpcClient();
            var manager = new HostMonitorManager(registry, factory, TimeProvider.System);
            var store = new HostStore(registry, manager, AvaloniaUiDispatcher.Instance);
            var clock = new DispatcherUiClock();
            var shell = new ShellViewModel(registry, store, clock, factory);

            desktop.MainWindow = new ShellWindow { DataContext = shell };
            desktop.Exit += (_, _) =>
            {
                clock.Dispose();
                shell.Dispose();
                store.Dispose();
                // Process teardown: bounded blocking wait is the pragmatic exception
                // to the no-sync-over-async rule (DisposeAsync cancels the loops and
                // returns promptly; 5 s is a generous ceiling).
                manager.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
            };
            manager.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>A corrupt config must not brick a monitoring app: keep the bad file, start fresh.</summary>
    private static HostRegistry LoadRegistryWithFallback(string path)
    {
        try
        {
            return HostRegistry.Load(path);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            try { File.Move(path, $"{path}.corrupt-{DateTimeOffset.Now:yyyyMMdd-HHmmss}"); }
            catch { /* keep going with defaults; nothing useful to do */ }
            return new HostRegistry(new LatticeConfig(5, []), path);
        }
    }
}

