using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using Lattice.App.Infrastructure;
using Lattice.App.Localization;
using Lattice.App.Tests.Fakes;
using Lattice.App.ViewModels;
using Lattice.App.Views;
using Lattice.Core;
using Lattice.Tests;
using Microsoft.Extensions.Time.Testing;
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
        var manager = new HostMonitorManager(registry, () => new FakeGuiRpcClient(), new FakeTimeProvider());
        var store = new HostStore(registry, manager, new ImmediateUiDispatcher());
        var uiPath = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}-ui.json");
        var settings = new SettingsViewModel(registry, store, () => new FakeGuiRpcClient(), new ThemePreference(new UiStateStore(uiPath)));
        // Hosts are added to prove they do NOT render as expanders in this view
        // any more — host management lives entirely in the rail (design 3b).
        registry.AddHost(TestData.MakeHostConfig(name: "a"));
        registry.AddHost(TestData.MakeHostConfig(name: "b"));

        var window = new Window { Width = 900, Height = 700, Content = new SettingsView { DataContext = settings } };
        return (window, settings, registry);
    }

    [AvaloniaFact]
    public void Renders_pointer_caption_and_no_host_expanders()
    {
        var (window, _, _) = MakeView();
        window.Show();
        Layout(window);

        // Exclude BOTH global-group expanders (Polling now, Theme after Task 14) so
        // this stays green across the sequence; the assertion is "no host-bound
        // expander remains" — every remaining expander is a named global one.
        Assert.Empty(window.GetVisualDescendants().OfType<FASettingsExpander>()
            .Where(e => e.Name is not ("PollingExpander" or "ThemeExpander")));
        var caption = window.GetVisualDescendants().OfType<TextBlock>()
            .SingleOrDefault(t => t.Text == Strings.SettingsHostsPointer);
        Assert.NotNull(caption);
        window.Close();
    }
}
