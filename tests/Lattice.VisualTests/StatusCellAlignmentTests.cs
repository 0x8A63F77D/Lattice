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
using Lattice.App.Aggregation;
using Lattice.App.Infrastructure;
using Lattice.App.Localization;
using Lattice.App.ViewModels;
using Lattice.App.Views;
using Lattice.Core;
using Lattice.Tests;
using Xunit;

namespace Lattice.VisualTests;

/// <summary>
/// Issue #180 — a data cell that pairs a fixed-size ICON with a text label must centre the icon on
/// the text's REFERENCE BAND: in every font, in every shipped UI script, and without anything in the
/// cell moving when the label changes.
///
/// THE DEFECT. <c>VerticalAlignment="Center"</c> centres each child's LAYOUT box independently, and
/// a TextBlock's box is a line box (ascent + descent + line gap). Glyph ink sits at a per-font
/// offset inside that box, so "centre both boxes" aligns in one font and misaligns in the next.
/// Measured here pre-fix, icon-box centre minus CAP-BAND centre at the status cells: +1.063 px in
/// Helvetica, +0.739 px in Inter, +0.626 px in .AppleSystemUIFont, +0.438 px in Courier New,
/// +0.024 px in Times New Roman. The defect is that whole SPREAD — over a pixel of drift with
/// nothing but the font changing — and the app renders in Helvetica on macOS (it pins no font;
/// Avalonia's Skia manager resolves the platform default there, established in #179), so the
/// shipping case is the worst one probed.
///
/// (#180 quotes larger numbers, −1.83 px in Inter and so on. Those measure the icon against the
/// centre of the LIVE WORD's ink, descenders included. That is the same layout seen through a
/// different reference — and not the one to align to, since it moves with the word; see below.)
///
/// WHY NOT THE SNOOZE PILL'S REMEDY. #176/#179 fixed the same defect class in the pill by
/// collapsing the text box onto the ink of a fixed DIGIT band, valid there because the pill shows
/// only digits. These cells show words that change: "Suspended" has a descender, "Running" has
/// none, so a band read off the live label would shift the pair every time a task changed state
/// (measured under that mutation: 1.24 px per status change). The band is a fixed reference glyph
/// instead. <see cref="Nothing_moves_when_the_status_word_changes"/> is the gate on that.
///
/// WHICH band is the cell's business, and both shipped answers are swept here:
/// <list type="bullet">
/// <item>a WORD cell (Tasks State, Projects/Transfers Status) aligns to the band its UI script is
/// read against — the cap band under en-US, the ideograph em band under the shipped zh-CN UI, whose
/// statuses are Han ("运行中", "活动中") and, measured in the shipping face, do not even share the
/// Latin line box: "H" reports a 12.000 px line height at 12 px where "运行中" falls back to a CJK
/// face and reports 16.800 px;</item>
/// <item>a FIGURES cell (Tasks Deadline, "MM-dd HH:mm" formatted with the invariant culture) aligns
/// to the digit band — a face with old-style figures draws them shorter than its capitals, so the
/// cap band would centre the icon on ink that cell never renders.</item>
/// </list>
///
/// WHAT IS PROBED, AND WHY IT IS FOUND STRUCTURALLY. <see cref="Cells.Probes"/> does not look for
/// the marker class the fix applies; it looks for the CONSTRUCTION — a horizontal StackPanel in a
/// realized grid cell whose children are an icon box followed by a TextBlock. So the sweep covers
/// the defect class rather than the four cells that were known to have it, and a fifth cell built
/// the same way later joins the gate by existing (it will fail until it is fixed too). Cells that
/// pair a TextBlock with a progress BAR are structurally excluded: a 3 px rule is not a glyph-sized
/// mark and where it should sit against text is a separate design question, not this invariant.
///
/// TWO LAYERS. The arranged gate is exact (no rasterizer in the loop, identical on every runner)
/// and is what is red pre-fix in EVERY family, Inter included. The rendered sweep then proves the
/// painted ink really lands where layout put it, which layout numbers cannot promise: the rasterizer
/// hints glyph edges onto the device pixel grid, so ink can sit a device pixel off the arranged
/// geometry in a font-dependent direction. That quantisation, not the layout, sets the pixel
/// tolerance (see <see cref="MaxInkDeviationDip"/>).
///
/// NOT env-gated: like <see cref="SnoozePillAlignmentTests"/> these assert geometry, not committed
/// screenshots, so they gate the fix in the normal <c>dotnet test</c> lane on every CI OS.
/// </summary>
[Trait("Category", "Visual")]
public class StatusCellAlignmentTests(ITestOutputHelper output)
{
    /// <summary>The arranged geometry is computed, not rasterised, so it is exact up to floating
    /// point; a thousandth of a pixel is slack for the arithmetic, not for the layout.</summary>
    private const double ArrangedTolerance = 0.001;

    /// <summary>
    /// TWO DEVICE PIXELS, in DIPs. Hinting grid-fits a glyph's top and bottom edges onto the device
    /// pixel grid independently and may shift the whole run as it does so, so each edge can move by
    /// up to a device pixel and the ink CENTRE this gate measures — their midpoint — inherits that
    /// same bound in each direction. The cap is the mechanism's, not a fit to the observations, and
    /// the observations sit inside it with room: post-fix the worst deviation is 0.852 px @1x /
    /// 0.311 px @2x on macOS and 1.194 px @1x on Windows, whose hinting is the more aggressive of
    /// the two (Times New Roman there renders a cap band a full pixel taller than its outline).
    /// A one-device-pixel cap was tried first and was red on the Windows runner alone.
    ///
    /// This layer is the weaker of the two by construction, and deliberately so: it proves the
    /// painted pixels follow the layout, and is not the detector. The EXACT gate is
    /// <see cref="Icon_is_centred_on_the_reference_band"/>, which is red pre-fix in every family on
    /// every runner; this one is red pre-fix only where the arranged error clears two device pixels.
    /// </summary>
    private static double MaxInkDeviationDip(double scaling) => 2.0 / scaling;

    /// <summary>Families worth probing: metrically different from each other and from Inter. Ones
    /// this runner lacks are skipped (see <see cref="Resolve"/>); Inter is embedded, so at least it
    /// is always measured — and the arranged assertions are red pre-fix in Inter too, so the gate
    /// does not go vacuous on a runner with a bare font stack.</summary>
    private static readonly string[] FamilyNames =
    [
        "Inter", "Helvetica", "Helvetica Neue", ".AppleSystemUIFont", "Arial",
        "Verdana", "Georgia", "Courier New", "Times New Roman", "Menlo", "Trebuchet MS", "Segoe UI",
    ];

    /// <summary>Labels chosen for their ink shape, not their prose: no descender / one descender /
    /// ascender + descender / mixed with a space. If the icon's position depended on the label at
    /// all, one of these would move it.</summary>
    private static readonly string[] StatusWords =
        ["Active", "Running", "Suspended", "Uploading", "Waiting to run", "No new tasks"];

    /// <summary>Render scales the pixel sweep runs at: 1x is the harness default, 2x the owner's
    /// Retina Mac (and the scale where rounding lands on half-DIP boundaries).</summary>
    private static readonly double[] Scalings = [1.0, 2.0];

    /// <summary>
    /// The shipped UI scripts, each with labels of its own. zh-CN is not decoration: those statuses
    /// are Han, and their band is a different glyph resolved through a different fallback face. A
    /// figures cell renders "MM-dd HH:mm" in every locale (invariant-culture formatting), so its
    /// label does not vary by script — only the word cells' does.
    /// </summary>
    private static readonly (string Culture, Func<Probe, string> Label)[] Scripts =
    [
        ("en-US", p => p.BoundText),
        ("zh-CN", p => p.BoundText.Any(char.IsLetter) ? "运行中" : p.BoundText),
        // ja-JP has no resource set, so with the language preference on System it displays the
        // NEUTRAL ENGLISH statuses on a CJK machine (LanguageCulture.Resolve returns null for
        // System). Branching the band on the culture rather than on what resolved would hand this
        // case an ideograph band for Latin labels; this row is the gate on that (Codex P2 round 2).
        ("ja-JP", p => p.BoundText),
    ];

    /// <summary>
    /// The arranged invariant: each icon's box centre sits on the centre of THAT CELL's reference
    /// band — the outline extents of <see cref="Probe.Band"/> at the cell's own typeface and size.
    ///
    /// This is what fails pre-fix in EVERY family — by 0.024 px in Times New Roman and 1.063 px in
    /// Helvetica, which is the point: the error is a per-font constant, so a gate that probed one
    /// family would call the layout fixed on the strength of that family's luck. Comparing the two
    /// BOXES instead would pass in both states and gate nothing.
    ///
    /// Swept over both shipped UI scripts (see <see cref="Cells.UseUiCulture"/>): under zh-CN the
    /// labels are Han and the band they are read against is a different glyph in a different
    /// fallback face, which the Latin-only version of this fix got wrong.
    /// </summary>
    [AvaloniaFact]
    public void Icon_is_centred_on_the_reference_band()
    {
        AssertAcrossFamilies(scaling: 1.0, (cells, family, report) =>
        {
            foreach (var script in Scripts)
            {
                if (!cells.UseUiCulture(script.Culture)) continue;
                foreach (var probe in cells.Probes)
                {
                    cells.Show(probe, script.Label(probe));
                    double delta = probe.IconBoxCentre - probe.BandCentre;
                    if (Math.Abs(delta) > ArrangedTolerance)
                        report($"{family} · {script.Culture} · {probe.Label}: the icon's box centre sits " +
                               $"{delta:+0.000;-0.000} px from the '{probe.Band}' band's centre — the " +
                               "layout centres boxes, not ink.");
                    cells.Restore(probe);
                }
            }
        });
    }

    /// <summary>
    /// The invariant this issue exists for, and the reason #179's remedy could not be copied:
    /// NOTHING in the cell may move when the status word changes. A band read off the live word
    /// would satisfy <see cref="Icon_is_centred_on_the_cap_band"/> for one status and shift on the
    /// next poll — a twitching cell traded for a mis-centred one.
    ///
    /// BOTH ELEMENTS ARE TRACKED, not just the icon. Which one moves depends on which is taller:
    /// the panel is as tall as its tallest child, so while the band is shorter than the 12 px icon
    /// (it is, at 12 px text) a live-ink band pins the icon and slides the LABEL instead — the same
    /// defect seen from the other side. Measured across labels with and without descenders and
    /// ascenders, in every family; falsified by pointing the converter at the live text.
    /// </summary>
    [AvaloniaFact]
    public void Nothing_moves_when_the_status_word_changes()
    {
        AssertAcrossFamilies(scaling: 1.0, (cells, family, report) =>
        {
            foreach (var probe in cells.Probes)
            {
                var seen = new List<(string Word, double Icon, double Band)>();
                foreach (var word in StatusWords)
                {
                    cells.Show(probe, word);
                    seen.Add((word, probe.IconBoxCentre, probe.BandCentre));
                }

                cells.Restore(probe);

                void Spread(string what, Func<(string Word, double Icon, double Band), double> pick)
                {
                    double spread = seen.Max(pick) - seen.Min(pick);
                    if (spread > ArrangedTolerance)
                        report($"{family} · {probe.Label}: the {what} moves {spread:F3} px across status " +
                               $"words ({string.Join(", ", seen.Select(s => $"{s.Word}={pick(s):F3}"))}) — " +
                               "nothing in this cell may depend on the label's own ink.");
                }

                Spread("icon", s => s.Icon);
                Spread("cap band", s => s.Band);
            }
        });
    }

    /// <summary>
    /// The rendered half: the icon's painted ink and the label's painted ink sit where the arranged
    /// cap-band alignment says they should.
    ///
    /// EXPECTED VALUE, NOT ZERO — for the same reason the pill's pixel gate has one. The cells align
    /// the icon to the fixed cap band, not to the ink of the word on screen, so a word with a
    /// descender paints its ink centre BELOW the band's by a per-font constant. That constant is
    /// computed from outlines and is what the two rendered inks are expected to differ by; demanding
    /// zero would be demanding the icon chase the descenders.
    ///
    /// The icon side needs no such correction: a PathIcon stretches its geometry uniformly into its
    /// box and centres the result, so the painted mark's ink centre IS its box centre whatever the
    /// glyph's own asymmetry.
    /// </summary>
    // 1x is the harness default; 2x is a Retina Mac — the owner's own condition, and the one where
    // layout rounding lands on half-DIP boundaries instead of whole ones. BOTH run against the same
    // window (rescaled between passes) rather than as a theory: a window per case is the thing
    // #179 measured to destabilise the neighbouring pixel gate, and this class already carries the
    // heaviest window in the assembly.
    //
    // The SCRIPT sweep runs here too, not only in the arranged gate (Codex P1 on PR #184): the CJK
    // case is the one where a fallback face draws the label, so leaving it to exact geometry alone
    // would ship the localized fix with no end-state pixel verification of the very path — fallback
    // resolution and its rasterisation — that is special about it.
    [AvaloniaFact]
    public void Rendered_icon_ink_sits_on_the_cap_band()
    {
        AssertAcrossFamilies(scaling: 1.0, (cells, family, report) =>
        {
            foreach (var script in Scripts)
            {
                if (!cells.UseUiCulture(script.Culture)) continue;
                foreach (var probe in cells.Probes)
                    cells.Show(probe, script.Label(probe));

                foreach (double scaling in Scalings)
                {
                    cells.Rescale(scaling);
                    foreach (var (probe, ink) in cells.MeasureRenderedInk())
                    {
                        double expected = probe.ShownTextOffsetFromBand;
                        double delta = ink.IconCentre - ink.TextCentre;
                        if (Math.Abs(delta - expected) > MaxInkDeviationDip(scaling))
                            report($"{family} @{scaling}x · {script.Culture} · {probe.Label}: the icon's " +
                                   $"ink centre sits {delta:+0.000;-0.000} px from the label's (expected " +
                                   $"{expected:+0.000;-0.000} px, cap ±{MaxInkDeviationDip(scaling)}). " +
                                   $"icon={ink.IconTop:F3}..{ink.IconBottom:F3}, " +
                                   $"text={ink.TextTop:F3}..{ink.TextBottom:F3}.");
                    }
                }

                cells.Rescale(1.0);
                foreach (var probe in cells.Probes)
                    cells.Restore(probe);
            }
        });
    }

    /// <summary>
    /// Runs <paramref name="probe"/> over every family this runner actually has, inside ONE window.
    ///
    /// ONE WINDOW, NOT ONE PER CASE. A window per theory case was measured to destabilise the
    /// neighbouring pixel gate in this assembly (SnoozePillAlignmentTests' finding: the
    /// MenuSeparatorVisualTests host-rail case intermittently captured a frame with no menu in it,
    /// and the flake tracked the ShellWindow count). Sweeping families inside a single window keeps
    /// the coverage while opening one window per test instead of one per family. Failures are
    /// collected rather than thrown one at a time, so a regression reports EVERY family and cell it
    /// broke, not just the first the runner happened to reach.
    /// </summary>
    private void AssertAcrossFamilies(double scaling, Action<Cells, string, Action<string>> probe)
    {
        var failures = new List<string>();
        var probed = new List<string>();

        using var cells = Cells.Open(ThemeVariant.Dark, scaling);

        // A theme's first render populates Skia's glyph/render caches and differs from later ones
        // (VisualWarmup's finding); this gate shares its process with the baseline captures.
        cells.Capture().Dispose();

        // The construction is what is under test, so an empty probe set is a broken harness, not a
        // clean bill of health. Four cells carry it today: Tasks' State and Deadline, Projects' and
        // Transfers' Status.
        Assert.Equal(4, cells.Probes.Count);

        foreach (var family in FamilyNames)
        {
            if (Resolve(family) is not { } resolved)
                continue;
            probed.Add(family);
            cells.UseFont(resolved);
            probe(cells, family, failures.Add);
        }

        // Without this a runner whose font manager resolved nothing would report a serene green over
        // an empty sweep. Inter ships embedded with the harness, so its absence means the probe
        // itself is broken, not the runner.
        Assert.Contains("Inter", probed);

        // Coverage this runner could not give is ANNOUNCED, never silently dropped: a green from a
        // sweep that skipped the CJK script (a runner with no Han face) must not read like a green
        // from the full one.
        output.WriteLine($"probed {probed.Count}/{FamilyNames.Length} families: {string.Join(", ", probed)}");
        output.WriteLine(cells.SkippedScripts.Count == 0
            ? $"probed all {Scripts.Length} UI scripts"
            : $"SKIPPED scripts (runner cannot draw their band): {string.Join(", ", cells.SkippedScripts)}");

        Assert.True(failures.Count == 0,
            $"probed {probed.Count} of {FamilyNames.Length} families ({string.Join(", ", probed)}) " +
            $"over {cells.Probes.Count} cells:" +
            Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    /// <summary>The family, or null when this runner does not have it. A font manager that cannot
    /// find a family silently substitutes the default one — which would re-probe Inter under another
    /// name and manufacture a green. (Same rule, same reasoning, as the pill gate's.)</summary>
    private static FontFamily? Resolve(string family)
    {
        var candidate = new FontFamily(family);
        if (!FontManager.Current.TryGetGlyphTypeface(new Typeface(candidate), out var typeface))
            return null;
        if (typeface.FamilyName == family)
            return candidate;
        return family.StartsWith('.') && typeface.FamilyName != FontManager.Current.DefaultFontFamily.Name
            ? candidate
            : null;
    }

    private readonly record struct RenderedInk(
        double IconTop, double IconBottom, double IconCentre,
        double TextTop, double TextBottom, double TextCentre);

    /// <summary>
    /// One icon-and-label cell: the icon's box (a Panel of state glyphs, or a bare PathIcon), the
    /// label, and the row they live in.
    /// </summary>
    private sealed class Probe(string label, Control icon, TextBlock text, Visual row, Window window)
    {
        public string Label { get; } = label;
        public Control Icon { get; } = icon;
        public TextBlock Text { get; } = text;
        public Visual Row { get; } = row;

        /// <summary>The label the cell's binding put there, restored after a word sweep.</summary>
        public string BoundText { get; } = text.Text ?? "";

        private double Top(Visual visual) => visual.TranslatePoint(new Point(0, 0), window)!.Value.Y;

        private Rect InkOf(string content) =>
            new FormattedText(content, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(Text.FontFamily, Text.FontStyle, Text.FontWeight), Text.FontSize,
                Brushes.Black).BuildGeometry(default)!.Bounds;

        public double IconBoxCentre => Top(Icon) + Icon.Bounds.Height / 2;

        /// <summary>
        /// The band this cell OUGHT to align to, derived from what its binding actually put on
        /// screen: a cell whose real text carries no letter is a figures cell (Tasks' Deadline,
        /// "MM-dd HH:mm" — invariant culture, so figures in every locale) and belongs on the digit
        /// band; anything else is a word cell and belongs on its UI script's band.
        ///
        /// DELIBERATELY NOT READ FROM THE CLASS the fix applies. An expectation taken from the class
        /// would say "this cell is aligned to the band it claims", which is true however wrongly the
        /// class was chosen — mark the Deadline cell as a word cell and the gate would follow it and
        /// stay green. Deriving it from the rendered repertoire keeps the choice itself under test
        /// (Codex P2 on PR #184 was exactly a wrong choice here).
        /// </summary>
        public string Band => BoundText.Any(char.IsLetter)
            ? TextInkCollapseConverter.WordBandFor(Strings.TaskStateRunning)
            : TextInkCollapseConverter.DigitBand;

        /// <summary>Window-space centre of that band's ink, as arranged.</summary>
        public double BandCentre
        {
            get
            {
                var ink = InkOf(Band);
                return Top(Text) + (ink.Top + ink.Bottom) / 2;
            }
        }

        /// <summary>How far the text actually on screen paints its ink centre from the band the cell
        /// aligns to — zero only when the shown text's ink is exactly the band.</summary>
        public double ShownTextOffsetFromBand
        {
            get
            {
                var band = InkOf(Band);
                var shown = InkOf(Text.Text ?? "");
                return (band.Top + band.Bottom) / 2 - (shown.Top + shown.Bottom) / 2;
            }
        }
    }

    /// <summary>
    /// The three data views, each showing one hand-built row, in one window. Rows are built by hand
    /// and added to the view models directly (the TasksViewTests idiom): what is under test is the
    /// cell templates' geometry, and the store → row projection has its own suites.
    /// </summary>
    private sealed class Cells : IDisposable
    {
        private readonly Window _window;
        private readonly HostStore _store;
        private readonly HostMonitorManager _manager;
        private readonly string[] _tempFiles;
        private readonly CultureInfo _entryUiCulture = CultureInfo.CurrentUICulture;
        private readonly List<string> _skippedScripts = [];
        private FontFamily _family = FontFamily.Default;
        private double _scaling;

        private Cells(Window window, HostStore store, HostMonitorManager manager, double scaling,
            string[] tempFiles, IReadOnlyList<Probe> probes)
        {
            _window = window;
            _store = store;
            _manager = manager;
            _scaling = scaling;
            _tempFiles = tempFiles;
            Probes = probes;
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

        public IReadOnlyList<Probe> Probes { get; }

        /// <summary>Scripts this runner could not draw, so the caller can announce the gap.</summary>
        public IReadOnlyList<string> SkippedScripts => _skippedScripts;

        public static Cells Open(ThemeVariant variant, double scaling)
        {
            Application.Current!.RequestedThemeVariant = variant;

            string Temp(string tag) => Path.Combine(Path.GetTempPath(), $"lattice-cells-{Guid.NewGuid():N}-{tag}.json");
            string hostsPath = Temp("hosts"), uiPath = Temp("ui");

            var registry = new HostRegistry(new LatticeConfig(5, []), hostsPath);
            // Never started, so no poll ever rebuilds over the hand-built rows.
            var manager = new HostMonitorManager(registry, () => new FakeGuiRpcClient(), TimeProvider.System);
            var store = new HostStore(registry, manager, new InlineUiDispatcher());
            var control = new HostControlService(registry, manager, () => new FakeGuiRpcClient());
            var uiState = new UiStateStore(uiPath);
            var density = new DensityPreference(uiState);
            var clock = new InertUiClock();

            var tasks = new TasksViewModel(store, clock, uiState, density, control);
            // Attach seams are required ctor args; this fixture hand-builds rows and never
            // opens the "Add project…" dialog, so a never-invoked no-op run is the whole need.
            var projects = new ProjectsViewModel(store, clock, control, NoopAttachRun, new InlineUiDispatcher());
            var transfers = new TransfersViewModel(store, clock, density);

            tasks.Rows.Add(TaskHolder());
            projects.Rows.Add(ProjectHolder());
            transfers.Rows.Add(TransferHolder());

            var stack = new Grid { RowDefinitions = new RowDefinitions("*,*,*") };
            Control[] views =
            [
                new TasksView { DataContext = tasks },
                new ProjectsView { DataContext = projects },
                new TransfersView { DataContext = transfers },
            ];
            for (int i = 0; i < views.Length; i++)
            {
                Grid.SetRow(views[i], i);
                stack.Children.Add(views[i]);
            }

            // 1280 is ShellWindow's default width — wide enough that no responsive breakpoint sheds
            // a probed column. The height is the smallest that still gives each stacked view its
            // command bar, column header, first row and status bar: this is the heaviest window in
            // the assembly (three data views), and frame size is the other half of the pressure that
            // #179 measured on the neighbouring pixel gate.
            var window = new Window { Width = 1280, Height = 600, Content = stack };
            // Production sets this at the composition root; a hosted data view reaches the UiStateStore
            // for column-width persistence (#120) through this inherited attached property.
            ColumnWidthScope.SetStore(window, uiState);
            window.Show();
            window.SetRenderScaling(scaling);
            Layout(window);

            return new Cells(window, store, manager, scaling, [hostsPath, uiPath], Discover(window));
        }

        /// <summary>
        /// Every realized cell built as "icon box then label in one centred horizontal StackPanel" —
        /// the construction under test, found structurally rather than by the class the fix applies,
        /// so the gate covers the defect class and not just today's four instances. A panel whose
        /// first child is a Border (the progress/share BAR cells) is not this construction and is
        /// deliberately not swept.
        /// </summary>
        private static Task<AttachFlowResult> NoopAttachRun(
            Guid hostId, AttachMachine.AttachRequest request,
            IProgress<AttachMachine.Stage>? progress, CancellationToken ct) =>
            Task.FromResult(new AttachFlowResult(AttachFlowOutcome.Attached, [], null));

        private static IReadOnlyList<Probe> Discover(Window window)
        {
            var probes = new List<Probe>();
            foreach (var row in window.GetVisualDescendants().OfType<DataGridRow>())
                foreach (var panel in row.GetVisualDescendants().OfType<StackPanel>())
                {
                    if (panel.Orientation != Avalonia.Layout.Orientation.Horizontal) continue;
                    if (panel.Children is not [Control icon, TextBlock text]) continue;
                    if (icon is not (PathIcon or Panel)) continue;
                    if (icon is Panel box && !box.GetVisualDescendants().OfType<PathIcon>().Any()) continue;

                    var view = row.GetVisualAncestors().OfType<UserControl>().First();
                    probes.Add(new Probe($"{view.GetType().Name}[{text.Text}]", icon, text, row, window));
                }
            return probes;
        }

        /// <summary>Re-renders every probed label in <paramref name="family"/>. Set on the text
        /// elements, so the margin binding sees the same change an inherited font change would
        /// produce at runtime; the icons are vector paths and do not depend on the font.</summary>
        public void UseFont(FontFamily family)
        {
            _family = family;
            foreach (var probe in Probes)
                TextElement.SetFontFamily(probe.Text, family);
            Layout(_window);
        }

        /// <summary>
        /// Switches the UI culture the converter reads its band from, and re-fires the margin
        /// bindings — a culture change raises no property notification of its own, and re-applying
        /// an UNCHANGED font raises none either (measured: the margin kept the previous culture's
        /// band and the assertions went red by exactly the difference between the two bands). In
        /// production nothing needs this: the UI culture is fixed before the first cell is
        /// realized, so the first evaluation already sees the final band.
        ///
        /// Returns false when this runner cannot draw the culture's band at all: a bare CI runner
        /// with no CJK face resolves no outlines for the ideograph, the converter correctly declines
        /// to collapse onto ink that does not exist, and asserting a band nothing can render would
        /// be asserting the fallback rather than the fix. Skipping is reported by the caller through
        /// the probed-family accounting, never silently.
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
            // under the new culture. Italic is guaranteed to differ from these cells' Normal.
            foreach (var probe in Probes)
            {
                TextElement.SetFontStyle(probe.Text, FontStyle.Italic);
                TextElement.SetFontStyle(probe.Text, FontStyle.Normal);
            }
            Layout(_window);
            return true;
        }

        /// <summary>Puts <paramref name="word"/> in the cell. Production changes this text through
        /// the row binding; for layout the two are the same write to the same property.</summary>
        public void Show(Probe probe, string word)
        {
            probe.Text.Text = word;
            Layout(_window);
        }

        public void Restore(Probe probe) => Show(probe, probe.BoundText);

        private static void Layout(Window window)
        {
            Dispatcher.UIThread.RunJobs();
            window.Measure(new Size(window.Width, window.Height));
            window.Arrange(new Rect(0, 0, window.Width, window.Height));
            Dispatcher.UIThread.RunJobs();
        }

        public Avalonia.Media.Imaging.Bitmap Capture() =>
            _window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No rendered frame captured.");

        /// <summary>
        /// Measures each probed cell's painted ink, in DIPs, so the 1x and 2x runs are directly
        /// comparable. Each element is scanned over its OWN columns across the full row height: the
        /// label's box is the cap band after the fix, so its descenders paint outside that box and a
        /// box-clipped scan would measure the band instead of the ink.
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
                var row = Device(probe.Row);
                // Inset one DIP at each edge. The row paints its bottom divider INSIDE its own
                // bounds, and on a tinted row (the at-risk one below) that rule clears the ink floor
                // in every column — it dragged the icon's measured extent to the row's foot. Nothing
                // real is lost: a 12 px mark in a 36 px row is nowhere near either edge.
                int y0 = (int)Math.Ceiling(row.Top + _scaling), y1 = (int)Math.Floor(row.Bottom - _scaling) - 1;

                // The row's own fill, sampled from a column strip with no content in it.
                var background = Modal(pixels, (int)row.Left + 2, (int)row.Left + 6, y0, y1);
                var icon = Ink(pixels, background, Device(probe.Icon), y0, y1, $"{probe.Label} icon");
                var text = Ink(pixels, background, Device(probe.Text), y0, y1, $"{probe.Label} label");

                results.Add((probe, new RenderedInk(
                    (icon.Top - row.Top) / _scaling, (icon.Bottom - row.Top) / _scaling,
                    (icon.Centre - row.Top) / _scaling,
                    (text.Top - row.Top) / _scaling, (text.Bottom - row.Top) / _scaling,
                    (text.Centre - row.Top) / _scaling)));
            }
            return results;
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
        /// Sub-pixel ink extents and coverage centre for one element's column range. Coverage is a
        /// pixel's distance from the row's fill normalised by the strongest ink in the same columns,
        /// so a partly covered edge row's coverage IS the fraction of that row the mark covers —
        /// which is what makes the extents sub-pixel instead of integer-quantised. The centre is the
        /// midpoint of those extents (not the coverage centroid): the two elements have different
        /// ink densities, and a centroid would weigh a bold word's mass against a thin outline's.
        /// </summary>
        private static (double Top, double Bottom, double Centre) Ink(
            PixelBuffer pixels, (int r, int g, int b) background, Rect element, int y0, int y1, string what)
        {
            int x0 = (int)Math.Floor(element.Left), x1 = (int)Math.Ceiling(element.Right) - 1;

            int Distance(int x, int y)
            {
                var (r, g, b) = pixels.Rgb(x, y);
                return Math.Abs(r - background.r) + Math.Abs(g - background.g) + Math.Abs(b - background.b);
            }

            int strongest = 0;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    strongest = Math.Max(strongest, Distance(x, y));
            Assert.True(strongest > 0, $"{what} painted no ink in its own columns.");

            var rows = new double[y1 - y0 + 1];
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    rows[y - y0] = Math.Max(rows[y - y0], Math.Clamp(Distance(x, y) / (double)strongest, 0, 1));

            // A floor above anti-aliasing noise but below any real glyph row (the pill gate's value).
            const double InkFloor = 0.10;
            int first = Array.FindIndex(rows, row => row > InkFloor);
            int last = Array.FindLastIndex(rows, row => row > InkFloor);
            Assert.True(first >= 0,
                $"no row in {what}'s columns x={x0}..{x1} cleared the {InkFloor:P0} ink floor, so its " +
                "extent cannot be measured — it painted nothing, or only anti-aliasing fringe.");

            double top = y0 + first + (1 - rows[first]), bottom = y0 + last + rows[last];
            return (top, bottom, (top + bottom) / 2);
        }

        public void Dispose()
        {
            // ModuleInit pins en-US so the zh-CN satellite cannot bleed Han glyphs into a committed
            // baseline (#147). This class is the one thing in the assembly that moves it, and the
            // suite runs serially in one process, so putting it back is not optional.
            CultureInfo.CurrentUICulture = _entryUiCulture;
            _window.Close();
            _store.Dispose();
            _manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
            foreach (var path in _tempFiles)
                File.Delete(path);
        }

        private static TaskRow TaskHolder()
        {
            var data = new TaskRowViewModel(
                Project: "Einstein@Home", Application: "binary radio pulsar search", Name: "task_00",
                Fraction: 0.42, PercentText: "42%", ElapsedText: "1h 04m", RemainingText: "22m",
                DeadlineText: "07-11 00:00", Deadline: new DateTimeOffset(2026, 7, 11, 0, 0, 0, TimeSpan.Zero),
                StateKind: TaskStateKind.Running, StateText: "Running",
                // At risk so the Deadline cell's warning glyph is visible — an invisible icon has no
                // arranged box to centre anything on.
                IsDeadlineAtRisk: true, IsSuspended: false, HostId: Guid.NewGuid(), Host: "mini-01");
            return new TaskRow(data.Key, data);
        }

        private static ProjectRow ProjectHolder()
        {
            const string url = "https://einstein.phys.uwm.edu/";
            var data = new ProjectRowViewModel(
                Key: ProjectRowKey.NewParentKey(url), MasterUrl: url, HostId: null, IsParent: true,
                IsExpanded: false, ShowChevron: false, Name: "Einstein@Home", HostsText: "1",
                ShareText: "100", ShowShareBar: true, ShareFraction: 1.0,
                AvgCreditText: "1,204", TotalCreditText: "98,231", TasksText: "",
                StatusKind: ProjectStatusKind.Active, StatusText: "Active",
                SortKey: new RowSortKey(
                    new GroupSortKey(nameKey: "einstein", hostCount: 1, shareMax: 100, shareMin: 100,
                        avgCredit: 1204, totalCredit: 98231, statusRank: 0, masterUrl: url),
                    RowLevel.ParentRow));
            return new ProjectRow(data.Key, data);
        }

        private static TransferRow TransferHolder()
        {
            var hostId = Guid.NewGuid();
            var data = new TransferRowViewModel(
                Key: new TransferRowKey(hostId, "https://einstein.phys.uwm.edu/", "h1_0201.dat", false),
                Name: "h1_0201.dat", Project: "Einstein@Home", DirectionText: "↓",
                ProgressText: "63%", Fraction: 0.63, SpeedText: "1.4 MB/s",
                UiState: TransferUiState.Active, StatusText: "Active", HostId: hostId, Host: "mini-01");
            return new TransferRow(data.Key, data);
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
