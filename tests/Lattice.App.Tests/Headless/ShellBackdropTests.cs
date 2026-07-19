using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
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

// PR A / #11 code half. The headless platform grants NO Mica, so these exercise the OPAQUE
// FALLBACK path — the one CI runs. The Mica-granted branch is decided by MicaBackdropPolicy
// (unit-tested in MicaBackdropPolicyTests) and its pixels are verified on Win11 hardware in #11.
public class ShellBackdropTests
{
    private static (ShellWindow Window, ShellViewModel Shell) MakeShell()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}.json");
        var registry = new HostRegistry(new LatticeConfig(5, []), path);
        var manager = new HostMonitorManager(registry, () => new FakeGuiRpcClient(), new FakeTimeProvider());
        var store = new HostStore(registry, manager, new ImmediateUiDispatcher());
        var uiState = new UiStateStore(Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}-ui.json"));
        var shell = new ShellViewModel(registry, store, new ManualUiClock(), uiState, () => new FakeGuiRpcClient());
        var window = new ShellWindow { DataContext = shell };
        return (window, shell);
    }

    private static IBrush? CanvasBrush(Window window) =>
        window.TryFindResource("LatticeCanvasBrush", window.ActualThemeVariant, out var value)
            ? value as IBrush
            : null;

    // Regression for the resource-override recursion (Codex P1, PR #99): under a granted Mica,
    // ApplyBackdrop writes Window.Resources, which re-notifies the canvas resource observable and
    // re-enters ApplyBackdrop. An unconditional write recursed to a UI-thread stack overflow; the
    // idempotent write must terminate. Forcing the granted level here drives the REAL re-entry path —
    // if the guard regressed this test would stack-overflow, not merely fail an assert.
    [AvaloniaFact]
    public void Granted_mica_toggles_the_command_bar_override_without_recursing()
    {
        var (window, _) = MakeShell();
        window.Show();
        Layout(window);

        // Headless coerces ActualTransparencyLevel to supported levels (never Mica), so apply the
        // granted level directly. This still mutates Window.Resources from inside the resource-observer
        // callback (the recursion trigger); an unconditional write would stack-overflow here.
        window.ApplyBackdropForTest(WindowTransparencyLevel.Mica);
        Assert.True(window.Resources.ContainsKey("LatticeCommandBarSurfaceBrush"));
        Assert.Same(Brushes.Transparent, window.Resources["LatticeCommandBarSurfaceBrush"]);

        // Applying Mica again is idempotent — no second write, no recursion.
        window.ApplyBackdropForTest(WindowTransparencyLevel.Mica);
        Assert.True(window.Resources.ContainsKey("LatticeCommandBarSurfaceBrush"));

        // Back to the fallback: the override is removed, still no recursion.
        window.ApplyBackdropForTest(WindowTransparencyLevel.None);
        Assert.False(window.Resources.ContainsKey("LatticeCommandBarSurfaceBrush"));

        window.Close();
    }

    // The exact #11 regression: the window must NOT paint transparent when Mica was not granted, and
    // must resolve the opaque canvas brush. Flipping MicaBackdropPolicy's else-branch to transparent,
    // or dropping the ApplyBackdrop wiring, reddens this (Background would be Transparent / null).
    [AvaloniaFact]
    public void Window_resolves_the_opaque_canvas_when_mica_is_not_granted()
    {
        var (window, _) = MakeShell();
        window.Show();
        Layout(window);

        // Guard: we are genuinely on the fallback path, not accidentally testing a granted Mica.
        Assert.NotEqual(WindowTransparencyLevel.Mica, window.ActualTransparencyLevel);

        var canvas = CanvasBrush(window);
        Assert.NotNull(canvas);
        Assert.Same(canvas, window.Background);
        Assert.NotSame(Brushes.Transparent, window.Background);

        window.Close();
    }

    // All four view command bars must be driven by the SHARED region brush so one Mica toggle flips
    // them together; on the fallback path that brush resolves to the opaque theme-dict seed. Naming
    // only Tasks (or leaving any on LatticeSurfaceBrush) would strand a command bar opaque over Mica
    // (Codex P2 on plan #89) — this asserts every view's CommandBar resolves LatticeCommandBarSurfaceBrush.
    // Renders each data view DIRECTLY (via HostGraphFixture, the MotionWiringTests pattern) rather than
    // navigating the shell: since PR C1 the shell hosts pages in a TransitioningContentControl that keeps
    // BOTH the outgoing and incoming view in the tree during the 150 ms switch, so a shell-nav lookup
    // would find two CommandBars/ContentRegions. A direct per-view render has exactly one of each.
    private static Control BuildDataView(int view, HostGraphFixture fx) => view switch
    {
        0 => new TasksView { DataContext = new TasksViewModel(fx.Store, fx.Clock, fx.UiState, fx.Density, fx.Control) },
        1 => new ProjectsView { DataContext = new ProjectsViewModel(fx.Store, fx.Clock) },
        2 => new TransfersView { DataContext = new TransfersViewModel(fx.Store, fx.Clock, fx.Density) },
        3 => new EventLogView { DataContext = new EventLogViewModel(fx.Store) },
        _ => throw new ArgumentOutOfRangeException(nameof(view)),
    };

    [AvaloniaTheory]
    [InlineData(0)] // Tasks
    [InlineData(1)] // Projects
    [InlineData(2)] // Transfers
    [InlineData(3)] // EventLog
    public void Every_command_bar_is_driven_by_the_shared_region_brush(int view)
    {
        using var fx = new HostGraphFixture();
        var window = fx.Host(BuildDataView(view, fx));
        window.Show();
        fx.Layout();

        var commandBar = window.GetVisualDescendants().OfType<Border>()
            .Single(b => b.Name == "CommandBar");
        var regionBrush = window.TryFindResource("LatticeCommandBarSurfaceBrush", window.ActualThemeVariant, out var v)
            ? v as IBrush : null;
        Assert.NotNull(regionBrush);
        Assert.Same(regionBrush, commandBar.Background);
        // The region brush must NOT be the content/overlay surface brush — keeping them separate is
        // what lets the Mica toggle transparentise command bars without touching loading/empty overlays.
        var surface = window.TryFindResource("LatticeSurfaceBrush", window.ActualThemeVariant, out var s)
            ? s as IBrush : null;
        Assert.NotSame(surface, commandBar.Background);
    }

    // The first-run empty state sits at the shell root (NOT inside PageHost), so it needs its own
    // opaque canvas — otherwise a granted Mica would show through the empty state on a new install
    // (Codex P2, PR #99). No hosts → FirstRun is the visible content surface and must stay opaque.
    [AvaloniaFact]
    public void First_run_content_stays_opaque()
    {
        var (window, shell) = MakeShell();
        window.Show();
        Layout(window);

        Assert.False(shell.HasHosts);
        Assert.True(window.FirstRun.IsVisible);
        var canvas = CanvasBrush(window);
        Assert.NotNull(canvas);
        Assert.Same(canvas, window.FirstRun.Background);

        window.Close();
    }

    // Option A (owner call, PR #99): the nav Mica reveal is delivered by the window going transparent
    // under Mica while FA 3.0.1's NavigationView pane is ALREADY transparent (empirically #00F3F3F3,
    // both themes) — the pane shows the window backdrop through, no extra wiring, and binding it to an
    // opaque brush was declined (it would change the fallback nav colour). Guard that empirical basis:
    // if a future FluentAvalonia paints the pane opaque, this reddens, signalling the nav reveal then
    // needs explicit wiring (the SplitView.PaneBackground, NOT NavigationViewDefaultPaneBackground —
    // overriding that key was verified to not drive the pane).
    [AvaloniaFact]
    public void Nav_pane_is_transparent_so_it_reveals_the_window_backdrop()
    {
        var (window, shell) = MakeShell();
        window.Show();
        shell.Registry.AddHost(TestData.MakeHostConfig());
        Layout(window);

        var pane = window.Nav.GetVisualDescendants().OfType<SplitView>().First();
        var paneBg = pane.PaneBackground as ISolidColorBrush;
        Assert.NotNull(paneBg);
        Assert.Equal(0, paneBg!.Color.A); // fully transparent → the window backdrop (Mica) shows through

        window.Close();
    }

    // The view host (ViewHost, a TransitioningContentControl since PR C1) must NOT be opaque: a command
    // bar lives at the top of each hosted view, and under Mica it goes transparent to reveal the
    // material. An opaque host behind it would reveal the canvas instead of the window/Mica, silently
    // killing the command-bar half of the fix (Codex P2, PR #99). Re-adding any host background reddens this.
    [AvaloniaFact]
    public void View_host_is_not_opaque_so_command_bars_can_reveal_mica()
    {
        var (window, shell) = MakeShell();
        window.Show();
        shell.Registry.AddHost(TestData.MakeHostConfig());
        Layout(window);

        var viewHost = window.GetVisualDescendants().OfType<ContentControl>().Single(c => c.Name == "ViewHost");
        Assert.Null(viewHost.Background);

        window.Close();
    }

    // Content opacity now lives per-view, in the region BELOW the command bar (design: content is
    // always opaque), so dropping PageHost's blanket paint does not let Mica bleed through content.
    // Each data view's ContentRegion (grid + overlays) paints the opaque canvas.
    [AvaloniaTheory]
    [InlineData(0)] // Tasks
    [InlineData(1)] // Projects
    [InlineData(2)] // Transfers
    [InlineData(3)] // EventLog
    public void Every_content_region_is_opaque(int view)
    {
        using var fx = new HostGraphFixture();
        var window = fx.Host(BuildDataView(view, fx));
        window.Show();
        fx.Layout();

        var contentRegion = window.GetVisualDescendants().OfType<Panel>().Single(p => p.Name == "ContentRegion");
        var canvas = CanvasBrush(window);
        Assert.NotNull(canvas);
        Assert.Same(canvas, contentRegion.Background);
    }
}
