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
    private static (Window Window, SettingsViewModel Settings, HostRegistry Registry) MakeView(
        IStartupRegistration? startupRegistration = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}.json");
        var registry = new HostRegistry(new LatticeConfig(5, []), path);
        var uiPath = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}-ui.json");
        var uiStore = new UiStateStore(uiPath);
        var settings = new SettingsViewModel(
            registry, () => new FakeGuiRpcClient(), new ThemePreference(uiStore), new LanguagePreference(uiStore),
            uiStore, restart: null,
            // Fake by default: a headless run must never write a real login item.
            startup: new StartupPreference(uiStore, startupRegistration ?? new FakeStartupRegistration()));
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
                or "CloseToTrayExpander" or "FullSpeedHiddenExpander"
                or "StartAtLoginExpander" or "StartMinimizedExpander")));
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

    /// <summary>Locates the ToggleSwitch inside a named FASettingsExpander's footer.</summary>
    private static ToggleSwitch Toggle(Window window, string expanderName) =>
        window.GetVisualDescendants().OfType<FASettingsExpander>().Single(e => e.Name == expanderName)
            .GetVisualDescendants().OfType<ToggleSwitch>().Single();

    [AvaloniaFact]
    public void Startup_toggles_render_off_and_round_trip_through_the_view_model()
    {
        var registration = new FakeStartupRegistration();
        var (window, settings, _) = MakeView(registration);
        window.Show();
        Layout(window);

        ToggleSwitch atLogin = Toggle(window, "StartAtLoginExpander");
        ToggleSwitch minimized = Toggle(window, "StartMinimizedExpander");
        Assert.False(atLogin.IsChecked);
        Assert.False(minimized.IsChecked);

        // Drive the control, not the VM: this is what proves the TwoWay binding is wired.
        atLogin.IsChecked = true;
        minimized.IsChecked = true;
        Dispatcher.UIThread.RunJobs();

        Assert.True(settings.StartAtLogin);
        Assert.True(settings.StartMinimized);
        // Registering is the authoritative Apply; the minimized flag is a content Heal.
        Assert.Equal([(true, false)], registration.Calls);
        Assert.Equal([true], registration.Heals);
        window.Close();
    }

    [AvaloniaFact]
    public void An_unregistrable_launch_disables_the_startup_rows_and_explains_why()
    {
        var (window, _, _) = MakeView(new UnsupportedStartupRegistration());
        window.Show();
        Layout(window);

        Assert.False(window.GetVisualDescendants().OfType<FASettingsExpander>()
            .Single(e => e.Name == "StartAtLoginExpander").IsEffectivelyEnabled);
        Assert.False(window.GetVisualDescendants().OfType<FASettingsExpander>()
            .Single(e => e.Name == "StartMinimizedExpander").IsEffectivelyEnabled);

        var reason = window.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Text == Strings.SettingsStartupUnsupported);
        Assert.True(reason.IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void The_unsupported_note_stays_hidden_when_registration_works()
    {
        var (window, _, _) = MakeView();
        window.Show();
        Layout(window);

        var reason = window.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Text == Strings.SettingsStartupUnsupported);
        Assert.False(reason.IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void A_failed_registration_shows_the_inline_error()
    {
        var registration = new FakeStartupRegistration { Fails = true };
        var (window, settings, _) = MakeView(registration);
        window.Show();
        Layout(window);

        Assert.Empty(window.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.Text == Strings.SettingsStartupSaveFailed && t.IsEffectivelyVisible));

        Toggle(window, "StartAtLoginExpander").IsChecked = true;
        Dispatcher.UIThread.RunJobs();

        var error = window.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Text == Strings.SettingsStartupSaveFailed);
        Assert.True(error.IsEffectivelyVisible);
        // The switch snaps back: the VM re-raised and the getter still reads the stored false.
        Assert.False(settings.StartAtLogin);
        window.Close();
    }
}
