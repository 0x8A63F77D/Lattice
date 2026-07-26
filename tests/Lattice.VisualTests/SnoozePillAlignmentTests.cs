using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
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
/// Issue #176 — the snooze time pill must centre its pause icon on the DIGITS, in every font.
///
/// THE DEFECT. <c>VerticalAlignment="Center"</c> centres each child's LAYOUT box independently.
/// A TextBlock's box is a line box (ascent + descent + line gap); digits have no descender, so the
/// glyph ink does not sit at that box's centre, and how far off it sits is a per-font constant.
/// "Centre both boxes" therefore aligns in one font and misaligns in the next. Measured in this
/// very pill, rendered: the pause bars sat 0.84 px below the digits in Helvetica and 1.31 px off in
/// Courier New, while in Inter the same layout was aligned to 0.03 px.
///
/// WHY INTER MATTERS AND WHY IT IS NOT ENOUGH. The visual harness pins Inter so glyph geometry does
/// not depend on the runner's font stack, and Inter is one of the faces where the ink happens to sit
/// dead centre in its line box — so a gate that probes only Inter is structurally blind to this
/// entire defect class. The gate below therefore sweeps several metrically different families, and
/// <see cref="Resolve"/> rejects any the font manager silently substituted rather than letting a
/// missing family re-probe Inter under another name.
///
/// WHICH FONT THE APP ACTUALLY SHIPS WITH. Lattice pins no font, so it draws in the platform
/// default. On macOS that is what Avalonia's Skia font manager reports — <c>SKTypeface.Default</c>,
/// measured as <b>Helvetica</b>, not the system UI font. Helvetica is the worst case in the numbers
/// above, and it is what the owner saw on hardware.
///
/// TWO LAYERS, DELIBERATELY. <see cref="Pause_icon_is_centred_on_the_digit_band"/> asserts the
/// ARRANGED geometry: exact, no rasterizer in the loop, identical on every runner. The pixel probe
/// then asserts that the painted ink really lands there, which layout numbers cannot promise — at 1x
/// the rasterizer snaps a glyph's baseline to a whole device pixel, so rendered ink can sit up to
/// half a pixel off what layout computed, in a font-dependent direction. That quantisation, not the
/// layout, is what sets the pixel tolerance.
///
/// NOT env-gated: like <see cref="MenuSeparatorVisualTests"/> these assert geometry, not committed
/// screenshots, so they gate the fix in the normal <c>dotnet test</c> lane on every CI OS.
/// </summary>
[Trait("Category", "Visual")]
public class SnoozePillAlignmentTests
{
    /// <summary>
    /// Rendered ink may sit up to half a device pixel off the arranged geometry because the glyph
    /// rasterizer snaps the baseline to the pixel grid; at 1x that is 0.5 px of slack nothing in the
    /// layout can remove. The cap comes from that mechanism, not from fitting the observations —
    /// but it does bracket them. Post-fix, the worst deviation over the probed families is 0.36 px
    /// (Courier New @1x) and 0.21 px (Times New Roman @2x); pre-fix, Helvetica — the face the app
    /// actually ships with — sat at 0.75 px @1x and 0.84 px @2x.
    /// </summary>
    private const double MaxInkDeviationDip = 0.5;

    /// <summary>The arranged geometry is computed, not rasterised, so it is exact up to floating
    /// point; a thousandth of a pixel is slack for the arithmetic, not for the layout. Pre-fix the
    /// same numbers are off by 0.34 px in Inter and ~0.8 px in Helvetica.</summary>
    private const double ArrangedTolerance = 0.001;

    /// <summary>Families worth probing: metrically different from each other and from Inter. Ones
    /// this runner lacks are skipped (see <see cref="Resolve"/>); Inter is embedded, so at least it
    /// is always measured — and the LAYOUT assertions are red pre-fix in Inter too, so the gate does
    /// not go vacuous on a runner with a bare font stack.</summary>
    private static readonly string[] FamilyNames =
    [
        "Inter", "Helvetica", "Helvetica Neue", ".AppleSystemUIFont", "Arial",
        "Verdana", "Georgia", "Courier New", "Times New Roman", "Menlo", "Trebuchet MS", "Segoe UI",
    ];

    /// <summary>
    /// The arranged invariant: the pause icon's box centre sits on the centre of the DIGIT BAND's
    /// ink — the outline extents of <see cref="TextInkCollapseConverter.DigitBand"/> at the time
    /// text's own typeface and size.
    ///
    /// This is what fails pre-fix in every family, Inter included (0.34 px there, because the
    /// TextBlock's arranged box is its line height rounded UP to a whole pixel while its ink is
    /// centred on the unrounded line). Comparing the two BOXES instead would pass in both states
    /// and gate nothing.
    /// </summary>
    [AvaloniaFact]
    public void Pause_icon_is_centred_on_the_digit_band()
    {
        AssertAcrossFamilies(scaling: 1.0, (pill, family, report) =>
        {
            double delta = pill.IconBoxCentre - pill.BandInkCentre;
            if (Math.Abs(delta) > ArrangedTolerance)
                report($"{family}: the pause icon's box centre sits {delta:+0.000;-0.000} px from the " +
                       "digit band's ink centre — the layout centres boxes, not ink.");
        });
    }

    /// <summary>
    /// The pill's own content box must be symmetric about that same band: the fix works by
    /// collapsing the text's layout box onto the ink, so the Border's padding is measured from the
    /// digits. A centred icon inside an off-centre pill reads as misaligned just as loudly — and a
    /// fix that only nudged the icon would have introduced exactly that.
    /// </summary>
    [AvaloniaFact]
    public void Pill_content_box_is_symmetric_about_the_digit_band()
    {
        AssertAcrossFamilies(scaling: 1.0, (pill, family, report) =>
        {
            double delta = pill.ContentBoxCentre - pill.BandInkCentre;
            if (Math.Abs(delta) > ArrangedTolerance)
                report($"{family}: the pill's content box centre sits {delta:+0.000;-0.000} px from the " +
                       "digit band's ink centre, so its padding is not symmetric about the digits.");
        });
    }

    /// <summary>
    /// The rendered half: the pause bars' painted ink and the digits' painted ink share a centre.
    ///
    /// EXPECTED VALUE, NOT ZERO. The pill centres the fixed digit BAND, not the four glyphs that
    /// happen to be on screen — deliberately, so a clock ticking 14:30 → 14:31 cannot resize the
    /// pill by the fraction of a pixel that separates one digit's overshoot from another's. In a
    /// face with lining figures those two boxes coincide and the expectation is ~0; in one with
    /// old-style figures (Georgia) the shown digits sit ~1 px off their own band's centre by the
    /// type designer's intent, and demanding 0 there would be demanding the pill jitter as the
    /// minutes tick. So the assertion is against that per-font expectation, computed from outlines.
    /// </summary>
    // 1x is the harness default; 2x is a Retina Mac — the owner's own condition, and the one where
    // layout rounding lands on half-DIP boundaries instead of whole ones.
    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void Rendered_pause_ink_and_digit_ink_share_a_centre(double scaling)
    {
        AssertAcrossFamilies(scaling, (pill, family, report) =>
        {
            var ink = pill.MeasureRenderedInk();
            double expected = pill.ShownTextOffsetFromBand;

            if (Math.Abs(ink.BoxCentreDelta - expected) > MaxInkDeviationDip)
                report($"{family} @{scaling}x: the pause bars' ink box centre sits " +
                       $"{ink.BoxCentreDelta:+0.000;-0.000} px from the digits' (expected " +
                       $"{expected:+0.000;-0.000} px, cap ±{MaxInkDeviationDip}). " +
                       $"bars={ink.IconTop:F3}..{ink.IconBottom:F3}, digits={ink.TextTop:F3}..{ink.TextBottom:F3}, " +
                       $"ink centroids {ink.CentroidDelta:+0.000;-0.000} apart.");
        });
    }

    /// <summary>
    /// Runs <paramref name="probe"/> over every family this runner actually has, inside ONE window.
    ///
    /// ONE WINDOW, NOT ONE PER FAMILY. A window per theory case was measured to destabilise the
    /// neighbouring pixel gate: at twelve families this assembly's <see cref="MenuSeparatorVisualTests"/>
    /// host-rail case intermittently captured a frame with no menu in it (3 of 4 runs), and the flake
    /// tracked the ShellWindow count — two families were stable across the same runs, twelve were not.
    /// Sweeping the families inside a single window keeps the coverage (which is the entire point of
    /// this gate) while opening four windows per class instead of thirty-six. Failures are collected
    /// rather than thrown one at a time, so a regression reports EVERY family it broke, not just the
    /// first the runner happened to reach.
    /// </summary>
    private static void AssertAcrossFamilies(double scaling, Action<Pill, string, Action<string>> probe)
    {
        var failures = new List<string>();
        var probed = new List<string>();

        using var pill = Pill.Open(ThemeVariant.Dark, scaling);

        // A theme's first render populates Skia's glyph/render caches and differs from later ones
        // (VisualWarmup's finding); this gate shares its process with the baseline captures.
        pill.Capture().Dispose();

        foreach (var family in FamilyNames)
        {
            if (Resolve(family) is not { } resolved)
                continue;
            probed.Add(family);
            pill.UseFont(resolved);
            probe(pill, family, failures.Add);
        }

        // Without this a runner whose font manager resolved nothing would report a serene green over
        // an empty sweep. Inter ships embedded with the harness, so its absence means the probe
        // itself is broken, not the runner.
        Assert.Contains("Inter", probed);

        Assert.True(failures.Count == 0,
            $"probed {probed.Count} of {FamilyNames.Length} families ({string.Join(", ", probed)}):" +
            Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    /// <summary>The family, or null when this runner does not have it. A font manager that cannot
    /// find a family silently substitutes the default one — which would re-probe Inter under another
    /// name and manufacture a green.</summary>
    private static FontFamily? Resolve(string family)
    {
        var candidate = new FontFamily(family);
        if (!FontManager.Current.TryGetGlyphTypeface(new Typeface(candidate), out var typeface))
            return null;
        if (typeface.FamilyName == family)
            return candidate;
        // ".AppleSystemUIFont" only ever resolves under its display name ("System Font"), so the
        // dot-prefixed aliases cannot be matched by name. Accepting them unconditionally would
        // re-admit the very substitution this method exists to reject — on a runner without the
        // Apple stack the alias resolves to the DEFAULT family, i.e. Inter wearing another name.
        return family.StartsWith('.') && typeface.FamilyName != FontManager.Current.DefaultFontFamily.Name
            ? candidate
            : null;
    }

    private readonly record struct RenderedInk(
        double IconTop, double IconBottom, double IconCentroid,
        double TextTop, double TextBottom, double TextCentroid)
    {
        public double BoxCentreDelta => (IconTop + IconBottom) / 2 - (TextTop + TextBottom) / 2;
        public double CentroidDelta => IconCentroid - TextCentroid;
    }

    /// <summary>
    /// A real <see cref="ShellWindow"/> showing a snoozed host, with the pill's font forced to the
    /// family under probe. The rail row's snooze state is set on the view model directly: which FORM
    /// the pill takes (time vs chip) and where its text comes from are the ViewModel's business and
    /// are covered by RunModePillPolicy's own tests — what is under test here is the template's
    /// geometry.
    /// </summary>
    private sealed class Pill : IDisposable
    {
        private readonly ShellWindow _window;
        private readonly PathIcon _icon;
        private readonly TextBlock _text;
        private readonly Border _border;
        private readonly double _scaling;

        private Pill(ShellWindow window, PathIcon icon, TextBlock text, Border border, double scaling)
        {
            _window = window;
            _icon = icon;
            _text = text;
            _border = border;
            _scaling = scaling;
        }

        public const string ShownTime = "14:30";

        public static Pill Open(ThemeVariant variant, double scaling)
        {
            Application.Current!.RequestedThemeVariant = variant;

            string Temp(string tag) => Path.Combine(Path.GetTempPath(), $"lattice-pill-{Guid.NewGuid():N}-{tag}.json");
            var registry = new HostRegistry(new LatticeConfig(5, []), Temp("hosts"));
            // Never started, so the clock behind the manager is inert here.
            var manager = new HostMonitorManager(registry, () => new FakeGuiRpcClient(), TimeProvider.System);
            var store = new HostStore(registry, manager, new InlineUiDispatcher());
            var shell = new ShellViewModel(registry, store, new InertUiClock(), new UiStateStore(Temp("ui")),
                () => new FakeGuiRpcClient());

            var window = new ShellWindow { DataContext = shell, Width = 1400, Height = 800 };
            window.Show();
            window.SetRenderScaling(scaling);
            registry.AddHost(TestData.MakeHostConfig(name: "mini-01"));
            Arrange(window);

            var row = shell.RailEntries.OfType<HostRailItemViewModel>().Single();
            row.SnoozedUntilTimeText = ShownTime;
            row.PillShowsTime = true;
            Arrange(window);

            var icon = Named<PathIcon>(window, "SnoozePillIcon");
            var text = Named<TextBlock>(window, "SnoozePillTime");

            return new Pill(window, icon, text, (Border)icon.GetVisualParent()!.GetVisualParent()!, scaling);
        }

        /// <summary>Re-renders the pill in <paramref name="family"/>. Set on the text element, so the
        /// margin binding sees the same change an inherited font change would produce at runtime.</summary>
        public void UseFont(FontFamily family)
        {
            TextElement.SetFontFamily(_text, family);
            Arrange(_window);
        }

        private static void Arrange(Window window)
        {
            Dispatcher.UIThread.RunJobs();
            window.Measure(new Size(window.Width, window.Height));
            window.Arrange(new Rect(0, 0, window.Width, window.Height));
            Dispatcher.UIThread.RunJobs();
        }

        private static T Named<T>(Visual root, string name) where T : Control =>
            root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

        private Rect InkOf(string content) =>
            new FormattedText(content, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(_text.FontFamily, _text.FontStyle, _text.FontWeight), _text.FontSize,
                Brushes.Black).BuildGeometry(default)!.Bounds;

        private double Top(Visual visual) => visual.TranslatePoint(new Point(0, 0), _window)!.Value.Y;

        private Visual Content => (Visual)_icon.GetVisualParent()!;

        /// <summary>Window-space centre of the digit band's ink, as arranged.</summary>
        public double BandInkCentre
        {
            get
            {
                var ink = InkOf(TextInkCollapseConverter.DigitBand);
                return Top(_text) + (ink.Top + ink.Bottom) / 2;
            }
        }

        public double IconBoxCentre => Top(_icon) + _icon.Bounds.Height / 2;

        public double ContentBoxCentre => Top(Content) + Content.Bounds.Height / 2;

        /// <summary>How far the four glyphs actually on screen sit from the centre of the band the
        /// pill aligns to: ~0 for lining figures, ~1 px for old-style ones.</summary>
        public double ShownTextOffsetFromBand
        {
            get
            {
                var band = InkOf(TextInkCollapseConverter.DigitBand);
                var shown = InkOf(ShownTime);
                return (band.Top + band.Bottom) / 2 - (shown.Top + shown.Bottom) / 2;
            }
        }

        public Avalonia.Media.Imaging.Bitmap Capture() =>
            _window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No rendered frame captured.");

        /// <summary>
        /// Measures each element's painted ink inside the pill, in DIPs, so the 1x and 2x runs are
        /// directly comparable.
        /// </summary>
        public RenderedInk MeasureRenderedInk()
        {
            using var frame = Capture();
            var pixels = PixelBuffer.From(frame);

            Rect Device(Visual visual)
            {
                var origin = visual.TranslatePoint(new Point(0, 0), _window)!.Value;
                return new Rect(origin.X * _scaling, origin.Y * _scaling,
                    visual.Bounds.Width * _scaling, visual.Bounds.Height * _scaling);
            }

            // Scan the CONTENT box and nothing else — no bleed rows. Both elements' ink lies inside
            // it by construction (the box is sized to the taller of the two) in the fixed AND the
            // broken layout, so nothing measurable is clipped; while OUTSIDE it the pill's padding
            // rows carry a faint fringe off the border that cleared the ink floor for the lighter
            // faces and dragged their measured extent to the scan boundary.
            var content = Device(Content);
            var pillRect = Device(_border);
            int y0 = (int)Math.Ceiling(content.Top), y1 = (int)Math.Floor(content.Bottom) - 1;

            var background = Modal(pixels, (int)pillRect.Left + 2, (int)pillRect.Right - 2, y0, y1);
            var iconInk = Ink(pixels, background, Device(_icon), content, y0, y1);
            var textInk = Ink(pixels, background, Device(_text), content, y0, y1);

            return new RenderedInk(
                (iconInk.Top - pillRect.Top) / _scaling, (iconInk.Bottom - pillRect.Top) / _scaling,
                (iconInk.Centroid - pillRect.Top) / _scaling,
                (textInk.Top - pillRect.Top) / _scaling, (textInk.Bottom - pillRect.Top) / _scaling,
                (textInk.Centroid - pillRect.Top) / _scaling);
        }

        private static (int r, int g, int b) Modal(PixelBuffer pixels, int x0, int x1, int y0, int y1)
        {
            var histogram = new Dictionary<(int, int, int), int>();
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    histogram[pixels.Rgb(x, y)] = histogram.GetValueOrDefault(pixels.Rgb(x, y)) + 1;
            return histogram.MaxBy(entry => entry.Value).Key;
        }

        /// <summary>
        /// Sub-pixel ink extents and coverage centroid for one element's column range. Coverage is a
        /// pixel's distance from the pill's fill normalised by the strongest ink in the same columns,
        /// so a partly covered edge row's coverage IS the fraction of that row the glyph covers —
        /// which is what makes the extents sub-pixel instead of integer-quantised.
        /// </summary>
        private static (double Top, double Bottom, double Centroid) Ink(
            PixelBuffer pixels, (int r, int g, int b) background, Rect element, Rect content, int y0, int y1)
        {
            // Clamped INSIDE the content box. The element rects touch its edges, and a bare 1px
            // bleed reached the pill's rounded corner: its anti-aliased arc cleared the ink floor and
            // dragged the measured text extent down to the scan boundary.
            int x0 = Math.Max((int)Math.Floor(element.Left) - 1, (int)Math.Ceiling(content.Left));
            int x1 = Math.Min((int)Math.Ceiling(element.Right) + 1, (int)Math.Floor(content.Right) - 1);

            int Distance(int x, int y)
            {
                var (r, g, b) = pixels.Rgb(x, y);
                return Math.Abs(r - background.r) + Math.Abs(g - background.g) + Math.Abs(b - background.b);
            }

            int strongest = 0;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    strongest = Math.Max(strongest, Distance(x, y));
            Assert.True(strongest > 0, "the probed element painted no ink inside the pill.");

            var rows = new double[y1 - y0 + 1];
            double weighted = 0, weight = 0;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    double coverage = Math.Clamp(Distance(x, y) / (double)strongest, 0, 1);
                    rows[y - y0] = Math.Max(rows[y - y0], coverage);
                    weighted += coverage * (y + 0.5);
                    weight += coverage;
                }

            // A floor above anti-aliasing noise but below any real glyph row: the fill's dithering
            // and the rounded corners' fringe both clear a 2% floor and would be read as ink several
            // rows outside the glyphs.
            const double InkFloor = 0.10;
            int first = Array.FindIndex(rows, row => row > InkFloor);
            int last = Array.FindLastIndex(rows, row => row > InkFloor);
            Assert.True(first >= 0,
                $"no row in x={x0}..{x1} cleared the {InkFloor:P0} ink floor, so the element's extent " +
                "cannot be measured — it painted nothing, or only anti-aliasing fringe.");
            return (y0 + first + (1 - rows[first]), y0 + last + rows[last], weighted / weight);
        }

        public void Dispose() => _window.Close();
    }

    /// <summary>Runs posted work inline. The App.Tests fake of the same shape is xunit-side.</summary>
    private sealed class InlineUiDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;
        public void Post(Action action) => action();
    }

    /// <summary>A clock that never ticks — these gates render frames, they do not age state.</summary>
    private sealed class InertUiClock : IUiClock
    {
        public event EventHandler? Tick;
        public DateTimeOffset Now => new(2026, 7, 9, 12, 0, 0, TimeSpan.Zero);
        private void Unused() => Tick?.Invoke(this, EventArgs.Empty);
    }
}
