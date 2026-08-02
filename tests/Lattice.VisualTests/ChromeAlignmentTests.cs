using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Lattice.App.Aggregation;
using Lattice.App.Infrastructure;
using Lattice.App.Localization;
using Lattice.App.ViewModels;
using Lattice.App.Views;
using Lattice.Core;
using Lattice.Tests;
using Xunit;

using static Lattice.Tests.HeadlessLayout;

namespace Lattice.VisualTests;

/// <summary>
/// Issues #185 and #204 — the issue-#180 defect class OUTSIDE the data grids.
///
/// THE DEFECT is the one <see cref="StatusCellAlignmentTests"/> documents in full, and it is not
/// specific to a grid cell: <c>VerticalAlignment="Center"</c> centres each child's LAYOUT box, and
/// a TextBlock's box is a line box whose glyph ink sits at a PER-FONT offset inside it. Wherever a
/// fixed-size mark is centred beside a label, "centre both boxes" therefore aligns in one font and
/// misaligns in the next. #184 fixed the four data cells; the same construction carries the app's
/// chrome, and this gate is what keeps it fixed:
///
/// <list type="bullet">
/// <item>the freshness caption in the Tasks / Projects / Transfers command bars (a 12 px
/// stale-warning triangle beside the "updated N ago" text);</item>
/// <item>the Event Log's three priority pills (a 12 px check beside the pill's word) — the Event
/// Log has no freshness caption, which #185's site list assumed it did;</item>
/// <item>the Tasks Computing button (a 16 px glyph and a 12 px chevron either side of a 13 px
/// SemiBold label, so ONE label seats two marks of different sizes);</item>
/// <item>the Statistics legend chips (a 12 px colour swatch beside the project name);</item>
/// <item>the Statistics overflow flyout's rows (#204) — a checkbox and TWO text columns, which is
/// a different question from all of the above and has its own assertion below.</item>
/// </list>
///
/// WHAT IS PROBED, AND WHY IT IS FOUND STRUCTURALLY. <see cref="Chrome.Probes"/> does not look for
/// the marker class the fix applies; it looks for the CONSTRUCTION — a container laying glyph-sized
/// marks out beside TextBlocks on one line, outside any data-grid row, holding nothing else. Every
/// (mark, label) pairing in such a container becomes a probe. So the sweep covers the defect class
/// rather than the sites that were known to have it, and a new command bar built the same way joins
/// the gate by existing. Two container shapes are recognised, a horizontal StackPanel
/// (<see cref="Chrome.PairPanels"/>) and a single grid ROW of columns
/// (<see cref="Chrome.PairRows"/>, whose bounds that method justifies). Containers holding a
/// progress RULE are excluded by <see cref="InkAlignment.IsGlyphSizedMark"/> for the reason #180
/// gave: a 56x3 bar is not a glyph-sized mark and where it should sit against text is a separate
/// design question.
///
/// THREE LAYERS. An EXACT arranged assertion that each mark sits on its container's band (no
/// rasterizer in the loop, red pre-fix in every family); the row-level assertion #204 added, that a
/// container's text columns share ONE band; and a rendered-ink sweep proving the painted pixels
/// follow the layout.
///
/// NOT env-gated: these assert geometry, not committed screenshots, so they gate the fix in the
/// normal <c>dotnet test</c> lane on every CI OS.
/// </summary>
[Trait("Category", "Visual")]
public class ChromeAlignmentTests(ITestOutputHelper output)
{
    /// <summary>Captions the freshness pair cycles through. Chosen for ink shape, not prose: no
    /// descender / one descender / ascender + descender. If the warning triangle's seat depended on
    /// the caption at all, one of these would move it.</summary>
    private static readonly string[] Captions =
        ["Updated now", "Updated 4 m ago", "Updated 1 h ago", "Never updated"];

    /// <summary>Render scales the pixel sweep runs at: 1x is the harness default, 2x the owner's
    /// Retina Mac (and the scale where rounding lands on half-DIP boundaries).</summary>
    private static readonly double[] Scalings = [1.0, 2.0];

    /// <summary>
    /// The shipped UI scripts. zh-CN is not decoration: under it these captions and pill words are
    /// Han, whose band is a different glyph resolved through a different fallback face and whose
    /// line box is not even the Latin one. ja-JP has no resource set, so with the language
    /// preference on System a CJK machine displays the NEUTRAL ENGLISH chrome — branching the band
    /// on the culture rather than on what resolved would hand that case an ideograph band for Latin
    /// labels, and this row is the gate on that (the same trap Codex found in PR #184).
    /// </summary>
    private static readonly (string Culture, Func<string, string> Label)[] Scripts =
    [
        ("en-US", shown => shown),
        ("zh-CN", _ => "已更新"),
        ("ja-JP", shown => shown),
    ];

    /// <summary>
    /// The arranged invariant: each mark's box centre sits on the centre of its label's reference
    /// band — the outline extents of <see cref="Probe.Band"/> at that label's own typeface and size.
    ///
    /// This is what fails pre-fix in EVERY family, which is the point: the error is a per-font
    /// constant, so a gate that probed one family would call the layout fixed on the strength of
    /// that family's luck. Comparing the two BOXES instead would pass in both states and gate
    /// nothing.
    /// </summary>
    [AvaloniaFact]
    public void Mark_is_centred_on_the_reference_band()
    {
        AssertAcrossFamilies((chrome, family, report) =>
        {
            foreach (var script in Scripts)
            {
                if (!chrome.UseUiCulture(script.Culture)) continue;
                chrome.Relabel(script.Label);
                foreach (var probe in chrome.Probes)
                {
                    double delta = probe.MarkBoxCentre - probe.BandCentre;
                    if (Math.Abs(delta) > InkAlignment.ArrangedTolerance)
                        report($"{family} · {script.Culture} · {probe.Label}: the mark's box centre sits " +
                               $"{delta:+0.000;-0.000} px from the '{probe.Band}' band's centre — the " +
                               "layout centres boxes, not ink.");
                }
                chrome.RestoreLabels();
            }
        });
    }

    /// <summary>
    /// The invariant the fixed-band mechanism exists for, carried over from the cells: NOTHING may
    /// move when the label's own text changes. The freshness caption is the chrome site that
    /// actually re-writes itself — it re-renders on every poll — so a band read off the LIVE text
    /// would twitch the warning triangle once a second.
    ///
    /// BOTH ELEMENTS ARE TRACKED, not just the mark. Which one moves depends on which is taller:
    /// the panel is as tall as its tallest child, so while the band is shorter than the 12 px
    /// triangle a live-ink band pins the mark and slides the LABEL instead — the same defect from
    /// the other side.
    /// </summary>
    [AvaloniaFact]
    public void Nothing_moves_when_the_freshness_caption_changes()
    {
        AssertAcrossFamilies((chrome, family, report) =>
        {
            var seen = chrome.Probes.ToDictionary(p => p, _ => new List<(string, double, double)>());
            foreach (var caption in Captions)
            {
                chrome.Relabel(_ => caption);
                foreach (var probe in chrome.Probes)
                    seen[probe].Add((caption, probe.MarkBoxCentre, probe.BandCentre));
            }
            chrome.RestoreLabels();

            foreach (var (probe, samples) in seen)
            {
                void Spread(string what, Func<(string Caption, double Mark, double Band), double> pick)
                {
                    double spread = samples.Max(pick) - samples.Min(pick);
                    if (spread > InkAlignment.ArrangedTolerance)
                        report($"{family} · {probe.Label}: the {what} moves {spread:F3} px across captions " +
                               $"({string.Join(", ", samples.Select(s => $"{s.Item1}={pick(s):F3}"))}) — " +
                               "nothing here may depend on the label's own ink.");
                }

                Spread("mark", s => s.Item2);
                Spread("band", s => s.Item3);
            }
        });
    }

    /// <summary>
    /// Issue #204's own invariant, and the one the #185 remedy would have broken: the text columns
    /// of ONE ROW sit on ONE BAND. Their band boxes must coincide — same top, same bottom, in window
    /// space — so they share a baseline whatever the face.
    ///
    /// THIS IS NOT RED PRE-FIX, and that is the point of writing it down. The overflow row's two
    /// columns share a baseline today for the accidental reason that they share a LINE BOX: same
    /// face, same size, both centred, so both are wrong by the same amount and the eye sees a level
    /// row. That accident is exactly what the obvious fix destroys. Collapsing only the project name
    /// onto its cap band would seat the checkbox correctly and leave the RAC column centring its
    /// line box — one mark aligned, two columns of text at different heights. Giving each column its
    /// OWN repertoire's band would do it again in the other direction: measured in Georgia, whose
    /// old-style figures descend below the baseline and fall short of its capitals, a digit-banded
    /// RAC sits 1.138 px off a cap-banded name.
    ///
    /// So this is a guard, deliberately, and it is falsifiable rather than decorative: swap
    /// <c>wordAligned</c> for <c>digitAligned</c> on the RAC column in StatisticsView.axaml and it
    /// goes red in Georgia.
    /// </summary>
    [AvaloniaFact]
    public void Text_columns_of_a_row_share_one_band()
    {
        AssertAcrossFamilies((chrome, family, report) =>
        {
            foreach (var row in chrome.Rows)
            {
                string band = BandFor(row.Labels);
                var first = BandBox(row.Labels[0], band, chrome.Root);
                foreach (var column in row.Labels.Skip(1))
                {
                    var box = BandBox(column, band, chrome.Root);
                    if (Math.Abs(box.Top - first.Top) > InkAlignment.ArrangedTolerance
                        || Math.Abs(box.Bottom - first.Bottom) > InkAlignment.ArrangedTolerance)
                        report($"{family} · '{row.Labels[0].Text}' | '{column.Text}': the row's columns " +
                               $"do not share the '{band}' band — {first.Top:F3}..{first.Bottom:F3} " +
                               $"against {box.Top:F3}..{box.Bottom:F3}, " +
                               $"a {(box.Top + box.Bottom) / 2 - (first.Top + first.Bottom) / 2:+0.000;-0.000} px " +
                               "baseline split. One row, one band.");
                }
            }
        });
    }

    /// <summary>
    /// The rendered half: each mark's painted ink and its label's painted ink sit where the arranged
    /// band alignment says they should.
    ///
    /// EXPECTED VALUE, NOT ZERO — the sites align the mark to a FIXED band, not to the ink of the
    /// string on screen, so a caption with a descender paints its ink centre BELOW the band's by a
    /// per-font constant. That constant is computed from outlines and is what the two rendered inks
    /// are expected to differ by; demanding zero would be demanding the mark chase the descenders.
    ///
    /// The mark side needs no such correction: a PathIcon stretches its geometry uniformly into its
    /// box and centres the result, and a swatch fills its box, so the painted mark's ink centre IS
    /// its box centre.
    /// </summary>
    [AvaloniaFact]
    public void Rendered_mark_ink_sits_on_the_reference_band()
    {
        var worst = (Deviation: 0.0, Where: "nothing measured");
        AssertAcrossFamilies((chrome, family, report) =>
        {
            foreach (double scaling in Scalings)
            {
                chrome.Rescale(scaling);
                foreach (var (probe, ink) in chrome.MeasureRenderedInk())
                {
                    double expected = probe.ShownTextOffsetFromBand;
                    double delta = ink.MarkCentre - ink.TextCentre;
                    double cap = InkAlignment.MaxInkDeviationDip(scaling);
                    double deviation = Math.Abs(delta - expected);
                    if (deviation > worst.Deviation)
                        worst = (deviation, $"{family} @{scaling}x · {probe.Label}");
                    if (deviation > cap)
                        report($"{family} @{scaling}x · {probe.Label}: the mark's ink centre sits " +
                               $"{delta:+0.000;-0.000} px from the label's (expected " +
                               $"{expected:+0.000;-0.000} px, cap ±{cap}). " +
                               $"mark={ink.MarkTop:F3}..{ink.MarkBottom:F3}, " +
                               $"text={ink.TextTop:F3}..{ink.TextBottom:F3}.");
                }
            }
            chrome.Rescale(1.0);
        });

        // How much headroom this runner actually left under the cap. Reported rather than asserted:
        // the number is the RASTERIZER's hinting, which differs per platform, and tightening a
        // tolerance to fit one runner's observations is how a gate becomes a flake.
        output.WriteLine($"worst rendered deviation: {worst.Deviation:F3} px ({worst.Where})");
    }

    /// <summary>
    /// Runs <paramref name="probe"/> over every family this runner actually has, inside ONE window.
    ///
    /// ONE WINDOW, NOT ONE PER CASE, for the reason the cell gate records: a window per theory case
    /// was measured to destabilise the neighbouring pixel gate in this assembly. Failures are
    /// collected rather than thrown one at a time, so a regression reports EVERY family and site it
    /// broke, not just the first the runner happened to reach.
    /// </summary>
    private void AssertAcrossFamilies(Action<Chrome, string, Action<string>> probe)
    {
        var failures = new List<string>();
        var probed = new List<string>();

        using var chrome = Chrome.Open(ThemeVariant.Dark);

        // A theme's first render populates Skia's glyph/render caches and differs from later ones
        // (VisualWarmup's finding); this gate shares its process with the baseline captures.
        chrome.Capture().Dispose();

        // The construction is what is under test, so a shrunken probe set is a broken harness, not
        // a clean bill of health — a binding change that hid one of these marks would otherwise
        // retire its site from the gate silently.
        var found = chrome.Probes.Select(p => p.Label).ToList();
        Assert.True(found.SequenceEqual(Chrome.ExpectedSites),
            "the probed site list drifted — a site was added, renamed, or (the dangerous one) " +
            "stopped being discovered because something hid its mark:" + Environment.NewLine +
            $"  expected: {string.Join(", ", Chrome.ExpectedSites)}" + Environment.NewLine +
            $"  found:    {string.Join(", ", found)}");

        // Same protection for the row sweep: with no multi-column row discovered the shared-band
        // invariant (#204) would report a serene green over an empty set.
        Assert.True(chrome.Rows.Count > 0,
            "no multi-column row was discovered — the shared-band invariant would pass vacuously.");

        foreach (var family in InkAlignment.FamilyNames)
        {
            if (InkAlignment.Resolve(family) is not { } resolved)
                continue;
            probed.Add(family);
            chrome.UseFont(resolved);
            probe(chrome, family, failures.Add);
        }

        // Without this a runner whose font manager resolved nothing would report a serene green over
        // an empty sweep. Inter ships embedded with the harness, so its absence means the probe
        // itself is broken, not the runner.
        Assert.Contains("Inter", probed);

        // Coverage this runner could not give is ANNOUNCED, never silently dropped.
        output.WriteLine($"probed {probed.Count}/{InkAlignment.FamilyNames.Length} families: {string.Join(", ", probed)}");
        output.WriteLine(chrome.SkippedScripts.Count == 0
            ? $"probed all {Scripts.Length} UI scripts"
            : $"SKIPPED scripts (runner cannot draw their band): {string.Join(", ", chrome.SkippedScripts)}");

        Assert.True(failures.Count == 0,
            $"probed {probed.Count} of {InkAlignment.FamilyNames.Length} families ({string.Join(", ", probed)}) " +
            $"over {chrome.Probes.Count} sites:" +
            Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    private readonly record struct RenderedInk(
        double MarkTop, double MarkBottom, double MarkCentre,
        double TextTop, double TextBottom, double TextCentre);

    /// <summary>
    /// A container laying marks and labels out on ONE line: the panel or grid row, every glyph-sized
    /// mark it paints (with the child that owns each, for naming), and every label it lays out.
    /// The unit of discovery, because the band question is the CONTAINER's, not any one pair's —
    /// see <see cref="BandFor"/>.
    /// </summary>
    private readonly record struct Group(
        Control Container,
        IReadOnlyList<(Control Owner, Control Mark)> Marks,
        IReadOnlyList<TextBlock> Labels);

    /// <summary>
    /// The band a container's text is read against — ONE band for the whole container, which is
    /// issue #204's invariant and the reason it is asked here rather than of a single label.
    ///
    /// A row carrying words and figures side by side (the Statistics overflow row: a project name
    /// and its RAC) has ONE baseline, so it has one band, and it is the WORD band whenever any
    /// column carries words. Giving each column its own repertoire's band is what splits that
    /// baseline: measured in Georgia, whose old-style figures descend below the baseline and fall
    /// short of its capitals, a digit-banded RAC column sits 1.138 px off the cap-banded name
    /// beside it — a mark misalignment traded for two columns of text at different heights.
    ///
    /// For a container with ONE label this is exactly the per-label rule the panel sweep has always
    /// used: a label carrying no letter is figures and belongs on the digit band.
    ///
    /// DELIBERATELY NOT READ FROM THE CLASS the fix applies. An expectation taken from the class
    /// would say "this site is aligned to the band it claims", which is true however wrongly the
    /// class was chosen. Deriving it from the rendered repertoire keeps that choice under test.
    /// </summary>
    private static string BandFor(IEnumerable<TextBlock> labels) =>
        labels.Any(label => (label.Text ?? "").Any(char.IsLetter))
            ? TextInkCollapseConverter.WordBandFor(Strings.TaskStateRunning)
            : TextInkCollapseConverter.DigitBand;

    /// <summary>Window-space ink box of <paramref name="band"/> as <paramref name="text"/> would
    /// draw it, at that label's own typeface and size.</summary>
    private static (double Top, double Bottom) BandBox(TextBlock text, string band, Window window)
    {
        var ink = InkAlignment.InkOf(text, band);
        double top = text.TranslatePoint(new Point(0, 0), window)!.Value.Y;
        return (top + ink.Top, top + ink.Bottom);
    }

    /// <summary>One mark-and-label pairing: the mark, the label, and the surface they are painted
    /// on (the command bar, the pill, the chip, the flyout row) — which is the region the pixel
    /// sweep scans and takes its background colour from.</summary>
    private sealed class Probe(
        string label, Control mark, TextBlock text, IReadOnlyList<TextBlock> columns,
        Control container, Visual backdrop, Window window)
    {
        public string Label { get; } = label;
        public Control Mark { get; } = mark;
        public TextBlock Text { get; } = text;

        /// <summary>Every label the container lays out — this one included. The band is asked of
        /// the whole set (<see cref="BandFor"/>), not of <see cref="Text"/> alone.</summary>
        public IReadOnlyList<TextBlock> Columns { get; } = columns;

        /// <summary>The panel or grid row holding the pair — the basis of the pixel sweep's scan
        /// band.</summary>
        public Control Container { get; } = container;

        public Visual Backdrop { get; } = backdrop;

        /// <summary>The label the markup or binding put there, restored after a caption sweep.</summary>
        public string BoundText { get; } = text.Text ?? "";

        private double Top(Visual visual) => visual.TranslatePoint(new Point(0, 0), window)!.Value.Y;

        public double MarkBoxCentre => Top(Mark) + Mark.Bounds.Height / 2;

        /// <summary>The band this site OUGHT to align to — the CONTAINER's, see
        /// <see cref="ChromeAlignmentTests.BandFor"/>.</summary>
        public string Band => BandFor(Columns);

        /// <summary>Window-space centre of that band's ink, as arranged.</summary>
        public double BandCentre
        {
            get
            {
                var (top, bottom) = BandBox(Text, Band, window);
                return (top + bottom) / 2;
            }
        }

        /// <summary>How far the text actually on screen paints its ink centre from the band the site
        /// aligns to — zero only when the shown text's ink is exactly the band.</summary>
        public double ShownTextOffsetFromBand
        {
            get
            {
                var band = InkAlignment.InkOf(Text, Band);
                var shown = InkAlignment.InkOf(Text, Text.Text ?? "");
                return (band.Top + band.Bottom) / 2 - (shown.Top + shown.Bottom) / 2;
            }
        }
    }

    /// <summary>
    /// The five views that carry chrome pairs, each in the state that makes its pair visible, all in
    /// one window. View models are driven directly (the TasksViewTests idiom): what is under test is
    /// the markup's geometry, and the store to row projection has its own suites.
    /// </summary>
    private sealed class Chrome : IDisposable
    {
        /// <summary>Every site this gate expects to find, by probe label. Written out rather than
        /// counted so a diff says WHICH site left the sweep.</summary>
        public static readonly string[] ExpectedSites =
        [
            "EventLogView[IconCheckmarkFilled+Error]",
            "EventLogView[IconCheckmarkFilled+Info]",
            "EventLogView[IconCheckmarkFilled+Warning]",
            "ProjectsView[IconWarningFilled+Updated 4 m ago]",
            // The overflow flyout's rows (#204) — a Grid, not a StackPanel, and the construction the
            // discovery sweep was extended to see. Each row pairs its checkbox with BOTH columns.
            "StatisticsView[CheckBox+1,234]",
            "StatisticsView[CheckBox+987]",
            "StatisticsView[CheckBox+Rosetta@home]",
            "StatisticsView[CheckBox+World Community Grid]",
            "StatisticsView[Panel+Einstein@Home]",
            "StatisticsView[Panel+LHC@home]",
            "TasksView[IconChevronDownRegular+Computing]",
            "TasksView[IconPlaySettingsRegular+Computing]",
            // The status bar's warning block — #185's fifth site, and the one that proves an
            // app-level style class reaches into a ControlTemplate's children.
            "TasksView[IconWarningFilled+3 deadlines at risk]",
            "TasksView[IconWarningFilled+Updated 4 m ago]",
            "TransfersView[IconWarningFilled+Updated 4 m ago]",
        ];

        /// <summary>
        /// DIPs of scan band added above and below the holding container. After the fix the label's
        /// box IS its reference band, so its descenders paint outside the container; six DIPs clears
        /// them at every size these sites use (12-14 px text) without reaching a surface edge.
        /// Clamped to the backdrop regardless, so it can never over-reach.
        ///
        /// A STACKED container's NEIGHBOUR is the other thing this could over-reach into, and the
        /// overflow flyout is the first site with one (#204): its rows are 3 DIPs of margin apart,
        /// so a six-DIP inflation reaches exactly the next row's box edge — but a row's own ink
        /// starts ~11 DIPs inside that box, and the scan only looks at the probed element's OWN
        /// columns. Measured on the fixed tree, the widest extent read for a flyout row was
        /// 9.502..23.000 against a 38 DIP pitch: no neighbour is in reach.
        /// </summary>
        private const double Slack = 6;

        private const string StaleCaption = "Updated 4 m ago";

        /// <summary>What the Tasks status bar's warning channel shows: an at-risk deadline count.</summary>
        private const string StatusBarWarning = "3 deadlines at risk";

        private readonly Window _window;
        private readonly HostStore _store;
        private readonly HostMonitorManager _manager;
        private readonly string[] _tempFiles;
        private readonly CultureInfo _entryUiCulture = CultureInfo.CurrentUICulture;
        private readonly List<string> _skippedScripts = [];
        private FontFamily _family = FontFamily.Default;
        private double _scaling = 1.0;

        private Chrome(Window window, HostStore store, HostMonitorManager manager, string[] tempFiles,
            IReadOnlyList<Probe> probes, IReadOnlyList<Group> rows)
        {
            _window = window;
            _store = store;
            _manager = manager;
            _tempFiles = tempFiles;
            Probes = probes;
            Rows = rows;
        }

        public IReadOnlyList<Probe> Probes { get; }

        /// <summary>Discovered containers laying out MORE THAN ONE label — the multi-column rows
        /// whose shared baseline is issue #204's invariant.</summary>
        public IReadOnlyList<Group> Rows { get; }

        /// <summary>The window every probed element is measured against.</summary>
        public Window Root => _window;

        /// <summary>Scripts this runner could not draw, so the caller can announce the gap.</summary>
        public IReadOnlyList<string> SkippedScripts => _skippedScripts;

        public static Chrome Open(ThemeVariant variant)
        {
            Application.Current!.RequestedThemeVariant = variant;

            string Temp(string tag) => Path.Combine(Path.GetTempPath(), $"lattice-chrome-{Guid.NewGuid():N}-{tag}.json");
            string hostsPath = Temp("hosts"), uiPath = Temp("ui");

            // One configured host: the Computing button is scoped-host-only chrome, so without an
            // entry to scope to there is nothing to probe.
            var host = TestData.MakeHostConfig(name: "mini-01");
            var registry = new HostRegistry(new LatticeConfig(5, [host]), hostsPath);
            // Never started, so no poll ever rebuilds over the state set below.
            var manager = new HostMonitorManager(registry, () => new FakeGuiRpcClient(), TimeProvider.System);
            var store = new HostStore(registry, manager, new InlineUiDispatcher());
            var control = new HostControlService(registry, manager, () => new FakeGuiRpcClient());
            var uiState = new UiStateStore(uiPath);
            var density = new DensityPreference(uiState);
            var clock = new InertUiClock();

            var tasks = new TasksViewModel(store, clock, uiState, density, control);
            var projects = new ProjectsViewModel(store, clock, control, NoopAttachRun, new InlineUiDispatcher());
            var transfers = new TransfersViewModel(store, clock, density);
            var eventLog = new EventLogViewModel(store);
            var statistics = new StatisticsViewModel(store, clock);

            var stack = new Grid { RowDefinitions = new RowDefinitions("*,*,*,*,*") };
            Control[] views =
            [
                new TasksView { DataContext = tasks },
                new ProjectsView { DataContext = projects },
                new TransfersView { DataContext = transfers },
                new EventLogView { DataContext = eventLog },
                new StatisticsView { DataContext = statistics },
            ];
            for (int i = 0; i < views.Length; i++)
            {
                Grid.SetRow(views[i], i);
                stack.Children.Add(views[i]);
            }

            // 1280 is ShellWindow's default width — wide enough that no responsive breakpoint sheds
            // a probed control. The height gives each of the five stacked views its command bar (and
            // Statistics its legend row) at the natural size the shell would give it.
            var window = new Window { Width = 1280, Height = 900, Content = stack };
            // Production sets this at the composition root; a hosted data view reaches the
            // UiStateStore for column-width persistence (#120) through this inherited property.
            ColumnWidthScope.SetStore(window, uiState);
            window.Show();
            window.SetRenderScaling(1.0);
            Layout(window);

            // AFTER the first layout, not before it. Statistics rebuilds its chrome off the store
            // when the page is realized, which wipes anything written to the view model at
            // construction time (measured: the legend row was still collapsed and its chip list
            // empty). Driving the state once the views are up is the write that survives.
            //
            // The freshness pair renders its warning triangle only while the data IS stale, and the
            // caption is what a poll would have written.
            tasks.IsUpdateStale = projects.IsUpdateStale = transfers.IsUpdateStale = true;
            tasks.UpdatedText = projects.UpdatedText = transfers.UpdatedText = StaleCaption;

            // A scoped host is what shows the Computing button.
            tasks.ScopedHost = new HostRailItemViewModel(store.Hosts[0], clock, control);

            // The status bar's warning block renders ONLY while its text is non-empty, and an
            // invisible pair has no arranged box to centre anything on — so with the views in their
            // ordinary state this site would silently sit outside the sweep. It is #185's fifth
            // site, approved outside that issue's own list. The block lives in the shared
            // StatusBarControl TEMPLATE, so every view carries it structurally, but Tasks is the
            // only view that binds WarningText today (its at-risk-deadline count) — so Tasks is
            // where the gate can see it, and a second view growing a warning inherits the fix.
            tasks.AtRiskText = StatusBarWarning;

            // A chart with two series: the legend row appears and carries two chips.
            statistics.HasChart = true;
            statistics.Chips.Add(new StatisticsLegendChip("https://einstein.phys.uwm.edu/",
                new StatisticsChipData("Einstein@Home", 0, 0)) { IsVisible = true });
            statistics.Chips.Add(new StatisticsLegendChip("https://lhcathome.cern.ch/",
                new StatisticsChipData("LHC@home", 1, 1)) { IsVisible = true });

            // Projects past the six-chip cap, which is what puts the "+N more" button on the legend
            // row and rows in its flyout (#204's site). Two rows, and their text is chosen for ink
            // shape rather than prose: one name without a descender and one with, so a band read off
            // the live text could not survive the caption sweep.
            statistics.HasOverflow = true;
            statistics.OverflowLabel = "+2 more";
            statistics.Overflow.Add(new StatisticsOverflowItem(
                "https://boinc.bakerlab.org/rosetta/", new StatisticsOverflowData("Rosetta@home", "1,234", true)));
            statistics.Overflow.Add(new StatisticsOverflowItem(
                "https://www.worldcommunitygrid.org/", new StatisticsOverflowData("World Community Grid", "987", true)));
            Layout(window);

            // The overflow rows live in a FLYOUT, so nothing is realized — and nothing can be
            // probed — until it is open. Headless popups render into the host window's own frame
            // (MenuSeparatorVisualTests' finding), so the same capture that sweeps the five views
            // sweeps these rows too. Settle, not a bare pump: the flyout opens with an entrance
            // transition off the REAL clock.
            var overflow = window.GetVisualDescendants().OfType<DropDownButton>()
                .Single(button => button.IsEffectivelyVisible);
            overflow.Flyout!.ShowAt(overflow);
            Settle(window);

            return new Chrome(window, store, manager, [hostsPath, uiPath], Discover(window),
                Groups(window).Where(group => group.Labels.Count > 1).ToList());
        }

        /// <summary>
        /// Every realized container built as "glyph-sized marks and labels, laid out on one line
        /// and centred" — the construction under test, found structurally rather than by the class
        /// the fix applies, so the gate covers the defect class and not just today's sites. Grid
        /// CELLS are excluded: they are the same defect but they have their own fixture and their
        /// own gate (<see cref="StatusCellAlignmentTests"/>), which drives row state this one does
        /// not.
        /// </summary>
        private static IReadOnlyList<Probe> Discover(Window window)
        {
            var probes = new List<Probe>();
            var icons = IconNames();
            foreach (var group in Groups(window))
            {
                var backdrop = Backdrop(group.Container);
                foreach (var (owner, mark) in group.Marks)
                    foreach (var label in group.Labels)
                        probes.Add(new Probe(
                            $"{View(group.Container)}[{Name(owner, icons)}+{label.Text}]",
                            mark, label, group.Labels, group.Container, backdrop, window));
            }
            return probes.OrderBy(p => p.Label, StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// The view a container belongs to, for the site name. The VISUAL tree answers it for
        /// everything laid out inside a view — and cannot answer it for a flyout: a popup is hosted
        /// in the window's own overlay layer, which is a sibling of the view content, so the row's
        /// visual ancestors run straight past every UserControl to the window. Its LOGICAL parent
        /// chain still runs back through the flyout to the button that owns it, which is where the
        /// view is (#204).
        /// </summary>
        private static string View(Control container) =>
            (container.GetVisualAncestors().OfType<UserControl>().FirstOrDefault()
             ?? container.GetLogicalAncestors().OfType<UserControl>().FirstOrDefault())
            ?.GetType().Name
            ?? throw new InvalidOperationException(
                $"a discovered {container.GetType().Name} belongs to no view — neither its visual nor " +
                "its logical ancestors reach a UserControl, so the site cannot be named.");

        /// <summary>Every discovered container, panels and grid rows alike, in one sequence.</summary>
        internal static IEnumerable<Group> Groups(Window window) => PairPanels(window).Concat(PairRows(window));

        /// <summary>
        /// The horizontal-StackPanel form of the construction: a bar, a pill, a chip.
        /// </summary>
        private static IEnumerable<Group> PairPanels(Window window)
        {
            foreach (var panel in window.GetVisualDescendants().OfType<StackPanel>())
            {
                if (panel.Orientation != Orientation.Horizontal) continue;
                if (!Eligible(panel)) continue;
                if (Partition(panel, panel.Children.Where(child => child.IsVisible).ToList()) is { } group)
                    yield return group;
            }
        }

        /// <summary>
        /// The GRID form (issue #204): the Statistics overflow flyout lays its checkbox, project
        /// name and RAC figure out as three columns of one grid row, so the StackPanel sweep above
        /// never saw it and the fix would have shipped ungated.
        ///
        /// THE PREDICATE, and why each clause is in it. "A Grid row" on its own is a far broader net
        /// than "a panel of marks and labels" — Grid is this app's general layout container, and an
        /// unqualified sweep would drag in every view's page skeleton, every control template's
        /// root, and the two grids inside each CheckBox's own template. What is wanted is a ROW OF
        /// COLUMNS holding a mark beside text, so the net is exactly that:
        ///
        /// <list type="bullet">
        /// <item>ONE ROW (<c>RowDefinitions</c> empty or single, every child in row 0, no row span)
        /// — a multi-row grid is a page skeleton, and "what baseline does this row share" is not a
        /// question one can ask of it;</item>
        /// <item>TWO OR MORE COLUMNS, each child in its OWN column with no column span — this is
        /// what makes it a row of columns rather than a stack of overlaid children, and it is what
        /// excludes the CheckBox template's inner grids (which overlay their box and check glyph in
        /// column 0);</item>
        /// <item>marks and labels and NOTHING ELSE, exactly as the panel sweep requires — a row
        /// holding a button or a combo box is a form, not this construction.</item>
        /// </list>
        ///
        /// The net is deliberately not widened to grids whose children merely happen to sit side by
        /// side without column definitions: that shape is a stack, and its own sweep already covers
        /// it.
        /// </summary>
        private static IEnumerable<Group> PairRows(Window window)
        {
            foreach (var grid in window.GetVisualDescendants().OfType<Grid>())
            {
                if (!Eligible(grid)) continue;
                if (grid.RowDefinitions.Count > 1 || grid.ColumnDefinitions.Count < 2) continue;

                var visible = grid.Children.Where(child => child.IsVisible).ToList();
                if (visible.Any(child =>
                        Grid.GetRow(child) != 0 || Grid.GetRowSpan(child) != 1 || Grid.GetColumnSpan(child) != 1))
                    continue;
                if (visible.Select(Grid.GetColumn).Distinct().Count() != visible.Count) continue;

                if (Partition(grid, visible) is { } group)
                    yield return group;
            }
        }

        /// <summary>Realized, on screen, and outside the data grids — which have their own gate.</summary>
        private static bool Eligible(Control container) =>
            container.IsEffectivelyVisible && !container.GetVisualAncestors().OfType<DataGridRow>().Any();

        /// <summary>
        /// Splits a container's visible children into marks and labels, or rejects the container.
        /// Marks and labels and NOTHING else: a bar holding buttons and combo boxes is a toolbar,
        /// not this construction, and its children carry boxes of their own.
        /// </summary>
        private static Group? Partition(Control container, IReadOnlyList<Control> visible)
        {
            var marks = new List<(Control Owner, Control Mark)>();
            var labels = new List<TextBlock>();
            foreach (var child in visible)
            {
                if (child is TextBlock label) labels.Add(label);
                else if (InkAlignment.MarkOf(child) is { } mark) marks.Add((child, mark));
                else return null;
            }
            return marks.Count > 0 && labels.Count > 0 ? new Group(container, marks, labels) : null;
        }

        /// <summary>A mark's name for the failure message: the icon resource it draws, or the type
        /// of the child that owns it — the composed box for the legend swatch, the control for a
        /// mark a template paints (the overflow row's CheckBox).</summary>
        private static string Name(Control owner, Dictionary<Geometry, string> icons) =>
            owner is PathIcon icon && icons.TryGetValue(icon.Data!, out var key) ? key : owner.GetType().Name;

        /// <summary>
        /// Reverse index of the shared icon dictionary, so a failure names the glyph the designer
        /// would name rather than a geometry's identity. Icons.axaml is MERGED into the
        /// application's resources and a ResourceDictionary enumerates only its own entries, so the
        /// merged ones have to be walked.
        ///
        /// Built per fixture and NEVER cached in a static: <c>HeadlessUnitTestSession</c> isolates
        /// PerTest, which stands up a fresh <see cref="Application"/> — and therefore a fresh set of
        /// Geometry instances — for every <c>[AvaloniaFact]</c>. A static cache keyed by geometry
        /// identity is populated by whichever test ran first and misses every one after it
        /// (measured: the site list named "PathIcon" in the second test of the same run).
        /// </summary>
        private static Dictionary<Geometry, string> IconNames()
        {
            var names = new Dictionary<Geometry, string>();
            Walk(Application.Current!.Resources);
            return names;

            void Walk(IResourceDictionary dictionary)
            {
                foreach (var (key, value) in dictionary)
                    if (key is string name && value is Geometry geometry)
                        names[geometry] = name;
                foreach (var merged in dictionary.MergedDictionaries.OfType<IResourceDictionary>())
                    Walk(merged);
            }
        }

        /// <summary>
        /// The surface a pair is painted on: the NEAREST ancestor that fills a box of its own — the
        /// command bar Border, the pill's ToggleButton, the chip's Border, the status bar. It bounds
        /// the pixel sweep and supplies its background colour, so a probe inside a tinted pill
        /// measures against the pill's fill and not the bar's.
        ///
        /// No "tall enough to hold the pair" condition, deliberately. An earlier version required
        /// the ancestor to be at least six DIPs taller than the panel, on the theory that the scan
        /// band had to have slack for descenders. That is false whenever the panel already FILLS its
        /// surface — the status bar's warning block is docked, so it stretches to the full 27 px
        /// strip, failed the test against its own 28 px status bar, and the search ran on to the
        /// 180 px view, whose fill and whose ink are somebody else's. The slack belongs to the scan
        /// band (see <see cref="MeasureRenderedInk"/>), which inflates around the panel and CLAMPS
        /// to this surface, not to the choice of surface.
        /// </summary>
        private static Visual Backdrop(Control container) =>
            container.GetVisualAncestors().FirstOrDefault(v => v is Border or TemplatedControl) ?? container;

        /// <summary>Re-renders every probed label in <paramref name="family"/>. Set on the text
        /// elements, so the margin binding sees the same change an inherited font change would
        /// produce at runtime; the marks are vector paths and solid fills and do not depend on the
        /// font.</summary>
        public void UseFont(FontFamily family)
        {
            _family = family;
            foreach (var probe in Probes)
                TextElement.SetFontFamily(probe.Text, family);
            Layout(_window);
        }

        /// <summary>Re-renders the same window at another scale, so the pixel sweep covers 1x and 2x
        /// without opening a second one.</summary>
        public void Rescale(double scaling)
        {
            if (_scaling == scaling) return;
            _scaling = scaling;
            _window.SetRenderScaling(scaling);
            Layout(_window);
        }

        /// <summary>Rewrites every probed label through <paramref name="rewrite"/>, which receives
        /// the string the markup or binding put there. Production changes these through bindings and
        /// resource lookups; for layout the two are the same write to the same property.</summary>
        public void Relabel(Func<string, string> rewrite)
        {
            foreach (var probe in Probes)
                probe.Text.Text = rewrite(probe.BoundText);
            Layout(_window);
        }

        public void RestoreLabels() => Relabel(bound => bound);

        /// <summary>
        /// Switches the UI culture the converter reads its band from, and re-fires the margin
        /// bindings — a culture change raises no property notification of its own, and re-applying
        /// an UNCHANGED font raises none either. In production nothing needs this: the UI culture is
        /// fixed before the first control is realized.
        ///
        /// Returns false when this runner cannot draw the culture's band at all: a bare CI runner
        /// with no CJK face resolves no outlines for the ideograph, the converter correctly declines
        /// to collapse onto ink that does not exist, and asserting a band nothing can render would
        /// be asserting the fallback rather than the fix.
        /// </summary>
        public bool UseUiCulture(string culture)
        {
            var previous = CultureInfo.CurrentUICulture;
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);

            // Asked AFTER the switch and through the resource set, exactly as production does — for
            // a culture with no satellite (ja-JP) that is the neutral English string, and the band
            // is the Latin one however CJK the culture reads.
            var band = TextInkCollapseConverter.WordBandFor(Strings.TaskStateRunning);
            if (!FontManager.Current.TryMatchCharacter(
                    char.ConvertToUtf32(band, 0), FontStyle.Normal, FontWeight.Normal, FontStretch.Normal,
                    _family, null, out _))
            {
                CultureInfo.CurrentUICulture = previous;
                _skippedScripts.Add($"{culture} in {_family.Name}");
                return false;
            }

            // Two notifications that land back where they started, so the MultiBinding re-evaluates
            // under the new culture. Italic is guaranteed to differ from these labels' Normal.
            foreach (var probe in Probes)
            {
                TextElement.SetFontStyle(probe.Text, FontStyle.Italic);
                TextElement.SetFontStyle(probe.Text, FontStyle.Normal);
            }
            Layout(_window);
            return true;
        }

        public Avalonia.Media.Imaging.Bitmap Capture() =>
            _window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No rendered frame captured.");

        /// <summary>
        /// Measures each probed pair's painted ink, in DIPs, so the 1x and 2x runs are directly
        /// comparable. Each element is scanned over its OWN columns across the backdrop's full
        /// interior height: the label's box is the band after the fix, so its descenders paint
        /// outside that box and a box-clipped scan would measure the band instead of the ink.
        /// </summary>
        public IEnumerable<(Probe Probe, RenderedInk Ink)> MeasureRenderedInk()
        {
            using var frame = Capture();
            var pixels = PixelBuffer.From(frame);

            Rect Device(Visual visual)
            {
                var origin = visual.TranslatePoint(new Point(0, 0), _window)!.Value;
                return new Rect(origin.X * _scaling, origin.Y * _scaling,
                    visual.Bounds.Width * _scaling, visual.Bounds.Height * _scaling);
            }

            var results = new List<(Probe, RenderedInk)>();
            foreach (var probe in Probes)
            {
                var surface = Device(probe.Backdrop);
                var band = Device(probe.Container);

                // The scan band is the PANEL, inflated for the ink that paints outside it — after
                // the fix the label's box IS its band, so descenders fall below the box and a
                // box-clipped scan would measure the band instead of the ink — then CLAMPED to the
                // surface, inset two DIPs. The inset matters because a backdrop paints its own
                // border and corner arcs inside its bounds, and those clear any ink floor in every
                // column they touch.
                int y0 = (int)Math.Ceiling(Math.Max(band.Top - Slack * _scaling, surface.Top + 2 * _scaling));
                int y1 = (int)Math.Floor(Math.Min(band.Bottom + Slack * _scaling, surface.Bottom - 2 * _scaling)) - 1;

                // The surface's own fill, taken as the modal colour over the region being scanned.
                var background = InkAlignment.Modal(
                    pixels, (int)Math.Ceiling(surface.Left + 2 * _scaling),
                    (int)Math.Floor(surface.Right - 2 * _scaling) - 1, y0, y1);
                var mark = InkAlignment.Extent(pixels, background, Device(probe.Mark), y0, y1, $"{probe.Label} mark");
                var text = InkAlignment.Extent(pixels, background, Device(probe.Text), y0, y1, $"{probe.Label} label");

                results.Add((probe, new RenderedInk(
                    (mark.Top - band.Top) / _scaling, (mark.Bottom - band.Top) / _scaling,
                    (mark.Centre - band.Top) / _scaling,
                    (text.Top - band.Top) / _scaling, (text.Bottom - band.Top) / _scaling,
                    (text.Centre - band.Top) / _scaling)));
            }
            return results;
        }

        private static Task<AttachFlowResult> NoopAttachRun(
            Guid hostId, AttachMachine.AttachRequest request,
            IProgress<AttachMachine.Stage>? progress, CancellationToken ct) =>
            Task.FromResult(new AttachFlowResult(AttachFlowOutcome.Attached, [], null));

        public void Dispose()
        {
            // ModuleInit pins en-US so the zh-CN satellite cannot bleed Han glyphs into a committed
            // baseline (#147). The suite runs serially in one process, so putting it back is not
            // optional.
            CultureInfo.CurrentUICulture = _entryUiCulture;
            _window.Close();
            _store.Dispose();
            _manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
            foreach (var path in _tempFiles)
                File.Delete(path);
        }
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
