using System.Globalization;
using Avalonia.Controls;
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
/// Geometry gate for issue #200: a Settings row's dropdown must not change width when the
/// user picks a different option.
///
/// FluentAvalonia's ComboBox theme sets <c>HorizontalAlignment="Left"</c> and puts only
/// <c>SelectionBoxItem</c> — the SELECTED item, never the whole item set — in the closed
/// box's ContentPresenter, so an unconstrained ComboBox is exactly as wide as whatever is
/// selected. Avalonia documents this as by-design ("the length and height of the combo box
/// are determined by the selected item, unless you define them explicitly") and the
/// documented remedy is to define the width. Measured on the un-fixed tree, the language
/// row settled at 144 / 92 / 72 px under en (System default / English / 中文) and at
/// 100 / 92 / 72 px under zh-CN. The three Settings rows also disagreed with each other —
/// under zh-CN: language 100, theme 100, polling 64.
///
/// The dropdown's own rows were NOT the defect: FA's ComboBoxItem theme already stretches
/// them (<c>HorizontalContentAlignment="Stretch"</c>, and the items panel stretches its
/// children), and all three measured 130px wide inside a 132px popup on the un-fixed tree.
/// What DID look wrong there is that the popup, whose MinWidth is bound to the box's
/// Bounds.Width, was wider than the box it dropped from whenever a short option was
/// selected — an overhang the reserved width also removes.
///
/// This lives in Lattice.VisualTests rather than Lattice.App.Tests because it is a
/// FONT-METRIC assertion and only this assembly renders with a real font: Avalonia's plain
/// headless platform fakes glyph advances at exactly one em per character, which inflates
/// "System default" from its true 100px to 196px and would force the shipped MinWidth to be
/// sized for a harness artefact rather than for the UI. Inter is pinned here (TestAppBuilder),
/// so the numbers are the same on all three CI runners — the same reasoning that keeps
/// <see cref="ComboBoxTextCenteringVisualTests"/> out of the env-gated screenshot family and
/// in the normal <c>dotnet test</c> run.
/// </summary>
[Trait("Category", "Visual")]
public class SettingsControlWidthTests
{
    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("zh-CN")]
    public void Every_settings_dropdown_keeps_one_width_across_all_of_its_options(string culture)
    {
        CultureInfo previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = new CultureInfo(culture);
        try
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

            AssertOneWidth("language", language);
            AssertOneWidth("theme", theme);
            AssertOneWidth("polling", polling);

            // …and the three rows agree with each other, so the Settings page reads as one
            // aligned column of controls rather than three ragged ones.
            AssertOneWidth("all three rows", [language[0], theme[0], polling[0]]);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    private static void AssertOneWidth(string what, IReadOnlyList<double> widths) =>
        Assert.True(
            widths.Distinct().Count() == 1,
            $"The {what} ComboBox renders at more than one width: " +
            $"[{string.Join(", ", widths.Select(w => w.ToString("F1", CultureInfo.InvariantCulture)))}]. " +
            "A Settings dropdown must reserve one width for all of its options.");

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
            double width = window.GetVisualDescendants().OfType<ComboBox>()
                .Single(c => ReferenceEquals(c.ItemsSource, itemsSource))
                .Bounds.Width;
            window.Close();
            return width;
        })];

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
