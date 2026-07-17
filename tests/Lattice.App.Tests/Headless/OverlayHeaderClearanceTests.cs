using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Lattice.App.ViewModels;
using Lattice.App.Views;
using Lattice.Tests;
using Xunit;

namespace Lattice.App.Tests.Headless;

// Issue #88: the empty-state / loading overlay (a Border) used to cover the
// ENTIRE grid, column header included, in all four data views. The fix
// constrains the overlay to the data-rows region so the header stays visible
// AND hit-testable whenever an overlay is up (empty and loading alike).
//
// One shared geometry+hit-test gate is exercised across every view/state pair,
// mirroring the single shared fix mechanism (the "dataOverlay" style class): a
// per-view transcription of the assertion would manufacture the same-class
// finding the fix exists to retire. Composition root / dispatcher discipline /
// settle rules all come from HostGraphFixture — see its class doc.
public class OverlayHeaderClearanceTests
{
    // Machine gate (issue #88 verification), asserted against the REAL view tree
    // with an overlay up:
    //   1. the column header band is rendered (a real header has ink), and
    //   2. the overlay does not cover the header coordinates (so a click there
    //      lands on the still-hit-testable header, not the overlay), and
    //   3. the overlay's bounds start at or below the header band (exclude it).
    //
    // Purely geometric on realized Bounds — no dependence on a render/hit-test
    // frame (GetVisualsAt is render-state-sensitive and flaked under the full
    // suite). The end-to-end real-input proof that a header click reaches the
    // header through the overlay lives in
    // ProjectsViewTests.Header_click_reaches_the_header_through_the_loading_overlay.
    private static void AssertOverlayClearsHeader(Window window)
    {
        var grid = window.GetVisualDescendants().OfType<DataGrid>().Single();

        // The overlay is the visible Border sibling of the DataGrid inside the
        // shared Panel wrapper (<Panel><DataGrid/><Border loading/><Border empty/></Panel>).
        // Exactly one overlay is up at a time (loading XOR empty), so Single is the
        // assertion that the state was actually reached. The wrapper is the common
        // coordinate space for the overlay and (translated) header geometry below.
        var wrapper = (Visual)grid.GetVisualParent()!;
        var overlay = wrapper.GetVisualChildren().OfType<Border>()
            .Single(b => b.IsVisible && b.Bounds.Height > 0);

        // A real, laid-out column header (skip the zero-width hidden Host column etc.).
        var header = grid.GetVisualDescendants().OfType<DataGridColumnHeader>()
            .Where(h => h.IsVisible && h.Bounds.Width > 0 && h.Bounds.Height > 0)
            .OrderBy(h => h.Bounds.X)
            .First();

        // 1. Header ink present and hit-testable.
        Assert.True(header is { IsHitTestVisible: true, Bounds.Width: > 0, Bounds.Height: > 0 },
            "the column header must be rendered and hit-testable while the overlay is up");

        // The header centre and bottom, expressed in the wrapper's coordinate
        // space — where overlay.Bounds and grid.Bounds also live (both are direct
        // children of the wrapper Panel, so their Bounds share that space).
        var headerCentre = header.TranslatePoint(
            new Point(header.Bounds.Width / 2, header.Bounds.Height / 2), wrapper)!.Value;
        var headerBottom = header.TranslatePoint(new Point(0, header.Bounds.Height), wrapper)!.Value.Y;

        // 2. The overlay does not sit over the header coordinates.
        Assert.False(overlay.Bounds.Contains(headerCentre),
            $"overlay {overlay.Bounds} must not cover the header centre {headerCentre}");

        // 3. Seam is EXACT — the overlay top coincides with the header bottom: no
        //    gap (a sliver of empty grid body would show / a double separator line)
        //    and no overlap (the overlay would clip the header's bottom rule).
        Assert.True(Math.Abs(overlay.Bounds.Y - headerBottom) < 0.5,
            $"overlay top must coincide with the header bottom: overlayTop={overlay.Bounds.Y}, headerBottom={headerBottom}");

        // 4. The overlay still fully covers the data-rows region — its left, right
        //    and bottom edges are flush with the grid, so no empty grid body peeks
        //    out on any side below the header.
        var g = grid.Bounds;
        Assert.True(Math.Abs(overlay.Bounds.X - g.X) < 0.5,
            $"overlay left must be flush with the grid: overlayX={overlay.Bounds.X}, gridX={g.X}");
        Assert.True(Math.Abs(overlay.Bounds.Right - g.Right) < 0.5,
            $"overlay right must be flush with the grid: overlayRight={overlay.Bounds.Right}, gridRight={g.Right}");
        Assert.True(Math.Abs(overlay.Bounds.Bottom - g.Bottom) < 0.5,
            $"overlay bottom must be flush with the grid: overlayBottom={overlay.Bounds.Bottom}, gridBottom={g.Bottom}");
    }

    // ---- Empty overlay: all four views ----------------------------------

    [AvaloniaFact]
    public async Task Tasks_empty_overlay_leaves_the_header_visible_and_hittable()
    {
        var fx = new HostGraphFixture();
        var vm = new TasksViewModel(fx.Store, fx.Clock, fx.UiState, fx.Density);
        var window = fx.Host(new TasksView { DataContext = vm });
        fx.AddHost("host-a", new FakeGuiRpcClient());
        fx.AddHost("host-b", new FakeGuiRpcClient());
        window.Show();
        fx.Start();

        // Settle on the ACTUAL end state (the empty overlay up, zero rows), not on
        // !IsLoading — the fixture bans settling on a boolean that can flip before
        // the expected state (matches the EventLog case below).
        await fx.SettleAsync(() => vm.IsEmpty && vm.Rows.Count == 0);
        fx.Layout();

        AssertOverlayClearsHeader(window);
        await fx.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task Transfers_empty_overlay_leaves_the_header_visible_and_hittable()
    {
        var fx = new HostGraphFixture();
        var vm = new TransfersViewModel(fx.Store, fx.Clock, fx.Density);
        var window = fx.Host(new TransfersView { DataContext = vm });
        fx.AddHost("host-a", new FakeGuiRpcClient());
        fx.AddHost("host-b", new FakeGuiRpcClient());
        window.Show();
        fx.Start();

        // Settle on the ACTUAL end state (the empty overlay up, zero rows), not on
        // !IsLoading — the fixture bans settling on a boolean that can flip before
        // the expected state (matches the EventLog case below).
        await fx.SettleAsync(() => vm.IsEmpty && vm.Rows.Count == 0);
        fx.Layout();

        AssertOverlayClearsHeader(window);
        await fx.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task Projects_empty_overlay_leaves_the_header_visible_and_hittable()
    {
        var fx = new HostGraphFixture();
        var vm = new ProjectsViewModel(fx.Store, fx.Clock);
        var window = fx.Host(new ProjectsView { DataContext = vm });
        fx.AddHost("host-a", new FakeGuiRpcClient());
        fx.AddHost("host-b", new FakeGuiRpcClient());
        window.Show();
        fx.Start();

        // Settle on the ACTUAL end state (the empty overlay up, zero rows), not on
        // !IsLoading — the fixture bans settling on a boolean that can flip before
        // the expected state (matches the EventLog case below).
        await fx.SettleAsync(() => vm.IsEmpty && vm.Rows.Count == 0);
        fx.Layout();

        AssertOverlayClearsHeader(window);
        await fx.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task EventLog_empty_overlay_leaves_the_header_visible_and_hittable()
    {
        var fx = new HostGraphFixture();
        var vm = new EventLogViewModel(fx.Store);
        var window = fx.Host(new EventLogView { DataContext = vm });
        // Two hosts so IsAllHostsScope earns the Host column (matches the sibling
        // header tests); with zero messages the empty overlay is up.
        fx.AddHost("host-a", new FakeGuiRpcClient());
        fx.AddHost("host-b", new FakeGuiRpcClient());
        window.Show();
        fx.Start();

        await fx.SettleAsync(() => vm.IsEmpty && vm.Rows.Count == 0);
        fx.Layout();

        AssertOverlayClearsHeader(window);
        await fx.DisposeAsync();
    }

    // ---- Loading overlay: the three snapshot views ----------------------
    // (EventLog has no loading overlay — it renders its empty state directly.)
    // The manager is deliberately NOT started, so no host has a snapshot and the
    // loading overlay is up (the Loading_overlay_text_names_the_host idiom).

    [AvaloniaFact]
    public void Tasks_loading_overlay_leaves_the_header_visible_and_hittable()
    {
        var fx = new HostGraphFixture();
        var vm = new TasksViewModel(fx.Store, fx.Clock, fx.UiState, fx.Density);
        var window = fx.Host(new TasksView { DataContext = vm });
        fx.AddHost("host-a", new FakeGuiRpcClient());
        window.Show();
        fx.Layout();
        Assert.True(vm.IsLoading);

        AssertOverlayClearsHeader(window);
        fx.Dispose();
    }

    [AvaloniaFact]
    public void Transfers_loading_overlay_leaves_the_header_visible_and_hittable()
    {
        var fx = new HostGraphFixture();
        var vm = new TransfersViewModel(fx.Store, fx.Clock, fx.Density);
        var window = fx.Host(new TransfersView { DataContext = vm });
        fx.AddHost("host-a", new FakeGuiRpcClient());
        window.Show();
        fx.Layout();
        Assert.True(vm.IsLoading);

        AssertOverlayClearsHeader(window);
        fx.Dispose();
    }

    [AvaloniaFact]
    public void Projects_loading_overlay_leaves_the_header_visible_and_hittable()
    {
        var fx = new HostGraphFixture();
        var vm = new ProjectsViewModel(fx.Store, fx.Clock);
        var window = fx.Host(new ProjectsView { DataContext = vm });
        fx.AddHost("host-a", new FakeGuiRpcClient());
        window.Show();
        fx.Layout();
        Assert.True(vm.IsLoading);

        AssertOverlayClearsHeader(window);
        fx.Dispose();
    }
}
