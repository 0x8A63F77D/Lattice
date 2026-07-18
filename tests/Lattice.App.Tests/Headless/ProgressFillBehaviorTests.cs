using System;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Threading;
using Lattice.App.Views;
using Xunit;

namespace Lattice.App.Tests.Headless;

// ProgressFillBehavior.SnapOnRebind stops the progress-width transition from animating when a
// virtualized cell is recycled to a different row (sort/scroll/filter), so a reused bar snaps to
// the new row's value instead of sliding from the previous row's (Codex P2, PR #103).
//
// Under the headless platform a running transition holds the property at its START value (the clock
// does not auto-advance), and removing the Transitions collection reveals the property's base
// (target) value — verified directly here. That is exactly the recycle artifact and its fix, so the
// gate is machine-readable without a real animation clock.
public class ProgressFillBehaviorTests
{
    private static Border NewFill(double width) => new()
    {
        Width = width,
        DataContext = new object(),
        Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = Layoutable.WidthProperty,
                Duration = TimeSpan.FromMilliseconds(200),
                Easing = new LinearEasing(),
            },
        },
    };

    [AvaloniaFact]
    public void A_recycle_snaps_the_width_past_an_in_flight_transition()
    {
        var border = NewFill(10);
        ProgressFillBehavior.SetSnapOnRebind(border, true);
        var window = new Window { Content = border };
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);

        border.Width = 90;                          // starts a transition; headless holds it at 10
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
        Assert.Equal(10, border.Width);             // sanity: the bar IS mid-transition (the artifact)

        border.DataContext = new object();          // recycle to another row → suppressor fires
        Assert.Equal(90, border.Width);             // snapped to target, not stuck at the old 10

        Dispatcher.UIThread.RunJobs();              // the posted restore runs
        Assert.NotNull(border.Transitions);         // transition re-armed for the next in-place tick
        window.Close();
    }

    [AvaloniaFact]
    public void Without_snap_on_rebind_the_recycled_cell_stays_mid_transition()
    {
        // The negative control: the same sequence WITHOUT the behavior leaves the bar animating from
        // the previous value — i.e. this is the bug the attached property fixes.
        var border = NewFill(10);
        var window = new Window { Content = border };
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);

        border.Width = 90;
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
        border.DataContext = new object();

        Assert.Equal(10, border.Width);             // no suppressor → still stuck at the old row's width
        window.Close();
    }

    [AvaloniaFact]
    public void Toggling_snap_off_detaches_the_handler()
    {
        var border = NewFill(10);
        ProgressFillBehavior.SetSnapOnRebind(border, true);
        var window = new Window { Content = border };
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);

        ProgressFillBehavior.SetSnapOnRebind(border, false);
        border.Width = 90;
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
        border.DataContext = new object();

        Assert.Equal(10, border.Width);             // handler removed → no snap
        window.Close();
    }
}
