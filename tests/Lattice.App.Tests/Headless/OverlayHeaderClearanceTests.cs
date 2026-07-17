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
    //   2. hit-testing at the header reaches the header, NOT the overlay, and
    //   3. the overlay's bounds start at or below the header band (exclude it).
    private static void AssertOverlayClearsHeader(Window window)
    {
        var grid = window.GetVisualDescendants().OfType<DataGrid>().Single();

        // The overlay is the visible Border sibling of the DataGrid inside the
        // shared Panel wrapper (<Panel><DataGrid/><Border loading/><Border empty/></Panel>).
        // Exactly one overlay is up at a time (loading XOR empty), so Single is the
        // assertion that the state was actually reached.
        var wrapper = grid.GetVisualParent()!;
        var overlay = wrapper.GetVisualChildren().OfType<Border>()
            .Single(b => b.IsVisible && b.Bounds.Height > 0);

        // A real, laid-out column header (skip the zero-width hidden Host column etc.).
        var header = grid.GetVisualDescendants().OfType<DataGridColumnHeader>()
            .Where(h => h.IsVisible && h.Bounds.Width > 0 && h.Bounds.Height > 0)
            .OrderBy(h => h.Bounds.X)
            .First();

        // 1. Header ink present.
        Assert.True(header.Bounds is { Width: > 0, Height: > 0 },
            "the column header must be rendered with real bounds while the overlay is up");

        // 3. Overlay excludes the header band: its top edge sits at or below the
        //    header's bottom edge (both translated into window space).
        var headerBottom = header.TranslatePoint(new Point(0, header.Bounds.Height), window)!.Value.Y;
        var overlayTop = overlay.TranslatePoint(new Point(0, 0), window)!.Value.Y;
        Assert.True(overlayTop >= headerBottom - 0.5,
            $"overlay must start at/below the header band: overlayTop={overlayTop}, headerBottom={headerBottom}");

        // 2. Hit-testing at the header centre reaches the header and not the overlay.
        var centre = header.TranslatePoint(
            new Point(header.Bounds.Width / 2, header.Bounds.Height / 2), window)!.Value;
        var hits = window.GetVisualsAt(centre).ToList();
        Assert.DoesNotContain(overlay, hits);
        Assert.Contains(header, hits);
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

        await fx.SettleAsync(() => !vm.IsLoading);
        fx.Layout();
        Assert.True(vm.IsEmpty);

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

        await fx.SettleAsync(() => !vm.IsLoading);
        fx.Layout();
        Assert.True(vm.IsEmpty);

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

        await fx.SettleAsync(() => !vm.IsLoading);
        fx.Layout();
        Assert.True(vm.IsEmpty);

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
