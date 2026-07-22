using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using Lattice.App.Infrastructure;
using Lattice.App.Localization;
using Lattice.App.Tests.Fakes;
using Lattice.App.ViewModels;
using Lattice.App.Views;
using Lattice.Core;
using Lattice.Tests;
using Xunit;
using static Lattice.Tests.HeadlessLayout;

namespace Lattice.App.Tests.Headless;

public class SettingsViewTests
{
    private static (Window Window, SettingsViewModel Settings, HostRegistry Registry) MakeView()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}.json");
        var registry = new HostRegistry(new LatticeConfig(5, []), path);
        var uiPath = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}-ui.json");
        var uiStore = new UiStateStore(uiPath);
        var settings = new SettingsViewModel(registry, () => new FakeGuiRpcClient(), new ThemePreference(uiStore), new LanguagePreference(uiStore), uiStore);
        // Hosts are added to prove they do NOT render as expanders in this view
        // any more — host management lives entirely in the rail (design 3b).
        registry.AddHost(TestData.MakeHostConfig(name: "a"));
        registry.AddHost(TestData.MakeHostConfig(name: "b"));

        var window = new Window { Width = 900, Height = 700, Content = new SettingsView { DataContext = settings } };
        return (window, settings, registry);
    }

    [AvaloniaFact]
    public void Renders_pointer_caption_and_no_host_expanders()
    {
        var (window, _, _) = MakeView();
        window.Show();
        Layout(window);

        // Exclude BOTH global-group expanders (Polling now, Theme after Task 14) so
        // this stays green across the sequence; the assertion is "no host-bound
        // expander remains" — every remaining expander is a named global one.
        Assert.Empty(window.GetVisualDescendants().OfType<FASettingsExpander>()
            .Where(e => e.Name is not ("PollingExpander" or "ThemeExpander" or "LanguageExpander"
                or "CloseToTrayExpander" or "FullSpeedHiddenExpander")));
        var caption = window.GetVisualDescendants().OfType<TextBlock>()
            .SingleOrDefault(t => t.Text == Strings.SettingsHostsPointer);
        Assert.NotNull(caption);
        window.Close();
    }

    [AvaloniaFact]
    public void Language_combo_binds_all_languages_and_selecting_surfaces_the_restart_hint()
    {
        var (window, settings, _) = MakeView();
        window.Show();
        Layout(window);

        // The combo is wired to AllLanguages via compiled binding + LanguageLabelConverter.
        var combo = window.GetVisualDescendants().OfType<ComboBox>()
            .Single(c => ReferenceEquals(c.ItemsSource, SettingsViewModel.AllLanguages));
        Assert.Equal(AppLanguage.System, combo.SelectedItem);

        // The restart hint + button are hidden (parent panel collapsed) until a change,
        // then latch visible. IsEffectivelyVisible accounts for the collapsed ancestor.
        var hint = window.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Text == Strings.SettingsLanguageRestartHint);
        var restartButton = window.GetVisualDescendants().OfType<Button>()
            .Single(b => Equals(b.Content, Strings.SettingsLanguageRestartButton));
        Assert.False(hint.IsEffectivelyVisible);
        Assert.False(restartButton.IsEffectivelyVisible);

        settings.SelectedLanguage = AppLanguage.Chinese;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(AppLanguage.Chinese, combo.SelectedItem);
        Assert.True(hint.IsEffectivelyVisible);
        Assert.True(restartButton.IsEffectivelyVisible);
        window.Close();
    }
}
