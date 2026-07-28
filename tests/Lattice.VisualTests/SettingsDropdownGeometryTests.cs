using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Lattice.App.Infrastructure;
using Lattice.App.ViewModels;
using Lattice.App.Views;
using Lattice.Core;
using Lattice.Tests;
using Xunit;
using static Lattice.Tests.HeadlessLayout;

namespace Lattice.VisualTests;

/// <summary>
/// Geometry gate for issue #200: a Settings dropdown must look the same whichever option is
/// involved — the closed box must not resize with the selection, and the option rows must not
/// resize with the script their label happens to be written in.
///
/// TWO independent defects, both owner-reported on real hardware, both reproduced here before
/// being fixed:
///
/// <list type="number">
/// <item><b>Width, from the selected item.</b> FluentAvalonia's ComboBox theme sets
/// <c>HorizontalAlignment="Left"</c> and fills the closed box from <c>SelectionBoxItem</c> — the
/// SELECTED item, never the whole item set — so an unconstrained ComboBox is exactly as wide as
/// whatever is picked. Avalonia documents that as by-design ("the length and height of the combo
/// box are determined by the selected item, unless you define them explicitly") and defining the
/// width is the documented remedy. Un-fixed, the language row settled at 144 / 92 / 72 px across
/// its options under en and 100 / 92 / 72 px under zh-CN, and the three rows disagreed with each
/// other as well (zh-CN: language 100, theme 100, polling 64).</item>
///
/// <item><b>Height, from font fallback.</b> A CJK label has no glyph coverage in the UI font, so
/// it is rendered from a fallback CJK face whose line box is TALLER: at <c>FontSize=14</c>,
/// 中文 measures 20 px against 17 px for English. FA's ComboBoxItem template derives the row
/// height from its content, so the 中文 row rendered 34 px tall against 31 px for its Latin
/// neighbours — a visibly deeper highlight block on one option of the language menu (3 logical
/// px, 6 device px on the owner's 2× display). Same root cause as the #147 version-label fix
/// already recorded in SettingsView.axaml. Pinning <c>LineHeight</c> makes the line box a
/// constant instead of a function of which face supplied the glyphs.</item>
/// </list>
///
/// The dropdown rows' WIDTHS were never the defect: FA's ComboBoxItem theme already stretches
/// them (<c>HorizontalContentAlignment="Stretch"</c>, and the items panel stretches its
/// children), and all three measured 130 px inside a 132 px popup on the un-fixed tree.
///
/// This lives in Lattice.VisualTests rather than Lattice.App.Tests because both assertions are
/// FONT-METRIC and only this assembly renders with a real font: Avalonia's plain headless
/// platform fakes glyph advances at exactly one em per character and gives every script the same
/// line box, so neither defect is observable there at all. Inter is pinned here (TestAppBuilder),
/// so the numbers are stable across the CI runners — the same reasoning that keeps
/// <see cref="ComboBoxTextCenteringVisualTests"/> out of the env-gated screenshot family and in
/// the normal <c>dotnet test</c> run. Both assertions compare measurements for EQUALITY, so a
/// runner with no CJK face installed (where a fallback cannot differ) reports green rather than
/// a false failure.
/// </summary>
[Trait("Category", "Visual")]
public class SettingsDropdownGeometryTests
{
    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("zh-CN")]
    public void Every_settings_dropdown_keeps_one_width_across_all_of_its_options(string culture)
        => InCulture(culture, () =>
        {
            // Each row is measured over EVERY option it offers, so a label that outgrows the
            // reserved width fails here instead of silently reintroducing the jump.
            IReadOnlyList<double> language = WidthsOver(
                SettingsViewModel.AllLanguages, (vm, v) => vm.SelectedLanguage = v,
                SettingsViewModel.AllLanguages);
            IReadOnlyList<double> theme = WidthsOver(
                SettingsViewModel.AllThemes, (vm, v) => vm.SelectedTheme = v,
                SettingsViewModel.AllThemes);
            IReadOnlyList<double> polling = WidthsOver(
                SettingsViewModel.AllowedPollingIntervals, (vm, v) => vm.PollingIntervalSeconds = v,
                SettingsViewModel.AllowedPollingIntervals);

            AssertOneValue("language dropdown's width", language);
            AssertOneValue("theme dropdown's width", theme);
            AssertOneValue("polling dropdown's width", polling);

            // …and the three rows agree with each other, so the Settings page reads as one
            // aligned column of controls rather than three ragged ones.
            AssertOneValue("width across the three dropdowns", [language[0], theme[0], polling[0]]);
        });

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("zh-CN")]
    public void Every_option_row_of_a_settings_dropdown_has_the_same_height(string culture)
        => InCulture(culture, () =>
        {
            // The language menu is the one that MIXES scripts within a single list (System
            // default / English / 中文 under en, 跟随系统 / English / 中文 under zh-CN), which is
            // where the taller CJK line box showed up. The other two are measured as well: their
            // labels are script-uniform per culture, so they only break if the row height stops
            // being pinned at all.
            IReadOnlyList<double> language = RowHeightsOf(SettingsViewModel.AllLanguages);
            IReadOnlyList<double> theme = RowHeightsOf(SettingsViewModel.AllThemes);
            IReadOnlyList<double> polling = RowHeightsOf(SettingsViewModel.AllowedPollingIntervals);

            AssertOneValue("language menu's row height", language);
            AssertOneValue("theme menu's row height", theme);
            AssertOneValue("polling menu's row height", polling);

            AssertOneValue("row height across the three menus", [language[0], theme[0], polling[0]]);
        });

    private static void InCulture(string culture, Action body)
    {
        CultureInfo previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = new CultureInfo(culture);
        try
        {
            body();
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    private static void AssertOneValue(string what, IReadOnlyList<double> measured) =>
        Assert.True(
            measured.Distinct().Count() == 1,
            $"The {what} is not a single value: " +
            $"[{string.Join(", ", measured.Select(v => v.ToString("F1", CultureInfo.InvariantCulture)))}]. " +
            "A Settings dropdown must look the same whichever option is involved.");

    /// <summary>
    /// Renders the real <see cref="SettingsView"/> once per option, applying <paramref name="select"/>
    /// before the first layout pass, and returns the width the row's ComboBox settled at.
    /// </summary>
    private static IReadOnlyList<double> WidthsOver<T>(
        IEnumerable<T> options, Action<SettingsViewModel, T> select, object itemsSource) =>
        [.. options.Select(option =>
        {
            (Window window, SettingsViewModel settings) = MakeView();
            select(settings, option);
            window.Show();
            Layout(window);
            double width = Dropdown(window, itemsSource).Bounds.Width;
            window.Close();
            return width;
        })];

    /// <summary>
    /// Opens one dropdown and returns the height of every realized option row. The rows live in
    /// the popup's own visual tree, which hangs off the Popup's child rather than off the window,
    /// so it is reached by walking up from that child.
    /// </summary>
    private static IReadOnlyList<double> RowHeightsOf(object itemsSource)
    {
        (Window window, SettingsViewModel _) = MakeView();
        window.Show();
        Layout(window);

        ComboBox dropdown = Dropdown(window, itemsSource);
        dropdown.IsDropDownOpen = true;
        Pump();
        Settle(window);
        Pump();

        Visual? popupRoot = dropdown.GetVisualDescendants().OfType<Popup>().Single().Child;
        while (popupRoot?.GetVisualParent() is { } parent)
            popupRoot = parent;

        double[] heights = [.. popupRoot!.GetSelfAndVisualDescendants().OfType<ComboBoxItem>()
            .Select(item => item.Bounds.Height)];
        window.Close();

        // Guards the false green: an unrealized popup would make "all rows equal" vacuously true.
        Assert.NotEmpty(heights);
        return heights;
    }

    private static ComboBox Dropdown(Window window, object itemsSource) =>
        window.GetVisualDescendants().OfType<ComboBox>()
            .Single(c => ReferenceEquals(c.ItemsSource, itemsSource));

    private static (Window Window, SettingsViewModel Settings) MakeView()
    {
        string path = Path.Combine(Path.GetTempPath(), $"lattice-visual-{Guid.NewGuid():N}.json");
        var registry = new HostRegistry(new LatticeConfig(5, []), path);
        var uiState = new UiStateStore(
            Path.Combine(Path.GetTempPath(), $"lattice-visual-{Guid.NewGuid():N}-ui.json"));
        // startup: null yields an UnsupportedStartupRegistration — no login item is ever written.
        var settings = new SettingsViewModel(
            registry, () => new FakeGuiRpcClient(), new ThemePreference(uiState),
            new LanguagePreference(uiState), uiState);
        return (new Window { Width = 900, Height = 700, Content = new SettingsView { DataContext = settings } },
            settings);
    }
}
