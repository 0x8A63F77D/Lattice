using System.Linq;
using System.Threading.Tasks;
using Avalonia.VisualTree;
using Avalonia.Headless.XUnit;
using FluentAvalonia.UI.Controls;
using Lattice.App.ViewModels;
using Lattice.App.Views;
using Lattice.Tests;
using Xunit;

namespace Lattice.App.Tests.Headless;

// Issue #95 (second finding): FAProgressRing's composition animation checks only
// IsActive — NOT IsVisible. A ring whose loading overlay has been shown once keeps
// re-registering for every animation frame after the overlay hides, keeping the
// dispatcher permanently non-idle; pointer input (dispatched at Input priority) then
// waits for the dispatcher's 1 s InputStarvationTimeout on EVERY interaction — the
// owner-reported ~1 s click lag. The fix binds each loading ring's IsActive to the
// view's IsLoading. These gates pin that wiring: the ring must be active during
// loading and — the leak gate — INACTIVE once loading ends. (IsActive defaults to
// true, so only the post-loading assertion catches a silently dropped binding.)
// The starvation itself is timing and cannot be asserted deterministically; the
// measurement evidence lives on #95 and the PR body.
public class ProgressRingActivityTests
{
    private static FAProgressRing RingOf(Avalonia.Controls.Window window) =>
        window.GetVisualDescendants().OfType<FAProgressRing>().Single();

    [AvaloniaFact]
    public async Task Tasks_loading_ring_stops_when_loading_ends()
    {
        var fx = new HostGraphFixture();
        var vm = new TasksViewModel(fx.Store, fx.Clock, fx.UiState, fx.Density);
        var view = new TasksView { DataContext = vm };
        var window = fx.Host(view);
        fx.AddHost("host-a", new FakeGuiRpcClient());
        fx.Layout();

        Assert.True(vm.IsLoading);          // pre-snapshot: overlay up…
        Assert.True(RingOf(window).IsActive); // …ring animating

        fx.Start();
        // Settle on the RING's end state, not the VM flag: the flag flips first and the
        // binding target is the behavior under guard (fixture determinism contract).
        await fx.SettleAsync(() => !RingOf(window).IsActive);
        Assert.False(vm.IsLoading); // the leak gate held: loading ended and the ring stopped
        await fx.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task Projects_loading_ring_stops_when_loading_ends()
    {
        var fx = new HostGraphFixture();
        var vm = new ProjectsViewModel(fx.Store, fx.Clock);
        var view = new ProjectsView { DataContext = vm };
        var window = fx.Host(view);
        fx.AddHost("host-a", new FakeGuiRpcClient());
        fx.Layout();

        Assert.True(vm.IsLoading);
        Assert.True(RingOf(window).IsActive);

        fx.Start();
        await fx.SettleAsync(() => !RingOf(window).IsActive);
        Assert.False(vm.IsLoading);
        await fx.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task Transfers_loading_ring_stops_when_loading_ends()
    {
        var fx = new HostGraphFixture();
        var vm = new TransfersViewModel(fx.Store, fx.Clock, fx.Density);
        var view = new TransfersView { DataContext = vm };
        var window = fx.Host(view);
        fx.AddHost("host-a", new FakeGuiRpcClient());
        fx.Layout();

        Assert.True(vm.IsLoading);
        Assert.True(RingOf(window).IsActive);

        fx.Start();
        await fx.SettleAsync(() => !RingOf(window).IsActive);
        Assert.False(vm.IsLoading);
        await fx.DisposeAsync();
    }
}
