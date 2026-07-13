using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
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

public class ShellWindowTests
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
    public void First_run_shows_cta_and_hides_navigation()
    {
        var (window, shell, _) = MakeShell();
        window.Show();
        Layout(window);
        Assert.False(shell.HasHosts);
        Assert.True(window.FirstRun.IsVisible);
        Assert.False(window.Nav.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void Adding_a_host_swaps_first_run_for_the_shell()
    {
        var (window, shell, registry) = MakeShell();
        window.Show();
        registry.AddHost(TestData.MakeHostConfig());
        Layout(window);
        Assert.True(shell.HasHosts);
        Assert.False(window.FirstRun.IsVisible);
        Assert.True(window.Nav.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void Selecting_a_view_changes_the_current_page()
    {
        var (window, shell, registry) = MakeShell();
        window.Show();
        registry.AddHost(TestData.MakeHostConfig());
        Layout(window);

        shell.SelectViewCommand.Execute("2");

        Assert.Same(shell.Transfers, shell.CurrentPage);
        window.Close();
    }

    [AvaloniaFact]
    public void Tasks_is_highlighted_on_first_render()
    {
        var (window, shell, registry) = MakeShell();
        window.Show();
        registry.AddHost(TestData.MakeHostConfig());
        Layout(window);

        Assert.Same(window.NavTasks, window.Nav.SelectedItem);
        Assert.IsType<TasksViewModel>(shell.CurrentPage);
        window.Close();
    }

    [AvaloniaFact]
    public void Programmatic_view_selection_moves_the_nav_highlight()
    {
        var (window, shell, registry) = MakeShell();
        window.Show();
        registry.AddHost(TestData.MakeHostConfig());
        Layout(window);

        shell.SelectViewCommand.Execute("2");

        Assert.Same(window.NavTransfers, window.Nav.SelectedItem);
        // Reentrancy check: the Nav.SelectedItem assignment re-enters
        // OnNavSelectionChanged -> SelectViewCommand; the VM equality guard must
        // stop the loop with the page still on Transfers.
        Assert.Same(shell.Transfers, shell.CurrentPage);
        window.Close();
    }

    [AvaloniaFact]
    public void Navigating_to_settings_highlights_the_footer_item()
    {
        var (window, shell, registry) = MakeShell();
        window.Show();
        registry.AddHost(TestData.MakeHostConfig());
        Layout(window);

        shell.NavigateToSettings();

        Assert.Same(window.NavSettings, window.Nav.SelectedItem);
        // Reentrancy check: highlighting the footer item re-enters
        // OnNavSelectionChanged -> NavigateToSettings; the equality guards must
        // leave the page settled on Settings.
        Assert.Same(shell.Settings, shell.CurrentPage);
        window.Close();
    }

    [AvaloniaFact]
    public void Host_rail_renders_one_item_per_host_plus_sentinel()
    {
        var (window, shell, registry) = MakeShell();
        window.Show();
        registry.AddHost(TestData.MakeHostConfig(name: "a"));
        registry.AddHost(TestData.MakeHostConfig(name: "b"));
        Layout(window);
        // +1 for the All-hosts sentinel that always leads the rail.
        Assert.Equal(3, window.HostList.ItemCount);
        window.Close();
    }

    [AvaloniaFact]
    public void Collapsed_pane_shows_host_state_icon_only()
    {
        var (window, _, registry) = MakeShell();
        window.Show();
        registry.AddHost(TestData.MakeHostConfig(name: "office-pc"));
        Layout(window);

        var hostText = window.GetVisualDescendants().OfType<StackPanel>()
            .Single(p => p.Name == "HostText");
        var header = window.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Name == "HostsHeader");
        Assert.True(hostText.IsVisible);
        Assert.True(header.IsVisible);

        // Design §Responsive: in the 48px compact rail, hosts show the state icon
        // only — name/subtext/countdown live in the tooltip.
        window.Nav.IsPaneOpen = false;
        Layout(window);
        Assert.False(hostText.IsVisible);
        Assert.False(header.IsVisible);

        // The theme's ListBoxItem MinWidth (88) used to overflow the 48px strip;
        // layout then centers the oversized item at a negative offset and the
        // state icon renders half-clipped at the pane edge. Two rows now exist
        // (the All-hosts sentinel plus this one host) — target the host row.
        var item = window.HostList.GetVisualDescendants().OfType<ListBoxItem>()
            .Single(li => li.DataContext is HostRailItemViewModel);
        var origin = item.TranslatePoint(new Point(0, 0), window)!.Value;
        Assert.True(origin.X >= 0, $"rail item starts at x={origin.X}");
        Assert.True(item.Bounds.Width <= window.Nav.CompactPaneLength,
            $"rail item width {item.Bounds.Width} exceeds the {window.Nav.CompactPaneLength}px compact strip");

        window.Nav.IsPaneOpen = true;
        Layout(window);
        Assert.True(hostText.IsVisible);
        Assert.True(header.IsVisible);
        window.Close();
    }

    // ---- Finding A: horizontal-scroll fidelity (Option 1 — MinWidth on every column) --------

    private static DataGrid PageGrid(ShellWindow window, ShellViewModel shell, string tag)
    {
        shell.SelectViewCommand.Execute(tag);
        Layout(window);
        return window.GetVisualDescendants().OfType<DataGrid>().First();
    }

    // Option 1's rule, per column: a FIXED column pins MinWidth == its spec Width (so it can never
    // shrink below spec — no crush); a STAR column carries a readable floor above the DataGrid's
    // 20px default MinColumnWidth. Without these, a star grid fits-to-width and crushes the fixed
    // columns to ~20px slivers when tight (owner Finding A: "左右的滚动是不可用的"). Mutation-
    // sensitive: dropping any column's MinWidth reverts it to the 20px default and reddens this —
    // a fixed column no longer equals its Width, a star column no longer clears 20.
    [AvaloniaTheory]
    [InlineData("0")] // Tasks
    [InlineData("1")] // Projects
    [InlineData("2")] // Transfers
    [InlineData("3")] // EventLog
    public void Every_data_grid_column_pins_its_min_width_to_spec(string tag)
    {
        var (window, shell, registry) = MakeShell();
        window.Show();
        registry.AddHost(TestData.MakeHostConfig());
        Layout(window);

        var grid = PageGrid(window, shell, tag);
        Assert.All(grid.Columns, c =>
        {
            if (c.Width.IsStar)
                Assert.True(c.MinWidth > 20,
                    $"star column '{c.Header}' needs a readable MinWidth floor above the 20px default (Finding A)");
            else
                Assert.True(c.MinWidth == c.Width.Value,
                    $"fixed column '{c.Header}' must pin MinWidth ({c.MinWidth}) to its spec Width ({c.Width.Value}) (Finding A)");
        });
        window.Close();
    }

    // At the design-default 1280×800 window there is comfortable slack, so no page overflows into a
    // horizontal scrollbar — the star column absorbs the remainder. This guards the width budget:
    // if a future MinWidth bump pushes a view's total past the ~1008px content area, it reddens.
    // (The min-window 1000×700 case is an owner visual gate: headless does not reproduce the
    // FANavigationView pane collapse, so its content width there is not faithful.)
    [AvaloniaTheory]
    [InlineData("0")] // Tasks
    [InlineData("1")] // Projects
    [InlineData("2")] // Transfers
    [InlineData("3")] // EventLog
    public void No_page_overflows_horizontally_at_the_design_default_window(string tag)
    {
        var (window, shell, registry) = MakeShell();
        window.Width = 1280;
        window.Height = 800;
        window.Show();
        registry.AddHost(TestData.MakeHostConfig());
        Layout(window);

        var grid = PageGrid(window, shell, tag);
        var hbar = grid.GetVisualDescendants().OfType<ScrollBar>()
            .FirstOrDefault(b => b.Name == "PART_HorizontalScrollbar");
        Assert.NotNull(hbar);
        Assert.False(hbar!.IsVisible,
            $"page {tag} should not overflow at 1280px (viewport={grid.Bounds.Width:F0})");
        window.Close();
    }
}
