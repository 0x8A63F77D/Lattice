using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Lattice.Tests;

/// <summary>
/// Drives a headless window to its SETTLED layout, so a test reads a steady end state and never
/// an arbitrary intermediate frame. Consume via <c>using static Lattice.Tests.HeadlessLayout;</c>
/// so call sites read <c>Layout(window)</c>.
///
/// Two distinct things have to happen, and neither is free:
///
/// 1. A REAL layout pass. Headless <c>Show()</c> runs none, and a bare
///    <c>window.Measure()/Arrange()</c> bypasses <c>LayoutManager.ExecuteLayoutPass</c>, so the
///    notifications scoped to a layout pass — <c>SizeChanged</c>, <c>EffectiveViewportChanged</c> —
///    never fire. Those are what make FluentAvalonia pick its pane display mode and what makes a
///    <c>VirtualizingStackPanel</c> realize item containers. The pass is driven by the render loop,
///    and Avalonia's headless render timer is a 60 fps <c>DispatcherTimer</c>: a plain
///    <c>Dispatcher.UIThread.RunJobs()</c> runs a pass only if that timer happens to be DUE, which
///    is a wall-clock coin flip. <see cref="Pump"/> calls <c>ForceRenderTimerTick()</c> instead, so
///    the pass is a post-condition rather than an accident.
///
/// 2. No property transition in flight. Avalonia's <c>SplitView</c> slides the nav pane open with a
///    <c>DoubleTransition</c>, and the animation clock is REAL time — so measure/arrange samples it
///    at whatever phase the wall clock is in. Observed on one unchanged shell: rail widths of 0, 22,
///    24, 141, 184, 193, 206, 222, 224, 246, 250, 251 and 254 across consecutive builds. At width 0
///    the rail's containers are not realized at all, which is the "Rail row has no realized
///    container" failure this helper exists to prevent. Transitions are therefore DETACHED for the
///    duration of the settle — an animated property falls back to its base value, i.e. the exact
///    target the animation was heading for — and re-attached afterwards, so tests that assert a
///    transition is wired (MotionWiringTests, ProgressFillBehaviorTests, ProjectsViewTests) still
///    see it. Re-attaching starts nothing: the property is already at its final value.
///
/// Do NOT "fix" a flake in this family with a sleep or a retry — a fixed settle is exactly the
/// wall-clock guess this helper replaces.
/// </summary>
public static class HeadlessLayout
{
    public static void Layout(Window window)
    {
        // Pass 1 applies control templates, so the transition-bearing template children exist to
        // be detached below.
        MeasureArrange(window);
        Settle(window, () => MeasureArrange(window));
    }

    /// <summary>
    /// Settles a subtree that is already laid out but has just gained content — a flyout or dialog
    /// opened into the window's overlay layer, whose entrance transition would otherwise still be
    /// running when the test reads geometry or captures a frame.
    /// </summary>
    public static void Settle(Visual root) => Settle(root, static () => { });

    private static void Settle(Visual root, Action relayout)
    {
        var detached = new List<(Animatable Target, Transitions Saved)>();
        try
        {
            for (var round = 0; round < 20; round++)
            {
                // Re-scan every round, not once up front: a pump applies queued templates and
                // realizes virtualized children, and each of those arrives with its OWN
                // transitions live. Detaching only the visuals that existed before the first
                // pump would let a late-materialized popup or row animate right through the
                // settle and hand the test an intermediate frame anyway (Codex P2).
                DetachNew(root, detached);
                relayout();
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();

                // Settled = nothing left to run AND nothing new appeared to detach.
                if (!Dispatcher.UIThread.HasJobsWithPriority(DispatcherPriority.SystemIdle)
                    && DetachNew(root, detached) == 0)
                    break;
            }
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            Reattach(detached);
        }
    }

    /// <summary>
    /// Runs the dispatcher and the render loop to a fixed point: jobs, then a forced render timer
    /// tick (which is what actually executes a layout pass), repeated until no job is left. Use
    /// this — never a bare <c>RunJobs()</c> — wherever a test pumps the UI without re-laying out a
    /// window. The iteration cap only bounds a genuine live-lock; the loop normally exits on the
    /// job check after one or two rounds.
    /// </summary>
    public static void Pump()
    {
        for (var i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            if (!Dispatcher.UIThread.HasJobsWithPriority(DispatcherPriority.SystemIdle))
                break;
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static void MeasureArrange(Window window)
    {
        window.Measure(new Size(window.Width, window.Height));
        window.Arrange(new Rect(0, 0, window.Width, window.Height));
    }

    // Detaches every transition currently live under <paramref name="root"/> and appends it to
    // <paramref name="sink"/>, returning how many were found. Already-detached visuals filter
    // themselves out (their Transitions is now null), so repeated calls only pick up newcomers.
    private static int DetachNew(Visual root, List<(Animatable Target, Transitions Saved)> sink)
    {
        List<Animatable> animated = root.GetSelfAndVisualDescendants()
            .OfType<Animatable>()
            .Where(a => a.Transitions is { Count: > 0 })
            .ToList();

        foreach (Animatable a in animated)
        {
            sink.Add((a, a.Transitions!));
            a.Transitions = null;
        }
        return animated.Count;
    }

    private static void Reattach(List<(Animatable Target, Transitions Saved)> detached)
    {
        foreach ((Animatable target, Transitions saved) in detached)
            target.Transitions = saved;
    }
}
