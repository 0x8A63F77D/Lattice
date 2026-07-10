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
using Xunit;

namespace Lattice.App.Tests.Headless;

/// <summary>Headless pins for the All-hosts sentinel that leads the host rail.</summary>
public class ShellRailTests
{
    private static (ShellWindow Window, ShellViewModel Shell, HostRegistry Registry) MakeShell()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}.json");
        var registry = new HostRegistry(new LatticeConfig(5, []), path);
        // Manager never started: no sockets, no background threads in headless tests.
        var manager = new HostMonitorManager(registry, () => new FakeGuiRpcClient(), TimeProvider.System);
        var store = new HostStore(registry, manager, new ImmediateUiDispatcher());
        var shell = new ShellViewModel(registry, store, new ManualUiClock(), () => new FakeGuiRpcClient());
        var window = new ShellWindow { DataContext = shell };
        return (window, shell, registry);
    }

    // Mirrors ShellWindowTests.Layout: headless Show() skips a full layout pass, so the
    // rail ListBox inside the NavigationView's PaneCustomContent stays unrealized until
    // measured.
    private static void Layout(Window window)
    {
        window.Measure(new Size(window.Width, window.Height));
        window.Arrange(new Rect(0, 0, window.Width, window.Height));
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void First_rail_row_renders_the_all_hosts_label()
    {
        var (window, shell, registry) = MakeShell();
        window.Show();
        registry.AddHost(TestData.MakeHostConfig());
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
        var host = TestData.MakeHostConfig();
        registry.AddHost(host);
        Layout(window);

        // Index 0 is the All-hosts sentinel; the sole host lives at index 1.
        window.HostList.SelectedIndex = 1;
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
        registry.AddHost(TestData.MakeHostConfig(name: "office-pc"));
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
}
