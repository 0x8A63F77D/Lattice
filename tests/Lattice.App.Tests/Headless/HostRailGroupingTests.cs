using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Lattice.App.Aggregation;
using Lattice.App.Infrastructure;
using Lattice.App.Tests.Fakes;
using Lattice.App.ViewModels;
using Lattice.App.Views;
using Lattice.Core;
using Lattice.Tests;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using static Lattice.Tests.HeadlessLayout;

namespace Lattice.App.Tests.Headless;

public class HostRailGroupingTests
{
    private static (ShellWindow Window, ShellViewModel Shell, HostRegistry Registry) MakeShell(double height)
        => MakeShell(new UiStateStore(Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}-ui.json")), height);

    // Overload taking an explicit UiStateStore so a test can inspect what a compact session did (or
    // did NOT) persist by re-loading the same file after the interaction.
    private static (ShellWindow Window, ShellViewModel Shell, HostRegistry Registry) MakeShell(UiStateStore uiState, double height)
    {
        var path = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}.json");
        var registry = new HostRegistry(new LatticeConfig(5, []), path);
        var manager = new HostMonitorManager(registry, () => new FakeGuiRpcClient(), new FakeTimeProvider());
        var store = new HostStore(registry, manager, new ImmediateUiDispatcher());
        var shell = new ShellViewModel(registry, store, new ManualUiClock(), uiState, () => new FakeGuiRpcClient());
        var window = new ShellWindow { DataContext = shell, Height = height, Width = 1280 };
        return (window, shell, registry);
    }

    [AvaloniaFact]
    public void Overflowing_rail_shows_group_headers_and_the_toggle()
    {
        var (window, shell, registry) = MakeShell(height: 700);
        window.Show();
        for (var i = 0; i < 12; i++) registry.AddHost(TestData.MakeHostConfig(name: $"h{i}"));
        Layout(window);

        // 12 hosts + All-hosts * 40 = 520 > footer budget on a 700-high window => grouped.
        Assert.Contains(shell.RailEntries, e => e is GroupHeaderRailItemViewModel);
        var toggle = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "RailGroupToggle");
        Assert.True(toggle.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void Selecting_a_group_header_does_not_change_scope()
    {
        var (window, shell, registry) = MakeShell(height: 700);
        window.Show();
        for (var i = 0; i < 12; i++) registry.AddHost(TestData.MakeHostConfig(name: $"h{i}"));
        Layout(window);
        Assert.True(shell.Scope.IsAllHosts);

        var header = shell.RailEntries.OfType<GroupHeaderRailItemViewModel>().First();
        RailInput.ClickRow(window, header);
        Layout(window);

        // Header click toggles its group / is rejected — scope stays All hosts and the
        // header is not left selected.
        Assert.True(shell.Scope.IsAllHosts);
        Assert.IsNotType<GroupHeaderRailItemViewModel>(window.HostList.SelectedItem);
        window.Close();
    }

    [AvaloniaFact]
    public void Clicking_a_group_header_keeps_the_scoped_host_end_to_end()
    {
        // Round-5 P2, real path: scope a host, then click a header through the actual
        // ListBox SelectedItem binding (not a direct ToggleCommand). Scope must survive.
        var (window, shell, registry) = MakeShell(height: 700);
        window.Show();
        for (var i = 0; i < 12; i++) registry.AddHost(TestData.MakeHostConfig(name: $"h{i}"));
        Layout(window);

        // Grouped (overflow). Expand Healthy so a host row is visible + selectable.
        shell.RailEntries.OfType<GroupHeaderRailItemViewModel>()
            .Single(g => g.Tier.Equals(RailTier.Healthy))
            .ToggleCommand.Execute(null);            // RebuildRail replaces the header instances
        Layout(window);
        var hostRow = shell.RailEntries.OfType<HostRailItemViewModel>().First();
        RailInput.ClickRow(window, hostRow);         // scope a host via a real click
        Layout(window);
        Assert.Equal(hostRow.HostId, shell.Scope.HostId);

        // Re-query the header — the expand's RebuildRail cleared RailEntries and made new
        // GroupHeaderRailItemViewModel instances, so click the current instance (the old
        // reference has no realized container).
        var healthy = shell.RailEntries.OfType<GroupHeaderRailItemViewModel>()
            .Single(g => g.Tier.Equals(RailTier.Healthy));
        RailInput.ClickRow(window, healthy);         // collapses Healthy → host row hidden
        Layout(window);

        // Scope persists as data even though the row is now hidden and unselected.
        Assert.Equal(hostRow.HostId, shell.Scope.HostId);
        Assert.False(shell.Scope.IsAllHosts);
        window.Close();
    }

    [AvaloniaFact]
    public void Grouped_rows_use_the_spec_heights_28_header_36_host()
    {
        var (window, shell, registry) = MakeShell(height: 700);
        window.Show();
        for (var i = 0; i < 12; i++) registry.AddHost(TestData.MakeHostConfig(name: $"h{i}"));
        Layout(window);   // grouped (overflow); all hosts are Healthy tier

        // Expand Healthy so host rows are realized alongside the header.
        shell.RailEntries.OfType<GroupHeaderRailItemViewModel>()
            .Single(g => g.Tier.Equals(RailTier.Healthy)).ToggleCommand.Execute(null);
        Layout(window);

        var headerRow = window.HostList.GetVisualDescendants().OfType<ListBoxItem>()
            .First(li => li.DataContext is GroupHeaderRailItemViewModel);
        var hostRow = window.HostList.GetVisualDescendants().OfType<ListBoxItem>()
            .First(li => li.DataContext is HostRailItemViewModel);

        // Design 3a: 28 px collapsed count/header rows, 36 px grouped host rows — NOT the 40 px flat height.
        Assert.Equal(28.0, headerRow.Bounds.Height, precision: 0);
        Assert.Equal(36.0, hostRow.Bounds.Height, precision: 0);
        window.Close();
    }

    [AvaloniaFact]
    public void Compact_pane_collapses_group_header_rows_to_no_blank_row()
    {
        var (window, shell, registry) = MakeShell(height: 700);
        window.Show();
        for (var i = 0; i < 12; i++) registry.AddHost(TestData.MakeHostConfig(name: $"h{i}"));
        Layout(window);
        Assert.Contains(shell.RailEntries, e => e is GroupHeaderRailItemViewModel);

        window.Nav.IsPaneOpen = false;   // 48px compact
        Layout(window);

        // Decisions §5: compact shows individual host icons; a group-header row has no visible
        // content in compact, so its container collapses — no blank 28px row.
        var headerRow = window.HostList.GetVisualDescendants().OfType<ListBoxItem>()
            .First(li => li.DataContext is GroupHeaderRailItemViewModel);
        Assert.True(headerRow.Bounds.Height < 1.0,
            $"compact group-header row should collapse, was {headerRow.Bounds.Height}px");
        window.Close();
    }

    [AvaloniaFact]
    public void Compact_pane_renders_every_host_icon_even_when_healthy_is_default_collapsed()
    {
        // Decisions §5: when the pane is compact, the rail renders EVERY host's individual state-icon.
        // Without the force-expand, the default-collapsed Healthy tier would emit only its (text-hidden,
        // untappable) header in compact — leaving healthy hosts with no visible, reachable icon.
        var (window, shell, registry) = MakeShell(height: 700);
        window.Show();
        for (var i = 0; i < 12; i++) registry.AddHost(TestData.MakeHostConfig(name: $"h{i}"));
        Layout(window);

        // Expanded pane, Grouped (overflow), Healthy default-collapsed: only the header, no host rows.
        Assert.Contains(shell.RailEntries, e => e is GroupHeaderRailItemViewModel);
        Assert.DoesNotContain(shell.RailEntries, e => e is HostRailItemViewModel);

        window.Nav.IsPaneOpen = false;   // 48px compact
        Layout(window);

        // Every host now has a rail row (the force-expand emits all Healthy hosts)...
        Assert.Equal(12, shell.RailEntries.OfType<HostRailItemViewModel>().Count());
        // ...and a realized host container renders as a visible icon row, not collapsed to 0px.
        var hostContainer = window.HostList.GetVisualDescendants().OfType<ListBoxItem>()
            .First(li => li.DataContext is HostRailItemViewModel);
        Assert.True(hostContainer.Bounds.Height > 1.0,
            $"compact host row should render an icon, was {hostContainer.Bounds.Height}px");
        window.Close();
    }

    [AvaloniaFact]
    public void Compact_force_expand_is_transient_and_never_flips_the_persisted_collapse()
    {
        // Guards the two halves of the §5 fix: (a) with the pane OPEN + grouped-default, Healthy IS
        // collapsed (host rows hidden) — so the fix didn't just always-expand; (b) a compact session
        // leaves the persisted RailHealthyExpanded untouched, and re-opening restores the saved state.
        var uiState = new UiStateStore(Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}-ui.json"));
        var (window, shell, registry) = MakeShell(uiState, height: 700);
        window.Show();
        for (var i = 0; i < 12; i++) registry.AddHost(TestData.MakeHostConfig(name: $"h{i}"));
        Layout(window);

        // (a) Pane open, grouped-default: Healthy collapsed, preference false.
        Assert.False(uiState.Load().RailHealthyExpanded);
        Assert.DoesNotContain(shell.RailEntries, e => e is HostRailItemViewModel);

        window.Nav.IsPaneOpen = false;   // compact force-expands host icons for the compute only
        Layout(window);
        Assert.Contains(shell.RailEntries, e => e is HostRailItemViewModel);
        Assert.False(uiState.Load().RailHealthyExpanded);   // (b) transient override, never persisted

        window.Nav.IsPaneOpen = true;    // re-open restores the saved collapsed state
        Layout(window);
        Assert.DoesNotContain(shell.RailEntries, e => e is HostRailItemViewModel);
        Assert.False(uiState.Load().RailHealthyExpanded);
        window.Close();
    }
}
