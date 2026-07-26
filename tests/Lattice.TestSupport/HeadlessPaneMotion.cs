using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace Lattice.Tests;

/// <summary>
/// Issue #133 — takes the wall clock out of the nav pane's WIDTH, for every headless test
/// assembly that shows a <c>ShellWindow</c>. Consume from the app-builder chain:
/// <c>.UseHeadless(...).WithoutPaneWidthAnimation()</c>.
///
/// Avalonia's SplitView theme animates PART_PaneRoot's Width with a DoubleTransition — probed
/// live at 0 → 235 → 260 over ~250 ms of REAL time. The host rail's ListBox sits inside that
/// pane on the default VirtualizingStackPanel, which realizes containers only for its effective
/// viewport. Measured while the pane is still at Width=0 that viewport is the EMPTY rect, so
/// RealizeElements' do/while exits after the anchor: every captured failure showed exactly
/// <c>realized=1: [AllHosts]</c> against <c>ItemCount=3</c>.
///
/// The state never heals, which is what made the flake so confusing. LayoutManager caches the
/// last effective viewport it raised per listener and re-raises only when the computed rect
/// CHANGES; the panel only refreshes <c>_lastMeasuredExtendedViewport</c> from that event. Once
/// the empty rect is latched everything is measure/arrange-valid and nothing re-invalidates —
/// confirmed in the dumps: three further <c>Layout(window)</c> passes, an explicit LayoutManager
/// pass and a forced <c>panel.InvalidateMeasure()</c> all left <c>realized=1</c> and
/// <c>_viewport=0,0,0,0</c>. Retrying or pumping harder — both banned here anyway — would not
/// have worked; only never latching the empty rect does.
///
/// The wall clock is the load coupling. A test body that fits inside one 60 fps frame never
/// observes an intermediate width, so the pane measures at its final size and the suite is
/// green. Under a full-solution <c>dotnet test</c> the test process contends with its siblings,
/// a body straddles a frame boundary, HeadlessRenderTimer (a DispatcherTimer at
/// DispatcherPriority.UiThreadRender) ticks mid-test, and the transition starts. Symptoms seen:
/// "Rail row has no realized container" from <c>RailInput.ClickRow</c>, its LINQ-shaped twin
/// "Sequence contains no matching element" at the visual-tree row lookups, and — the second
/// instance, which is why this lives here rather than in one assembly's builder —
/// <c>MenuSeparatorVisualTests</c>' hostrail case finding 0 dividers because the row whose menu
/// it opens was never realized.
///
/// Scope is deliberately this one control part. The app's own transitions (App.axaml,
/// ProjectsView) animate Opacity and RenderTransform, which do not affect layout, and
/// MotionWiringTests asserts those — a blanket "no transitions in tests" hook would defeat that
/// gate. Pixel baselines are unaffected: they were already captured at the settled pane width,
/// which is now simply reached without the animation.
/// </summary>
public static class HeadlessPaneMotion
{
    private static bool _installed;

    /// <summary>Whether <see cref="WithoutPaneWidthAnimation"/> has run in this process.</summary>
    public static bool IsInstalled => _installed;

    /// <summary>
    /// Clears <c>PART_PaneRoot</c>'s transitions as each SplitView applies its template, so the
    /// pane's width is a pure function of <c>IsPaneOpen</c>/<c>DisplayMode</c> and container
    /// realization is a pure function of state.
    ///
    /// A class handler rather than a style, because the ControlTemplate declares those
    /// Transitions inline: that is a LOCAL value and outranks every style. An application-level
    /// <c>SplitView /template/ Panel#PART_PaneRoot</c> setter was tried first and measurably
    /// lost — the DoubleTransition was still on the instance. Writing the property back at
    /// TemplateApplied is the lever that beats it.
    ///
    /// Idempotent, and deliberately process-scoped: class handlers live on the static
    /// RoutedEvent and survive the per-test application rebuild that
    /// <c>AvaloniaTestIsolationLevel.PerTest</c> performs, so an unguarded hook would stack one
    /// handler per <c>[AvaloniaFact]</c>.
    /// </summary>
    public static AppBuilder WithoutPaneWidthAnimation(this AppBuilder builder)
    {
        if (_installed)
            return builder;
        _installed = true;
        TemplatedControl.TemplateAppliedEvent.AddClassHandler<SplitView>(static (_, e) =>
        {
            if (e.NameScope.Find<Panel>("PART_PaneRoot") is { } paneRoot)
                paneRoot.Transitions = null;
        });
        return builder;
    }
}
