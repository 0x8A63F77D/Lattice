using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
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
/// 1. A REAL layout pass — every time state changes, not just once. <c>Show()</c> runs an initial
///    one (measured: straight after it the shell's window bounds and <c>PART_PaneRoot</c> are
///    already arranged), but nothing after that is free, and a bare
///    <c>window.Measure()/Arrange()</c> bypasses <c>LayoutManager.ExecuteLayoutPass</c>, so the
///    notifications scoped to a layout pass — <c>SizeChanged</c>, <c>EffectiveViewportChanged</c> —
///    never fire. Those are what make FluentAvalonia pick its pane display mode and what makes a
///    <c>VirtualizingStackPanel</c> realize item containers. Further passes are driven by the render
///    loop, and Avalonia's headless render timer is a 60 fps <c>DispatcherTimer</c>: a plain
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
/// The nav pane's own width gets a SECOND, stronger guarantee on top of that detach — see
/// <see cref="SuppressPaneWidthAnimation"/>. The detach can only act once a settle is under way,
/// i.e. after at least one measure/arrange; the pane must never be measured at width 0 even once,
/// because that latch does not heal. Issue #198 converged the two mechanisms into this file.
///
/// Do NOT "fix" a flake in this family with a sleep or a retry — a fixed settle is exactly the
/// wall-clock guess this helper replaces.
/// </summary>
public static class HeadlessLayout
{
    /// <summary>
    /// Issue #133 / #198 — takes the wall clock out of the nav pane's WIDTH, from the moment the
    /// pane exists, for every headless test assembly in the process.
    ///
    /// WHY A SECOND LEVER AT ALL, given the settle already detaches transitions. The detach runs
    /// inside <see cref="Settle(Visual, Action)"/>, so the earliest it can bite is after the first
    /// measure/arrange — and a headless <c>Window.Show()</c> already runs a full initial layout
    /// pass (measured: straight after <c>Show()</c> the shell's <c>PART_PaneRoot</c> is present and
    /// arranged). One measurement at width 0 is all it takes. The host rail's <c>ListBox</c> sits
    /// inside that pane on the default <c>VirtualizingStackPanel</c>, which realizes containers
    /// only for its effective viewport; measured while the pane is at width 0 that viewport is the
    /// EMPTY rect, so <c>RealizeElements</c> exits after the anchor — every captured #133 failure
    /// showed exactly <c>realized=1: [AllHosts]</c> against <c>ItemCount=3</c>.
    ///
    /// And the state never heals. <c>LayoutManager</c> caches the last effective viewport it raised
    /// per listener and re-raises only when the computed rect CHANGES; the panel refreshes
    /// <c>_lastMeasuredExtendedViewport</c> only from that event. Once the empty rect is latched
    /// everything is measure/arrange-valid and nothing re-invalidates — confirmed in #195's dumps:
    /// three further <c>Layout(window)</c> passes, an explicit LayoutManager pass and a forced
    /// <c>panel.InvalidateMeasure()</c> all left <c>realized=1</c> and <c>_viewport=0,0,0,0</c>.
    /// Pumping harder cannot recover it; only never latching the empty rect does. Hence: the pane
    /// is born without a width transition, so its width is a pure function of
    /// <c>IsPaneOpen</c>/<c>DisplayMode</c> and realization is a pure function of state.
    ///
    /// A TemplateApplied class handler rather than a style, because the ControlTemplate declares
    /// those Transitions INLINE: that is a local value and outranks every style. #195 measured an
    /// application-level <c>SplitView /template/ Panel#PART_PaneRoot</c> setter losing to it — the
    /// DoubleTransition was still on the instance.
    ///
    /// WHY A PLAIN ASSIGNMENT HERE IS SAFE, when the settle deliberately does NOT use one. The
    /// settle's override must be reversible, and #197's Codex P2 established that restoring by
    /// assignment promotes a style-sourced value to LOCAL, after which a class or theme change can
    /// no longer restyle it. Neither half of that applies here: this write is permanent (there is
    /// no restore that could promote anything), and what it overwrites is the template's own inline
    /// local value on a freshly-templated part, so no style source is being shadowed — the priority
    /// of the property is local before and after. Pinned by
    /// <c>HeadlessLayoutSettleTests.Layout_leaves_style_sourced_transitions_at_style_priority</c>,
    /// which still requires every SURVIVING transition to be off Animation priority, and by
    /// <c>Layout_leaves_the_nav_pane_root_without_a_width_transition</c>, which goes red if this
    /// handler is dropped (the settle would then detach and RE-ATTACH the pane's transition).
    ///
    /// WHEN IT HAS TO RUN, and why each consuming assembly calls it from a module initializer. The
    /// deadline is the first <c>Window.Show()</c> in the process, not the first <c>Layout</c> —
    /// Show already runs a full initial layout pass. Attaching this to the type initializer of
    /// <c>HeadlessLayout</c>, or to a module initializer on THIS assembly, was measured
    /// insufficient: a test that shows a shell before touching any shared test double never loads
    /// this module in time, and the probe saw the pane root arrive with its DoubleTransition live
    /// (and its width sampled at 320 in one run, 260 in another — the wall-clock variance itself).
    /// A module initializer in the TEST assembly has no such gap: the CLR runs it before any code
    /// in that assembly, which is upstream of test discovery, of <c>BuildAvaloniaApp</c>, and of
    /// every window. Class handlers live on the static RoutedEvent, so one registration covers the
    /// whole process and survives the per-test application rebuild that
    /// <c>AvaloniaTestIsolationLevel.PerTest</c> performs — hence the idempotence guard, without
    /// which the second assembly (or a repeat call) would stack a duplicate handler.
    ///
    /// Scope is deliberately this one control part. The app's own transitions (App.axaml,
    /// ProjectsView) animate Opacity and RenderTransform, which do not affect layout, and
    /// MotionWiringTests asserts those — a blanket "no transitions in tests" hook would defeat that
    /// gate. Pixel baselines are unaffected: they were already captured at the settled pane width,
    /// which is now simply reached without the animation.
    /// </summary>
    public static void SuppressPaneWidthAnimation()
    {
        if (_paneWidthAnimationSuppressed)
            return;
        _paneWidthAnimationSuppressed = true;
        TemplatedControl.TemplateAppliedEvent.AddClassHandler<SplitView>(static (_, e) =>
        {
            if (e.NameScope.Find<Panel>("PART_PaneRoot") is { } paneRoot)
                paneRoot.Transitions = null;
        });
    }

    private static bool _paneWidthAnimationSuppressed;

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
        var detached = new List<IDisposable>();
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

    // Detaches every transition currently live under <paramref name="root"/>, appending one undo
    // handle per visual to <paramref name="sink"/>, and returns how many were found.
    // Already-detached visuals filter themselves out (their effective Transitions is now null), so
    // repeated calls only pick up newcomers.
    //
    // The override goes in at Animation priority — the top of the value-precedence stack — rather
    // than as a plain `a.Transitions = null` assignment. Two reasons. It has to outrank a LOCAL
    // value, which is what a ControlTemplate's inline <Panel.Transitions> is (SplitView's
    // PART_PaneRoot); and disposing it must REVEAL the original value rather than overwrite it,
    // because App.axaml / TasksView.axaml / ProjectsView.axaml declare Transitions from a STYLE
    // setter. Writing the effective value back by assignment would promote it to a local value,
    // after which a later class or theme change could no longer restyle it and the test would
    // silently stop reproducing production behaviour (Codex P2).
    private static int DetachNew(Visual root, List<IDisposable> sink)
    {
        List<Animatable> animated = root.GetSelfAndVisualDescendants()
            .OfType<Animatable>()
            .Where(a => a.Transitions is { Count: > 0 })
            .ToList();

        foreach (Animatable a in animated)
            if (a.SetValue(Animatable.TransitionsProperty, null, BindingPriority.Animation) is { } undo)
                sink.Add(undo);

        return animated.Count;
    }

    private static void Reattach(List<IDisposable> detached)
    {
        foreach (IDisposable undo in detached)
            undo.Dispose();
    }
}
