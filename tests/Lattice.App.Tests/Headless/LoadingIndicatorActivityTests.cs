using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Headless.XUnit;
using Lattice.App.Tests.Fakes;
using Lattice.App.ViewModels;
using Lattice.App.Views;
using Lattice.Tests;
using Xunit;

namespace Lattice.App.Tests.Headless;

// Issue #95 (second finding) + #168: the loading overlays used to hold an FAProgressRing whose
// composition animation checks only IsActive — NOT IsVisible — so a ring shown once kept
// re-registering for every frame, keeping the dispatcher non-idle and stalling pointer input on
// the dispatcher's 1 s InputStarvationTimeout. Binding IsActive bounded WHEN it spun but not the
// starvation itself: measurements on #167 put input-priority stalls at ~1030-1095 ms for as long
// as a ring stayed visible, and lowering DispatcherOptions.InputStarvationTimeout was verified to
// reach the render scheduler yet did NOT help. The loading indicator is now a stock indeterminate
// ProgressBar (measured clean), swept here from Statistics to Tasks/Projects/Transfers.
//
// The #95 leak-gate discipline carries over to the new control: IsIndeterminate must track
// IsLoading so the animation runs only while a first fetch is pending. A silently dropped binding
// fails the during-loading assertion (IsIndeterminate defaults FALSE) — the mirror image of the
// retired ring gate, where IsActive defaulted true and only the post-loading assertion bit.
// The starvation itself is timing and cannot be asserted deterministically; the measurement
// evidence lives on #95, #168 and the PR bodies.
public class LoadingIndicatorActivityTests
{
    // Single(): the loading overlay's bar is each view's ONLY ProgressBar, so this also pins
    // that a future second bar has to come with its own gate.
    private static ProgressBar BarOf(Window window) =>
        window.GetVisualDescendants().OfType<ProgressBar>().Single();

    [AvaloniaFact]
    public async Task Tasks_loading_bar_stops_when_loading_ends()
    {
        var fx = new HostGraphFixture();
        var vm = new TasksViewModel(fx.Store, fx.Clock, fx.UiState, fx.Density, fx.Control);
        var view = new TasksView { DataContext = vm };
        var window = fx.Host(view);
        fx.AddHost("host-a", new FakeGuiRpcClient());
        fx.Layout();

        Assert.True(vm.IsLoading);                  // pre-snapshot: overlay up…
        Assert.True(BarOf(window).IsIndeterminate); // …bar animating

        fx.Start();
        // Settle on the BAR's end state, not the VM flag: the flag flips first and the
        // binding target is the behavior under guard (fixture determinism contract).
        await fx.SettleAsync(() => !BarOf(window).IsIndeterminate);
        Assert.False(vm.IsLoading); // the leak gate held: loading ended and the bar stopped
        await fx.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task Projects_loading_bar_stops_when_loading_ends()
    {
        var fx = new HostGraphFixture();
        var vm = new ProjectsViewModel(fx.Store, fx.Clock, fx.Control, FakeAttachFlow.NoopRun, new ImmediateUiDispatcher());
        var view = new ProjectsView { DataContext = vm };
        var window = fx.Host(view);
        fx.AddHost("host-a", new FakeGuiRpcClient());
        fx.Layout();

        Assert.True(vm.IsLoading);
        Assert.True(BarOf(window).IsIndeterminate);

        fx.Start();
        await fx.SettleAsync(() => !BarOf(window).IsIndeterminate);
        Assert.False(vm.IsLoading);
        await fx.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task Transfers_loading_bar_stops_when_loading_ends()
    {
        var fx = new HostGraphFixture();
        var vm = new TransfersViewModel(fx.Store, fx.Clock, fx.Density);
        var view = new TransfersView { DataContext = vm };
        var window = fx.Host(view);
        fx.AddHost("host-a", new FakeGuiRpcClient());
        fx.Layout();

        Assert.True(vm.IsLoading);
        Assert.True(BarOf(window).IsIndeterminate);

        fx.Start();
        await fx.SettleAsync(() => !BarOf(window).IsIndeterminate);
        Assert.False(vm.IsLoading);
        await fx.DisposeAsync();
    }
}
