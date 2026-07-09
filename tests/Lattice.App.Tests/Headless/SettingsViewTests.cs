using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lattice.App.Infrastructure;
using Lattice.App.Tests.Fakes;
using Lattice.App.ViewModels;
using Lattice.App.Views;
using Lattice.Core;
using Lattice.Tests;
using Xunit;

namespace Lattice.App.Tests.Headless;

public class SettingsViewTests
{
    // Headless Show() does not run a full layout pass, so the ItemsControl's
    // containers are never materialized and GetVisualDescendants can't see the
    // per-host expanders. A single measure/arrange realizes the tree, matching
    // what a real render loop does at startup (precedent: ShellWindowTests.Layout).
    private static void Layout(Window window)
    {
        window.Measure(new Size(window.Width, window.Height));
        window.Arrange(new Rect(0, 0, window.Width, window.Height));
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Renders_one_expander_per_host_plus_polling_selector()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}.json");
        var registry = new HostRegistry(new LatticeConfig(5, []), path);
        var manager = new HostMonitorManager(registry, () => new FakeGuiRpcClient(), TimeProvider.System);
        var store = new HostStore(registry, manager, new ImmediateUiDispatcher());
        var settings = new SettingsViewModel(registry, store, () => new FakeGuiRpcClient());
        store.Changed += (_, _) => settings.Reconcile();
        registry.AddHost(TestData.MakeHostConfig(name: "a"));
        registry.AddHost(TestData.MakeHostConfig(name: "b"));

        var window = new Window { Width = 900, Height = 700, Content = new SettingsView { DataContext = settings } };
        window.Show();
        Layout(window);

        Assert.Equal(2, window.GetVisualDescendants().OfType<FluentAvalonia.UI.Controls.FASettingsExpander>().Count(
            e => e.Name != "PollingExpander"));
        Assert.Single(window.GetVisualDescendants().OfType<ComboBox>());
        window.Close();
    }
}
