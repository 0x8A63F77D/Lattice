using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using Lattice.App.Infrastructure;
using Lattice.App.Tests.Fakes;
using Lattice.App.ViewModels;
using Lattice.App.Views;
using Lattice.Core;
using Lattice.Tests;
using Xunit;
using static Lattice.Tests.HeadlessLayout;

namespace Lattice.App.Tests.Headless;

public class SettingsViewTests
{
    private static (Window Window, SettingsViewModel Settings, HostRegistry Registry) MakeView()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}.json");
        var registry = new HostRegistry(new LatticeConfig(5, []), path);
        // Manager never started: no sockets, no background threads in headless tests.
        var manager = new HostMonitorManager(registry, () => new FakeGuiRpcClient(), TimeProvider.System);
        var store = new HostStore(registry, manager, new ImmediateUiDispatcher());
        var settings = new SettingsViewModel(registry, store, () => new FakeGuiRpcClient());
        store.Changed += (_, _) => settings.Reconcile();
        registry.AddHost(TestData.MakeHostConfig(name: "a"));
        registry.AddHost(TestData.MakeHostConfig(name: "b"));

        var window = new Window { Width = 900, Height = 700, Content = new SettingsView { DataContext = settings } };
        return (window, settings, registry);
    }

    [AvaloniaFact]
    public void Renders_one_expander_per_host_plus_polling_selector()
    {
        var (window, _, _) = MakeView();
        window.Show();
        Layout(window);

        Assert.Equal(2, window.GetVisualDescendants().OfType<FASettingsExpander>().Count(
            e => e.Name != "PollingExpander"));
        Assert.Single(window.GetVisualDescendants().OfType<ComboBox>());
        window.Close();
    }

    [AvaloniaFact]
    public async Task Confirming_remove_removes_the_host_from_the_registry()
    {
        var (window, settings, registry) = MakeView();
        window.Show();
        Layout(window);
        var removed = settings.Hosts[0];

        removed.RemoveCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var dialog = Assert.Single(window.GetVisualDescendants().OfType<FAContentDialog>());
        dialog.Hide(FAContentDialogResult.Primary);
        await HeadlessSync.WaitUntilAsync(() => registry.Hosts.Count == 1);

        Assert.Single(registry.Hosts);
        Assert.DoesNotContain(registry.Hosts, h => h.Id == removed.HostId);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Cancelling_remove_leaves_the_registry_untouched()
    {
        var (window, settings, registry) = MakeView();
        window.Show();
        Layout(window);

        settings.Hosts[0].RemoveCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var dialog = Assert.Single(window.GetVisualDescendants().OfType<FAContentDialog>());
        dialog.Hide(FAContentDialogResult.None);
        await HeadlessSync.WaitUntilAsync(
            () => !window.GetVisualDescendants().OfType<FAContentDialog>().Any());

        Assert.Equal(2, registry.Hosts.Count);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Double_clicking_remove_confirms_once_and_removes_once()
    {
        var (window, settings, registry) = MakeView();
        window.Show();
        Layout(window);
        var removed = settings.Hosts[0];

        // A fast double-click fires the command twice before any dialog resolves.
        removed.RemoveCommand.Execute(null);
        removed.RemoveCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        // Single-flight: the second request must not stack a second dialog
        // (two stacked dialogs both resolving used to call RemoveHost twice —
        // the second call throws ArgumentException unhandled on the dispatcher).
        var dialog = Assert.Single(window.GetVisualDescendants().OfType<FAContentDialog>());
        dialog.Hide(FAContentDialogResult.Primary);
        await HeadlessSync.WaitUntilAsync(
            () => registry.Hosts.Count == 1
                && !window.GetVisualDescendants().OfType<FAContentDialog>().Any());

        Assert.Single(registry.Hosts);
        Assert.DoesNotContain(registry.Hosts, h => h.Id == removed.HostId);
        Assert.Empty(window.GetVisualDescendants().OfType<FAContentDialog>());
        window.Close();
    }

    [AvaloniaFact]
    public void Navigating_away_unsubscribes_the_discarded_view()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}.json");
        var registry = new HostRegistry(new LatticeConfig(5, []), path);
        var manager = new HostMonitorManager(registry, () => new FakeGuiRpcClient(), TimeProvider.System);
        var store = new HostStore(registry, manager, new ImmediateUiDispatcher());
        var settings = new SettingsViewModel(registry, store, () => new FakeGuiRpcClient());

        // Replicates the shell: pages live in a ContentControl via DataTemplates;
        // navigating swaps Content, discarding the old view without rebinding its
        // DataContext. The Settings VM outlives every visit, so a discarded view
        // that stays subscribed to RemoveRequested is a per-visit view-tree leak.
        var host = new ContentControl
        {
            DataTemplates =
            {
                new Avalonia.Controls.Templates.FuncDataTemplate<SettingsViewModel>(
                    (_, _) => new SettingsView()),
            },
        };
        var window = new Window { Width = 900, Height = 700, Content = host };
        window.Show();
        host.Content = settings;
        Layout(window);

        var view = Assert.Single(window.GetVisualDescendants().OfType<SettingsView>());
        Assert.Same(settings, view.SubscribedVmForTests);
        Assert.True(settings.HasRemoveSubscribersForTests);

        host.Content = "elsewhere"; // navigate away
        Layout(window);

        // VM-side check: the handler itself must be gone, not just the view's
        // bookkeeping field — a leaked handler retains the whole view tree.
        Assert.False(settings.HasRemoveSubscribersForTests);
        window.Close();
    }
}
