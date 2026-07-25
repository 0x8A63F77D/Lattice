using System;
using System.Collections.Generic;
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
using Lattice.App.Views;
using Xunit;

namespace Lattice.VisualTests;

/// <summary>
/// Pixel gate for issue #155: the context-menu divider must paint as a thin rule sitting in
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
/// The probe drives the REAL <c>ProjectsView</c> / <c>TasksView</c> context flyouts, not a
/// hand-rolled replica, so reintroducing the pseudo-item in either view fails this gate. The views
/// are rendered without a DataContext: their items bind to commands that then do not resolve, so
/// the rows paint in their disabled foreground. That is deliberate and harmless — this gate
/// measures the geometry of glyph ink and the rule, not text colour — and it keeps the test on the
/// "faithful standalone control under the shipping styles" posture the other pixel gates use,
/// with no async host or snapshot pipeline involved.
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

    public static TheoryData<string, string> Menus() => new()
    {
        { "projects", "dark" }, { "projects", "light" },
        { "tasks", "dark" }, { "tasks", "light" },
    };

    [AvaloniaTheory]
    [MemberData(nameof(Menus))]
    public void Menu_divider_paints_as_a_thin_rule_in_fa_rhythm(string menu, string theme)
    {
        var variant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;

        // Warm the theme's Skia caches once (a variant's first render differs) and dispose the
        // frame immediately — this test shares its process with the env-gated baseline captures,
        // and a leaked WriteableBitmap can segfault libSkiaSharp (VisualCapture's Avalonia #19611
        // note).
        Measure(menu, variant).Dispose();

        using var m = Measure(menu, variant);

        Assert.True(m.RuleRows.Count is > 0 and <= 2,
            $"the {menu} divider must paint as a thin full-width rule; " +
            $"found {m.RuleRows.Count} full-width rows at [{string.Join(",", m.RuleRows)}]");

        Assert.True(m.GapAbove <= MaxInkToRulePx && m.GapBelow <= MaxInkToRulePx,
            $"the {menu} divider ({theme}) must sit in FA's menu rhythm, not in a full menu-item " +
            $"slot: ink-to-rule above={m.GapAbove}px, below={m.GapBelow}px (cap {MaxInkToRulePx}px). " +
            "A ~27px gap means the divider is the MenuItem Header=\"-\" pseudo-item again (#155).");

        Assert.True(Math.Abs(m.GapAbove - m.GapBelow) <= MaxAsymmetryPx,
            $"the {menu} divider ({theme}) rule must be centred between its neighbours: " +
            $"above={m.GapAbove}px, below={m.GapBelow}px (tolerance ±{MaxAsymmetryPx}px).");
    }

    private sealed record Measurement(IReadOnlyList<int> RuleRows, int GapAbove, int GapBelow, IDisposable Frame)
        : IDisposable
    {
        public void Dispose() => Frame.Dispose();
    }

    private static Measurement Measure(string menu, ThemeVariant variant)
    {
        Application.Current!.RequestedThemeVariant = variant;

        var flyout = menu switch
        {
            "projects" => (MenuFlyout)new ProjectsView().Grid.ContextFlyout!,
            "tasks" => (MenuFlyout)new TasksView().Grid.ContextFlyout!,
            _ => throw new ArgumentOutOfRangeException(nameof(menu), menu, "unknown menu"),
        };

        // A tiny top-left anchor so the flyout opens fully inside the captured window frame.
        // Headless popups are overlay popups, so the menu renders into the window's own frame.
        var anchor = new Border
        {
            Width = 8,
            Height = 8,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Background = Avalonia.Media.Brushes.Transparent,
        };
        var window = new Window { Width = 300, Height = 260, Content = anchor };

        // In the app these views live inside ShellWindow, which carries the design-3b rule. The
        // bare test host does not, and without it the pseudo-item would collapse on its own and
        // this probe would false-green on the very defect it exists to catch.
        var rowHeight = new Style(x => x.OfType<MenuFlyoutPresenter>().Descendant().OfType<MenuItem>());
        rowHeight.Setters.Add(new Setter(Layoutable.MinHeightProperty, 32.0));
        window.Styles.Add(rowHeight);

        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            flyout.ShowAt(anchor, showAtPointer: false);
            Dispatcher.UIThread.RunJobs();

            var presenter = window.GetVisualDescendants().OfType<MenuFlyoutPresenter>().Single();
            var frame = window.CaptureRenderedFrame()
                ?? throw new InvalidOperationException("No rendered frame captured.");
            var px = PixelBuffer.From(frame);

            var origin = presenter.TranslatePoint(new Point(0, 0), window)!.Value;
            int x0 = (int)origin.X, y0 = (int)origin.Y;
            int w = (int)presenter.Bounds.Width, h = (int)presenter.Bounds.Height;

            // The menu's own background, sampled at a quiet spot inset from the top-right corner
            // (clear of glyph ink and of the presenter's rounded border).
            var bg = px.Rgb(x0 + w - 6, y0 + 6);
            bool Ink(int x, int y)
            {
                var (r, g, b) = px.Rgb(x, y);
                return Math.Abs(r - bg.r) + Math.Abs(g - bg.g) + Math.Abs(b - bg.b) > 12;
            }

            // Per row inside the presenter: how much ink, and how wide it spans.
            var rows = new List<(int Y, int Count, int Span)>();
            for (int y = y0 + 2; y < y0 + h - 2; y++)
            {
                int count = 0, first = -1, last = -1;
                for (int x = x0 + 2; x < x0 + w - 2; x++)
                    if (Ink(x, y)) { count++; if (first < 0) first = x; last = x; }
                if (count > 0) rows.Add((y - y0, count, last - first));
            }

            // The rule is the full-width run. Skip the top of the menu: the first item takes focus
            // when a flyout opens and paints a full-width hover fill, which is also full width.
            int firstItemBottom = 40;
            var ruleRows = rows
                .Where(r => r.Y > firstItemBottom && r.Span >= w - 6 && r.Count >= w - 6)
                .Select(r => r.Y)
                .ToList();
            if (ruleRows.Count == 0)
                return new Measurement(ruleRows, int.MaxValue, int.MaxValue, frame);

            // Glyph ink is narrow; the rule and the hover fill are not. Measure from the rule to
            // the nearest text ink above and below it.
            int rule = ruleRows[0];
            bool IsGlyph((int Y, int Count, int Span) r) => r.Span < w - 20;
            int above = rows.Where(r => r.Y < rule - 1 && IsGlyph(r)).Select(r => r.Y).DefaultIfEmpty(-1).Max();
            int below = rows.Where(r => r.Y > rule + 1 && IsGlyph(r)).Select(r => r.Y).DefaultIfEmpty(-1).Min();
            Assert.True(above >= 0 && below >= 0,
                $"expected menu text above and below the {menu} divider; above={above}, below={below}");

            return new Measurement(ruleRows, rule - above, below - rule, frame);
        }
        finally
        {
            window.Close();
        }
    }
}
