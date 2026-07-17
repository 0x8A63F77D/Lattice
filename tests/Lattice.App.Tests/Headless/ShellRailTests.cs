using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
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

/// <summary>Headless pins for the All-hosts sentinel that leads the host rail.</summary>
public class ShellRailTests
{
    private static (ShellWindow Window, ShellViewModel Shell, HostRegistry Registry) MakeShell()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}.json");
        var registry = new HostRegistry(new LatticeConfig(5, []), path);
        // Manager never started: no sockets, no background threads in headless tests.
        var manager = new HostMonitorManager(registry, () => new FakeGuiRpcClient(), new FakeTimeProvider());
        var store = new HostStore(registry, manager, new ImmediateUiDispatcher());
        var uiState = new UiStateStore(Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}-ui.json"));
        var shell = new ShellViewModel(registry, store, new ManualUiClock(), uiState, () => new FakeGuiRpcClient());
        var window = new ShellWindow { DataContext = shell };
        return (window, shell, registry);
    }

    [AvaloniaFact]
    public void First_rail_row_renders_the_all_hosts_label()
    {
        var (window, shell, registry) = MakeShell();
        window.Show();
        // Two hosts + a tall viewport → Flat, so the "All hosts" sentinel leads the rail
        // (a lone host now renders as SingleHost with no sentinel). The shell's own viewport
        // feed (Task 8 wires it from Nav.Bounds) is stood in here by a direct call.
        registry.AddHost(TestData.MakeHostConfig(name: "a"));
        registry.AddHost(TestData.MakeHostConfig(name: "b"));
        shell.SetRailViewportHeight(1000.0);
        Layout(window);

        var sentinelRow = window.HostList.GetVisualDescendants().OfType<ListBoxItem>()
            .Single(li => ReferenceEquals(li.DataContext, shell.RailEntries[0]));
        var label = VisualTree.FindInVisualTree<TextBlock>(sentinelRow, t => t.Text == Strings.AllHosts);

        Assert.NotNull(label);
        window.Close();
    }

    [AvaloniaFact]
    public void Selecting_a_host_then_switching_views_leaves_scope_unchanged()
    {
        var (window, shell, registry) = MakeShell();
        window.Show();
        var host = TestData.MakeHostConfig(name: "a");
        registry.AddHost(host);
        registry.AddHost(TestData.MakeHostConfig(name: "b"));
        shell.SetRailViewportHeight(1000.0);   // Flat rail (see First_rail_row_ note)
        Layout(window);

        // Scope to host "a" with a real click — the scope trigger is the click gesture
        // (OnHostRailTapped), so a bare SelectedIndex assignment no longer scopes.
        var hostRow = shell.RailEntries.OfType<HostRailItemViewModel>().Single(r => r.HostId == host.Id);
        RailInput.ClickRow(window, hostRow);
        Layout(window);
        Assert.Equal(host.Id, shell.Scope.HostId);

        shell.SelectViewCommand.Execute("2");
        Layout(window);

        // Scope is a rail concern; switching the current page must not touch it.
        Assert.Equal(host.Id, shell.Scope.HostId);
        window.Close();
    }

    [AvaloniaFact]
    public void Collapsed_pane_keeps_the_all_hosts_sentinel_icon_only()
    {
        var (window, shell, registry) = MakeShell();
        window.Show();
        // Two hosts + tall viewport → Flat, so the sentinel is present (see First_rail_row_ note).
        registry.AddHost(TestData.MakeHostConfig(name: "office-pc"));
        registry.AddHost(TestData.MakeHostConfig(name: "home-pc"));
        shell.SetRailViewportHeight(1000.0);
        Layout(window);

        var sentinelText = window.GetVisualDescendants().OfType<StackPanel>()
            .Single(p => p.Name == "AllHostsText");
        Assert.True(sentinelText.IsVisible);

        // Design §Responsive: in the 48px compact rail, the sentinel shows the
        // icon only too — name/subtext live in the tooltip, same as host rows.
        window.Nav.IsPaneOpen = false;
        Layout(window);
        Assert.False(sentinelText.IsVisible);

        var sentinelRow = window.HostList.GetVisualDescendants().OfType<ListBoxItem>()
            .Single(li => ReferenceEquals(li.DataContext, shell.RailEntries[0]));
        var origin = sentinelRow.TranslatePoint(new Point(0, 0), window)!.Value;
        Assert.True(origin.X >= 0, $"sentinel row starts at x={origin.X}");
        Assert.True(sentinelRow.Bounds.Width <= window.Nav.CompactPaneLength,
            $"sentinel row width {sentinelRow.Bounds.Width} exceeds the {window.Nav.CompactPaneLength}px compact strip");

        window.Nav.IsPaneOpen = true;
        Layout(window);
        Assert.True(sentinelText.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void Navigating_to_tasks_renders_a_TasksView()
    {
        var (window, shell, registry) = MakeShell();
        window.Show();
        registry.AddHost(TestData.MakeHostConfig());
        Layout(window);

        // Leave Tasks (the default page) and come back, so this actually exercises
        // navigation rather than just the initial state.
        shell.SelectViewCommand.Execute("1");
        Layout(window);
        shell.SelectViewCommand.Execute("0");
        Layout(window);

        Assert.IsType<TasksViewModel>(shell.CurrentPage);
        Assert.NotEmpty(window.GetVisualDescendants().OfType<TasksView>());
        window.Close();
    }

    [AvaloniaFact]
    public void Selecting_tasks_swaps_its_icon_to_filled_and_deselecting_restores_regular()
    {
        var (window, shell, registry) = MakeShell();
        window.Show();
        registry.AddHost(TestData.MakeHostConfig());
        Layout(window);

        Assert.True(Application.Current!.TryGetResource("IconTaskListSquareLtrFilled", null, out var filled));
        Assert.True(Application.Current!.TryGetResource("IconTaskListSquareLtrRegular", null, out var regular));

        // Tasks is the default selected view, so its icon should already be Filled.
        var icon = Assert.IsType<FAPathIconSource>(window.NavTasks.IconSource);
        Assert.Same(filled, icon.Data);

        shell.SelectViewCommand.Execute("1");
        Layout(window);

        Assert.Same(regular, icon.Data);
        window.Close();
    }

    [AvaloniaFact]
    public void Navigating_to_settings_fills_its_icon_and_leaving_restores_regular()
    {
        var (window, shell, registry) = MakeShell();
        window.Show();
        registry.AddHost(TestData.MakeHostConfig());
        Layout(window);

        Assert.True(Application.Current!.TryGetResource("IconSettingsFilled", null, out var settingsFilled));
        Assert.True(Application.Current!.TryGetResource("IconSettingsRegular", null, out var settingsRegular));
        var settingsIcon = Assert.IsType<FAPathIconSource>(window.NavSettings.IconSource);

        // Default page is Tasks → the Settings footer item is outlined at rest.
        Assert.Same(settingsRegular, settingsIcon.Data);

        shell.NavigateToSettings();
        Layout(window);

        // Settings becomes the active destination → its glyph fills.
        Assert.Same(settingsFilled, settingsIcon.Data);
        // Invariant: exactly one filled glyph on the rail — no view item fills while
        // Settings is active (all four view items resolve their *Regular geometry).
        var viewItems = new[] { window.NavTasks, window.NavProjects, window.NavTransfers, window.NavEventLog };
        for (var i = 0; i < viewItems.Length; i++)
        {
            Assert.True(Application.Current!.TryGetResource(shell.Views[i].IconKey, null, out var viewRegular));
            var icon = Assert.IsType<FAPathIconSource>(viewItems[i].IconSource);
            Assert.Same(viewRegular, icon.Data);
        }

        // Leaving Settings for a view restores the outlined Settings glyph.
        shell.SelectViewCommand.Execute("0");
        Layout(window);
        Assert.Same(settingsRegular, settingsIcon.Data);
        window.Close();
    }

    [AvaloniaFact]
    public void Every_nav_icon_key_resolves_against_the_shown_window()
    {
        // Invariant pin for ShellWindow.ResolveIconGeometry's fail-fast contract:
        // the Regular/Filled keys are raw strings in ShellViewModel's Views list
        // with no compile-time check, so every current (and future) key must
        // resolve to a geometry in the merged resources.
        var (window, shell, registry) = MakeShell();
        window.Show();
        registry.AddHost(TestData.MakeHostConfig());
        Layout(window);

        foreach (var view in shell.Views)
            foreach (var key in new[] { view.IconKey, view.IconFilledKey })
            {
                Assert.True(window.TryFindResource(key, out var value),
                    $"nav icon resource '{key}' ({view.Title}) should resolve");
                Assert.IsType<StreamGeometry>(value);
            }
        window.Close();
    }

    [AvaloniaFact]
    public void Hosts_block_is_hosted_in_the_pane_footer_slot()
    {
        var (window, shell, registry) = MakeShell();
        window.Show();
        registry.AddHost(TestData.MakeHostConfig(name: "office-pc"));
        Layout(window);

        // Design 3a: the hosts block lives in the PaneFooter slot, NOT the rejected
        // on-top PaneCustomContent slot. This discriminates the two layouts (order alone
        // does not — PaneCustomContent also sits above the footer menu).
        Assert.Null(window.Nav.PaneCustomContent);
        var footer = Assert.IsAssignableFrom<Control>(window.Nav.PaneFooter);
        Assert.Contains(window.HostList.GetVisualAncestors(), a => ReferenceEquals(a, footer));

        // Secondary sanity: still renders above Settings (FooterMenuItems).
        var hostsTop = window.HostList.TranslatePoint(new Point(0, 0), window)!.Value.Y;
        var settingsTop = window.NavSettings.TranslatePoint(new Point(0, 0), window)!.Value.Y;
        Assert.True(hostsTop < settingsTop,
            $"hosts (y={hostsTop}) must render above Settings (y={settingsTop})");
        window.Close();
    }
}
