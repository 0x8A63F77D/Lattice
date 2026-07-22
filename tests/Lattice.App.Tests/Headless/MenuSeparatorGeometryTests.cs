using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Styling;
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

/// <summary>
/// Issue #155: our context-menu dividers used the <c>MenuItem Header="-"</c>
/// pseudo-separator. That pseudo-item is a real MenuItem, so the window-level
/// <c>MenuFlyoutPresenter MenuItem</c> MinHeight=32 rule (design 3b) forced the
/// divider into a full 32px item slot with a 1px line floating in the middle —
/// the owner saw a large, off-rhythm gap around the line on both the host-rail
/// menu and the grid row menus.
///
/// The fix uses the first-class MenuFlyout separator, a raw <c>&lt;Separator/&gt;</c>
/// (empirically re-verified to render on FA 3.0.1 / Avalonia 12.1, contra the stale
/// PR #135 "no-ops" note). A Separator is not a MenuItem, so it escapes the
/// MinHeight rule and collapses to FA's own thin metric (MenuFlyoutSeparatorHeight
/// = 1 + MenuFlyoutSeparatorThemePadding = -4,1,-4,1 ⇒ ~3px of layout).
///
/// These probes measure the RENDERED divider footprint between the two items that
/// flank each menu's separator. They are RED on the pre-fix pseudo-item (≈32px gap)
/// and GREEN after (≈3px), and additionally pin the line as vertically centred in
/// the gap (kills the "extra space on one side" class).
/// </summary>
public class MenuSeparatorGeometryTests
{
    // A real FA menu separator consumes only its 1px line plus 1px top/bottom
    // margin. A normal menu item is 32px (design 3b). The divider must read as a
    // thin rule, not an empty item slot: cap well below half an item's height.
    private const double MaxDividerGap = 10.0;

    private static void AssertThinCentredDivider(
        MenuFlyoutPresenter presenter, object aboveHeader, object belowHeader)
    {
        double Top(Visual v) => v.TranslatePoint(new Point(0, 0), presenter)!.Value.Y;

        var items = presenter.GetVisualDescendants().OfType<MenuItem>().ToList();
        var above = items.First(m => Equals(m.Header, aboveHeader));
        var below = items.First(m => Equals(m.Header, belowHeader));

        double aboveBottom = Top(above) + above.Bounds.Height;
        double belowTop = Top(below);
        double gap = belowTop - aboveBottom;

        Assert.True(gap > 0 && gap < MaxDividerGap,
            $"divider between '{aboveHeader}' and '{belowHeader}' must be a thin rule, " +
            $"not a full item slot; measured gap {gap:F1}px (cap {MaxDividerGap})");

        // The rendered line sits centred in that gap: the space above the line
        // equals the space below (symmetry). The Separator lives between the two
        // items — pick the one whose centre falls inside the gap.
        var sep = presenter.GetVisualDescendants().OfType<Separator>()
            .Select(s => (Sep: s, CentreY: Top(s) + s.Bounds.Height / 2))
            .Single(x => x.CentreY > aboveBottom - 0.5 && x.CentreY < belowTop + 0.5);
        double above2 = sep.CentreY - aboveBottom;
        double below2 = belowTop - sep.CentreY;
        Assert.True(System.Math.Abs(above2 - below2) < 1.5,
            $"divider line must be centred in its gap; above={above2:F1}px, below={below2:F1}px");
    }

    // Reproduce ShellWindow's window-level rule (design 3b: 32px menu rows) on the
    // grid test host. In the app the grid views live inside ShellWindow, so their
    // flyout menu items inherit this MinHeight; the bare grid fixture does not carry
    // it, so without this the pseudo-separator would collapse on its own and the
    // probe would false-green. This is the exact style from ShellWindow.axaml.
    private static void ApplyMenuRowMinHeight(Window window)
    {
        var style = new Style(x => x.OfType<MenuFlyoutPresenter>().Descendant().OfType<MenuItem>());
        style.Setters.Add(new Setter(Layoutable.MinHeightProperty, 32.0));
        window.Styles.Add(style);
    }

    private static MenuFlyoutPresenter OpenFlyout(Window window, MenuFlyout flyout, Control anchor)
    {
        flyout.ShowAt(anchor);
        Dispatcher.UIThread.RunJobs();
        return window.GetVisualDescendants().OfType<MenuFlyoutPresenter>().First();
    }

    // ---- Host rail menu (ShellWindow) ---------------------------------------

    [AvaloniaFact]
    public void Host_rail_menu_divider_is_a_thin_rule()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}.json");
        var registry = new HostRegistry(new LatticeConfig(5, []), path);
        var manager = new HostMonitorManager(registry, () => new FakeGuiRpcClient(), new FakeTimeProvider());
        var store = new HostStore(registry, manager, new ImmediateUiDispatcher());
        var uiState = new UiStateStore(Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}-ui.json"));
        var shell = new ShellViewModel(registry, store, new ManualUiClock(), uiState, () => new FakeGuiRpcClient());
        var window = new ShellWindow { DataContext = shell, Width = 1280, Height = 800 };
        window.Show();
        registry.AddHost(TestData.MakeHostConfig(name: "mini-01"));
        Layout(window);

        var hostRow = window.HostList.GetVisualDescendants().OfType<ListBoxItem>()
            .Single(li => li.DataContext is HostRailItemViewModel);
        var root = hostRow.GetVisualDescendants().OfType<DockPanel>().First();
        var flyout = Assert.IsType<MenuFlyout>(root.ContextFlyout);
        var presenter = OpenFlyout(window, flyout, root);

        // Divider after "Test connection", before the "Run modes" submenu.
        AssertThinCentredDivider(presenter, Strings.HostMenuTest, Strings.HostMenuRunModes);
        // Divider before "Remove host…" (Snooze is the item above it; Resume is
        // collapsed unless snoozed).
        AssertThinCentredDivider(presenter, Strings.HostMenuSnooze, Strings.HostMenuRemove);

        flyout.Hide();
        window.Close();
    }

    // ---- Grid row menus (TasksView / ProjectsView) --------------------------

    [AvaloniaFact]
    public void Tasks_row_menu_divider_is_a_thin_rule()
    {
        var fx = new HostGraphFixture();
        var vm = new TasksViewModel(fx.Store, fx.Clock, fx.UiState, fx.Density, fx.Control);
        var view = new TasksView { DataContext = vm };
        var window = fx.Host(view);
        ApplyMenuRowMinHeight(window);
        fx.AddHost("host-a", new FakeGuiRpcClient());
        window.Show();
        fx.Layout();

        var flyout = Assert.IsType<MenuFlyout>(view.Grid.ContextFlyout);
        var presenter = OpenFlyout(window, flyout, view.Grid);

        AssertThinCentredDivider(presenter, Strings.Resume, Strings.Abort);

        flyout.Hide();
        fx.Dispose();
    }

    [AvaloniaFact]
    public void Projects_row_menu_divider_is_a_thin_rule()
    {
        var fx = new HostGraphFixture();
        var vm = new ProjectsViewModel(fx.Store, fx.Clock, fx.Control);
        var view = new ProjectsView { DataContext = vm };
        var window = fx.Host(view);
        ApplyMenuRowMinHeight(window);
        fx.AddHost("host-a", new FakeGuiRpcClient());
        window.Show();
        fx.Layout();

        var flyout = Assert.IsType<MenuFlyout>(view.Grid.ContextFlyout);
        var presenter = OpenFlyout(window, flyout, view.Grid);

        AssertThinCentredDivider(presenter, Strings.Resume, Strings.Detach);

        flyout.Hide();
        fx.Dispose();
    }
}
