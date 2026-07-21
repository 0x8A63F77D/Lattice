using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using Lattice.App.ViewModels;
using Lattice.App.Views;
using Lattice.Tests;
using Xunit;

namespace Lattice.App.Tests.Headless;

// Issue #107: the owner saw a thin gap between the data grid's column header and
// the 1-px rule above it (the command bar's bottom border). Root cause: the three
// snapshot views used to DOCK a partial-results FAInfoBar between the command bar
// and the grid; a CLOSED FAInfoBar collapses its template content to zero height
// but the control itself stays IsVisible=true, so its 8-DIP top margin kept
// reserving layout space in the DockPanel. Event Log (no partial bar) was always
// flush.
//
// The fix is structural (owner call): the bar is an overlay child of the grid's
// Panel wrapper, OUT of the dock flow the grid participates in, so grid geometry
// is independent of the bar in EVERY state — open, closed, or absent. When open
// it floats over the first data rows, below the column header (which must stay
// visible and sortable — the #88 invariant), instead of pushing rows down.
// These tests pin both sides:
//   1. closed bar -> the grid sits EXACTLY flush below the command bar (gap 0);
//   2. open bar   -> the bar is genuinely laid out (real height, floating just
//                    below the header band) and the grid STILL sits flush — the
//                    bar overlays, it does not displace.
public class HeaderFrameGapTests
{
    // Vertical gap between the command bar's bottom edge and the DataGrid's top
    // edge, in the window's coordinate space. The command bar is each view's
    // single top-docked, LatticeCommandBarHeight-sized Border at the window top.
    private static (double Gap, DataGrid Grid, Border Bar) MeasureGap(Window window)
    {
        double WY(Visual v) => v.TranslatePoint(new Point(0, 0), window)!.Value.Y;
        var grid = window.GetVisualDescendants().OfType<DataGrid>().Single();
        var bar = window.GetVisualDescendants().OfType<Border>()
            .First(b => WY(b) == 0 && b.Bounds.Height >= 40 && b.Bounds.Height <= 60);
        return (WY(grid) - (WY(bar) + bar.Bounds.Height), grid, bar);
    }

    private static void AssertFlush(Window window)
    {
        var (gap, _, _) = MeasureGap(window);
        Assert.True(Math.Abs(gap) < 0.5,
            $"the grid must sit flush below the command bar while the partial bar is closed; measured gap {gap}");
    }

    // ---- Closed (or absent) partial bar: all four views are flush -----------

    [AvaloniaFact]
    public void Tasks_grid_sits_flush_below_the_command_bar()
    {
        var fx = new HostGraphFixture();
        var vm = new TasksViewModel(fx.Store, fx.Clock, fx.UiState, fx.Density, fx.Control);
        var window = fx.Host(new TasksView { DataContext = vm });
        fx.AddHost("host-a", new FakeGuiRpcClient());
        window.Show();
        fx.Layout();

        AssertFlush(window);
        fx.Dispose();
    }

    [AvaloniaFact]
    public void Projects_grid_sits_flush_below_the_command_bar()
    {
        var fx = new HostGraphFixture();
        var vm = new ProjectsViewModel(fx.Store, fx.Clock);
        var window = fx.Host(new ProjectsView { DataContext = vm });
        fx.AddHost("host-a", new FakeGuiRpcClient());
        window.Show();
        fx.Layout();

        AssertFlush(window);
        fx.Dispose();
    }

    [AvaloniaFact]
    public void Transfers_grid_sits_flush_below_the_command_bar()
    {
        var fx = new HostGraphFixture();
        var vm = new TransfersViewModel(fx.Store, fx.Clock, fx.Density);
        var window = fx.Host(new TransfersView { DataContext = vm });
        fx.AddHost("host-a", new FakeGuiRpcClient());
        window.Show();
        fx.Layout();

        AssertFlush(window);
        fx.Dispose();
    }

    [AvaloniaFact]
    public void EventLog_grid_sits_flush_below_the_command_bar()
    {
        var fx = new HostGraphFixture();
        var vm = new EventLogViewModel(fx.Store);
        var window = fx.Host(new EventLogView { DataContext = vm });
        fx.AddHost("host-a", new FakeGuiRpcClient());
        window.Show();
        fx.Layout();

        AssertFlush(window);
        fx.Dispose();
    }

    // ---- Open partial bar: overlays the grid, never displaces it ------------

    [AvaloniaFact]
    public async Task Open_partial_bar_overlays_the_grid_without_displacing_it()
    {
        var fx = new HostGraphFixture();
        var vm = new TasksViewModel(fx.Store, fx.Clock, fx.UiState, fx.Density, fx.Control);
        var window = fx.Host(new TasksView { DataContext = vm });
        fx.AddHost("host-up", new FakeGuiRpcClient());
        // An unauthorized host makes coverage partial, which opens the bar.
        fx.AddHost("host-down",
            new FakeGuiRpcClient { OnAuthorize = _ => Task.FromResult(false) });
        window.Show();
        fx.Start();

        await fx.SettleAsync(() => vm.ShowPartialBar);
        fx.Layout();

        // The grid does not move for the bar: flush below the command bar even
        // while the bar is OPEN. (Red on the old docked layout, where the open
        // bar pushed the grid down.)
        AssertFlush(window);

        // And the bar is genuinely showing, floating over the data rows BELOW the
        // column header band — never over the header itself, which must stay
        // visible and clickable/sortable under any overlay (the #88 invariant; a
        // partial outage can persist for hours).
        double WY(Visual v) => v.TranslatePoint(new Point(0, 0), window)!.Value.Y;
        var (_, grid, _) = MeasureGap(window);
        // The view now hosts a second (closed) FAInfoBar — the M3 control-failure
        // bar — so select the partial bar by its placement class.
        var infoBar = window.GetVisualDescendants().OfType<FAInfoBar>()
            .Single(b => b.Classes.Contains("partialBar"));
        Assert.True(infoBar is { IsOpen: true, IsVisible: true } && infoBar.Bounds.Height > 20,
            $"the open partial bar must be laid out with real height; got IsOpen={infoBar.IsOpen} " +
            $"IsVisible={infoBar.IsVisible} height={infoBar.Bounds.Height}");
        var header = grid.GetVisualDescendants()
            .OfType<Avalonia.Controls.DataGridColumnHeader>()
            .First(h => h.IsVisible && h.Bounds.Width > 0 && h.Bounds.Height > 0);
        double headerBottom = WY(header) + header.Bounds.Height;
        Assert.True(WY(infoBar) > headerBottom - 0.5,
            $"the open partial bar must not cover the column header; barTop={WY(infoBar)}, headerBottom={headerBottom}");
        Assert.True(WY(infoBar) < headerBottom + 16,
            $"the open partial bar must float just below the header band; barTop={WY(infoBar)}");

        // Card geometry (issue #119 design pass): the bar is a content-hugging card
        // capped at 720 px, horizontally centred over the grid — never a full-width
        // banner band (a stretched bar reads as part of the grid and maximizes row
        // occlusion; the width cap bounds occlusion to the card's own footprint).
        Assert.True(infoBar.Bounds.Width <= 720 + 0.5,
            $"the open partial bar must cap at 720px, not stretch across the grid; width={infoBar.Bounds.Width}");
        double WX(Visual v) => v.TranslatePoint(new Point(0, 0), window)!.Value.X;
        var panel = (Visual)infoBar.GetVisualParent()!;
        double barCenter = WX(infoBar) + infoBar.Bounds.Width / 2;
        double panelCenter = WX(panel) + panel.Bounds.Width / 2;
        Assert.True(Math.Abs(barCenter - panelCenter) < 1,
            $"the open partial bar must be horizontally centred over the grid; barCenter={barCenter}, panelCenter={panelCenter}");
        // Overlay-surface corner radius (spec radiusXLarge = 8 via LatticeSurfaceRadius),
        // asserted on the control because the template binds it through to the card's
        // ContentRoot border. Equality also proves the DynamicResource resolved — an
        // unresolved token would leave FA's ControlCornerRadius default (4).
        Assert.Equal(new CornerRadius(8), infoBar.CornerRadius);
        // Elevation: LatticeOverlayShadow must be wired onto the template's ContentRoot
        // (an unresolved DynamicResource silently yields an empty BoxShadows). Pixel
        // truth of the shadow is the owner's eyeball; the wiring is machine-checked.
        var contentRoot = infoBar.GetVisualDescendants().OfType<Border>()
            .First(b => b.Name == "ContentRoot");
        Assert.True(contentRoot.BoxShadow.Count > 0,
            "the open partial bar's ContentRoot must carry the LatticeOverlayShadow elevation");

        await fx.DisposeAsync();
    }
}
