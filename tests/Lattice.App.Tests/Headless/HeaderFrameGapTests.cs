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
// snapshot views dock a partial-results FAInfoBar between the command bar and the
// grid; a CLOSED FAInfoBar collapses its template content to zero height but the
// control itself stays IsVisible=true, so its 8-DIP top margin kept reserving
// layout space in the DockPanel. Event Log (no partial bar) was always flush.
//
// The fix is the shared "partialBar" style class (App.axaml): it owns the bar's
// margin and collapses IsVisible while IsOpen=false, so a closed bar contributes
// zero layout space. These tests pin both sides of that contract:
//   1. closed bar  -> the grid sits EXACTLY flush below the command bar (gap 0);
//   2. open bar    -> the bar is genuinely laid out (it has height) and the grid
//                     sits EXACTLY flush below the bar, i.e. collapsing the
//                     closed bar did not also collapse the open one.
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
        var vm = new TasksViewModel(fx.Store, fx.Clock, fx.UiState, fx.Density);
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

    // ---- Open partial bar: still laid out, grid flush below IT --------------

    [AvaloniaFact]
    public async Task Open_partial_bar_occupies_space_and_the_grid_sits_flush_below_it()
    {
        var fx = new HostGraphFixture();
        var vm = new TasksViewModel(fx.Store, fx.Clock, fx.UiState, fx.Density);
        var window = fx.Host(new TasksView { DataContext = vm });
        fx.AddHost("host-up", new FakeGuiRpcClient());
        // An unauthorized host makes coverage partial, which opens the bar.
        fx.AddHost("host-down",
            new FakeGuiRpcClient { OnAuthorize = _ => Task.FromResult(false) });
        window.Show();
        fx.Start();

        await fx.SettleAsync(() => vm.ShowPartialBar);
        fx.Layout();

        double WY(Visual v) => v.TranslatePoint(new Point(0, 0), window)!.Value.Y;
        var (_, grid, bar) = MeasureGap(window);
        var infoBar = window.GetVisualDescendants().OfType<FAInfoBar>().Single();
        Assert.True(infoBar is { IsOpen: true, IsVisible: true } && infoBar.Bounds.Height > 20,
            $"the open partial bar must be laid out with real height; got IsOpen={infoBar.IsOpen} " +
            $"IsVisible={infoBar.IsVisible} height={infoBar.Bounds.Height}");
        Assert.True(WY(infoBar) > WY(bar) + bar.Bounds.Height,
            "the open partial bar must sit below the command bar (its top margin applies while open)");
        double slack = WY(grid) - (WY(infoBar) + infoBar.Bounds.Height);
        Assert.True(Math.Abs(slack) < 0.5,
            $"the grid must sit flush below the OPEN partial bar; measured slack {slack}");

        await fx.DisposeAsync();
    }
}
