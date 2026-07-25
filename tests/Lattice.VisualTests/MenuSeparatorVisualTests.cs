using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lattice.App.Infrastructure;
using Lattice.App.ViewModels;
using Lattice.App.Views;
using Lattice.Core;
using Lattice.Tests;
using Xunit;

namespace Lattice.VisualTests;

/// <summary>
/// Pixel gate for issue #155: every context-menu divider must paint as a thin rule sitting in
/// FA's normal menu rhythm, not as a hairline marooned in a full 32 px menu-item slot.
///
/// The bug: the dividers were the pseudo-separator <c>MenuItem</c> whose header is <c>"-"</c>.
/// Avalonia swaps that item's template to a bare <c>Separator</c>, so the LINE ALWAYS PAINTED —
/// but the pseudo-item is still a real <c>MenuItem</c>, so ShellWindow's design-3b rule
/// (<c>MenuFlyoutPresenter MenuItem</c> ⇒ <c>MinHeight = 32</c>) inflated its slot and left ~27 px
/// of dead space on each side of the rule. The fix is a raw <c>&lt;Separator/&gt;</c>, which is not
/// a <c>MenuItem</c> and so escapes that rule.
///
/// WHAT THIS GATE MEASURES, AND WHY IT IS THE MAGNITUDE AND NOT THE SYMMETRY. Issue #155 proposed
/// a symmetry assert (space above the line == space below) as the kill criterion. Measured against
/// real pixels, symmetry does NOT discriminate: the pseudo-item centres its rule inside the
/// oversized slot, so BOTH states are symmetric — 27/26 px broken vs 12/12 px fixed. The signal
/// that separates them is the ABSOLUTE ink-to-rule distance. Both are asserted here (symmetry
/// remains a real regression guard against an off-centre rule), but the magnitude is what fails
/// red on the defect; this is recorded so a future reader does not mistake the symmetry assert for
/// the #155 detector.
///
/// COVERAGE. The probe drives the REAL flyouts — <c>ProjectsView</c>, <c>TasksView</c>, and the
/// host rail inside a real <c>ShellWindow</c> — not hand-rolled replicas, and it pins the expected
/// number of rules per menu. Reverting any one of the five dividers the fix touched turns this
/// gate red. The grid views are rendered without a DataContext: their items bind to commands that
/// then do not resolve, so the rows paint in their disabled foreground. That is deliberate and
/// harmless — this gate measures the geometry of glyph ink and the rule, not text colour.
///
/// NOT env-gated: like <see cref="ComboBoxTextCenteringVisualTests"/> this asserts integer-layout
/// distances under a pinned font (Inter) at RenderScaling 1.0, not pixel-exact colour, so it is
/// stable across the ubuntu/windows/macOS runners and gates the fix in the normal
/// <c>dotnet test</c> lane.
/// </summary>
[Trait("Category", "Visual")]
public class MenuSeparatorVisualTests
{
    // Measured: 12 px fixed, 27 px with the pseudo-item. The band sits between them with ~6 px of
    // headroom on the passing side, absorbing glyph-AA jitter across render backends.
    private const int MaxInkToRulePx = 18;

    // The rule must be centred between its neighbours; 27/26 and 12/12 both satisfy this, so it is
    // a regression guard, not the #155 detector (see the class remarks).
    private const int MaxAsymmetryPx = 3;

    public static TheoryData<string, string, int> Menus() => new()
    {
        // menu, theme, expected divider count
        { "projects", "dark", 1 }, { "projects", "light", 1 },
        { "tasks", "dark", 1 }, { "tasks", "light", 1 },
        { "hostrail", "dark", 2 }, { "hostrail", "light", 2 },
    };

    [AvaloniaTheory]
    [MemberData(nameof(Menus))]
    public void Menu_dividers_paint_as_thin_rules_in_fa_rhythm(string menu, string theme, int expectedRules)
    {
        var variant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;

        // Warm the theme's Skia caches once (a variant's first render differs) and dispose the
        // frame immediately — this test shares its process with the env-gated baseline captures,
        // and a leaked WriteableBitmap can segfault libSkiaSharp (VisualCapture's Avalonia #19611
        // note).
        Measure(menu, variant).Dispose();

        using var m = Measure(menu, variant);

        Assert.True(m.Rules.Count == expectedRules,
            $"expected {expectedRules} painted divider(s) in the {menu} menu, found {m.Rules.Count} " +
            $"at [{string.Join(",", m.Rules.Select(r => r.Y))}]. A missing rule means the divider " +
            "does not paint; an extra one means a full-width fill was mistaken for a rule.");

        foreach (var rule in m.Rules)
        {
            Assert.True(rule.Thickness <= 2,
                $"the {menu} divider at y={rule.Y} must be a thin rule, not a band " +
                $"({rule.Thickness}px tall).");

            Assert.True(rule.GapAbove <= MaxInkToRulePx && rule.GapBelow <= MaxInkToRulePx,
                $"the {menu} divider at y={rule.Y} ({theme}) must sit in FA's menu rhythm, not in a " +
                $"full menu-item slot: ink-to-rule above={rule.GapAbove}px, below={rule.GapBelow}px " +
                $"(cap {MaxInkToRulePx}px). A ~27px gap means the divider is the " +
                "MenuItem Header=\"-\" pseudo-item again (#155).");

            Assert.True(Math.Abs(rule.GapAbove - rule.GapBelow) <= MaxAsymmetryPx,
                $"the {menu} divider at y={rule.Y} ({theme}) must be centred between its " +
                $"neighbours: above={rule.GapAbove}px, below={rule.GapBelow}px " +
                $"(tolerance ±{MaxAsymmetryPx}px).");
        }
    }

    private sealed record Rule(int Y, int Thickness, int GapAbove, int GapBelow);

    private sealed record Measurement(IReadOnlyList<Rule> Rules, IDisposable Frame) : IDisposable
    {
        public void Dispose() => Frame.Dispose();
    }

    /// <summary>
    /// Opens <paramref name="menu"/>'s real flyout, captures the frame, and measures every painted
    /// divider in it. Headless popups are OVERLAY popups, so the menu renders into the host
    /// window's own frame and <c>CaptureRenderedFrame</c> sees it.
    /// </summary>
    private static Measurement Measure(string menu, ThemeVariant variant)
    {
        Application.Current!.RequestedThemeVariant = variant;

        var (window, anchor, flyout) = menu switch
        {
            "projects" => GridHost(new ProjectsView()),
            "tasks" => GridHost(new TasksView()),
            "hostrail" => ShellHost(),
            _ => throw new ArgumentOutOfRangeException(nameof(menu), menu, "unknown menu"),
        };

        try
        {
            Dispatcher.UIThread.RunJobs();
            flyout.ShowAt(anchor, showAtPointer: false);
            Dispatcher.UIThread.RunJobs();

            var presenter = window.GetVisualDescendants().OfType<MenuFlyoutPresenter>().First();
            var frame = window.CaptureRenderedFrame()
                ?? throw new InvalidOperationException("No rendered frame captured.");
            return new Measurement(MeasureRules(PixelBuffer.From(frame), window, presenter), frame);
        }
        finally
        {
            window.Close();
        }
    }

    private static IReadOnlyList<Rule> MeasureRules(PixelBuffer px, Window window, MenuFlyoutPresenter presenter)
    {
        var origin = presenter.TranslatePoint(new Point(0, 0), window)!.Value;
        int x0 = (int)origin.X, y0 = (int)origin.Y;
        int w = (int)presenter.Bounds.Width, h = (int)presenter.Bounds.Height;

        // The menu's own background is the MODAL colour over the presenter's rect: the surface
        // dominates the area, while glyphs, rules and the focus fill are each a minority. Sampling
        // a single "quiet-looking" pixel instead is fragile — the corner that is bare menu surface
        // in a standalone host lands inside the focused item's fill in the real ShellWindow, which
        // silently shifts the ink threshold and hides the rules.
        var histogram = new Dictionary<(int r, int g, int b), int>();
        for (int y = y0 + 2; y < y0 + h - 2; y++)
            for (int x = x0 + 2; x < x0 + w - 2; x++)
            {
                var c = px.Rgb(x, y);
                histogram[c] = histogram.GetValueOrDefault(c) + 1;
            }
        var bg = histogram.MaxBy(kv => kv.Value).Key;

        // Two ink thresholds, because the two things being measured have very different contrast.
        // The RULE is deliberately subtle (FA's separator sits a few units off the menu surface),
        // so finding it needs a low threshold. TEXT is near-full contrast even when disabled. Using
        // the low threshold for both is what a first cut did, and it silently broke the gate: the
        // 1px rule's own anti-aliasing fringe on the rows either side of it cleared the low bar and
        // was mistaken for neighbouring text, collapsing every measured gap to 1px and turning the
        // pseudo-item defect green. Text must clear a bar an AA fringe cannot.
        int Contrast(int x, int y)
        {
            var (r, g, b) = px.Rgb(x, y);
            return Math.Abs(r - bg.r) + Math.Abs(g - bg.g) + Math.Abs(b - bg.b);
        }
        const int FaintInk = 12;   // anything off the surface: finds the subtle rule
        const int TextInk = 90;    // sum over RGB; menu text clears this, an AA fringe does not

        // Per row inside the presenter: faint-ink extent (for the rule) and whether the row
        // carries real text.
        var rows = new List<(int Y, int Count, int Span, bool Text)>();
        for (int y = y0 + 2; y < y0 + h - 2; y++)
        {
            int count = 0, first = -1, last = -1, strong = 0;
            for (int x = x0 + 2; x < x0 + w - 2; x++)
            {
                int c = Contrast(x, y);
                if (c > FaintInk) { count++; if (first < 0) first = x; last = x; }
                if (c > TextInk) strong++;
            }
            // 3+ strong pixels, so a stray AA speck cannot pass for a glyph row.
            if (count > 0) rows.Add((y - y0, count, last - first, strong >= 3));
        }

        // A rule spans the full menu width. So does the fill on the item that takes focus when the
        // flyout opens — and in the host-rail menu the two span the SAME width, so width alone
        // cannot tell them apart. What does: the fill is a ~32px BAND, a rule is 1-2px. Group the
        // full-width rows into consecutive runs and keep only the thin runs. This is why no "skip
        // the first item" fudge is needed, and it generalises to menus with several rules.
        //
        // The w-12 margin is measured, not guessed: in the host-rail menu the widest GLYPH row
        // (an item with both a leading icon and a trailing submenu chevron) spans w-17, while
        // rules and fills span w-8. The threshold sits in that gap.
        bool FullWidth((int Y, int Count, int Span, bool Text) r) => r.Span >= w - 12 && r.Count >= w - 12;
        var bands = new List<List<int>>();
        foreach (var y in rows.Where(FullWidth).Select(r => r.Y))
        {
            if (bands.Count > 0 && bands[^1][^1] == y - 1) bands[^1].Add(y);
            else bands.Add([y]);
        }

        // Measure each rule to the nearest real menu text above and below it.
        bool IsGlyph((int Y, int Count, int Span, bool Text) r) => r.Text && !FullWidth(r);
        var rules = new List<Rule>();
        foreach (var band in bands.Where(b => b.Count <= 2))
        {
            int top = band[0], bottom = band[^1];
            int above = rows.Where(r => r.Y < top && IsGlyph(r)).Select(r => r.Y).DefaultIfEmpty(-1).Max();
            int below = rows.Where(r => r.Y > bottom && IsGlyph(r)).Select(r => r.Y).DefaultIfEmpty(-1).Min();
            Assert.True(above >= 0 && below >= 0,
                $"expected menu text above and below the divider at y={top}; above={above}, below={below}");
            rules.Add(new Rule(top, band.Count, top - above, below - bottom));
        }

        return rules;
    }

    /// <summary>
    /// A grid view's context flyout on a bare host. In the app these views live inside ShellWindow,
    /// which carries the design-3b rule; the bare host does not, and without it the pseudo-item
    /// would collapse on its own and this probe would false-green on the very defect it exists to
    /// catch. The host-rail case needs no such injection — it uses the real ShellWindow.
    /// </summary>
    private static (Window Window, Control Anchor, MenuFlyout Flyout) GridHost(UserControl view)
    {
        var grid = view switch
        {
            ProjectsView p => p.Grid,
            TasksView t => t.Grid,
            _ => throw new ArgumentOutOfRangeException(nameof(view)),
        };
        var flyout = (MenuFlyout)grid.ContextFlyout!;

        // A tiny top-left anchor so the flyout opens fully inside the captured window frame.
        var anchor = new Border
        {
            Width = 8,
            Height = 8,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Background = Avalonia.Media.Brushes.Transparent,
        };
        var window = new Window { Width = 300, Height = 260, Content = anchor };
        var rowHeight = new Style(x => x.OfType<MenuFlyoutPresenter>().Descendant().OfType<MenuItem>());
        rowHeight.Setters.Add(new Setter(Layoutable.MinHeightProperty, 32.0));
        window.Styles.Add(rowHeight);
        window.Show();
        return (window, anchor, flyout);
    }

    /// <summary>
    /// The host-rail row menu, on a real <see cref="ShellWindow"/> over a never-started host graph
    /// — so the design-3b MinHeight rule and the rail's own template are the shipping ones.
    /// </summary>
    private static (Window Window, Control Anchor, MenuFlyout Flyout) ShellHost()
    {
        string temp(string tag) => Path.Combine(Path.GetTempPath(), $"lattice-visual-{Guid.NewGuid():N}-{tag}.json");
        var registry = new HostRegistry(new LatticeConfig(5, []), temp("hosts"));
        // The monitors are never started, so the clock behind the manager is inert here.
        var manager = new HostMonitorManager(registry, () => new FakeGuiRpcClient(), TimeProvider.System);
        var store = new HostStore(registry, manager, new InlineUiDispatcher());
        var uiState = new UiStateStore(temp("ui"));
        var shell = new ShellViewModel(registry, store, new InertUiClock(), uiState, () => new FakeGuiRpcClient());

        var window = new ShellWindow { DataContext = shell, Width = 1280, Height = 800 };
        window.Show();
        registry.AddHost(TestData.MakeHostConfig(name: "mini-01"));
        Dispatcher.UIThread.RunJobs();
        window.Measure(new Size(1280, 800));
        window.Arrange(new Rect(0, 0, 1280, 800));
        Dispatcher.UIThread.RunJobs();

        var row = window.HostList.GetVisualDescendants().OfType<ListBoxItem>()
            .Single(li => li.DataContext is HostRailItemViewModel);
        var anchor = row.GetVisualDescendants().OfType<DockPanel>().First();
        return (window, anchor, (MenuFlyout)anchor.ContextFlyout!);
    }

    /// <summary>Runs posted work inline. The App.Tests fake of the same shape is xunit-side.</summary>
    private sealed class InlineUiDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;
        public void Post(Action action) => action();
    }

    /// <summary>A clock that never ticks — this gate renders one frame, it does not age state.</summary>
    private sealed class InertUiClock : IUiClock
    {
        public event EventHandler? Tick;
        public DateTimeOffset Now => new(2026, 7, 9, 12, 0, 0, TimeSpan.Zero);
        private void Unused() => Tick?.Invoke(this, EventArgs.Empty);
    }
}
