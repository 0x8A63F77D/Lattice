using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lattice.App.Infrastructure;
using Lattice.App.Tests.Fakes;
using Lattice.App.ViewModels;
using Lattice.App.Views;
using Lattice.Core;
using Lattice.Tests;
using Xunit;
using static Lattice.Tests.HeadlessLayout;

namespace Lattice.App.Tests.Headless;

/// <summary>
/// Exercises the Theme setting end to end through the Settings view: selecting
/// AppTheme.Dark on the view-model applies Application.Current.RequestedThemeVariant
/// and is reflected back by the ComboBox binding. Like ThemePreferenceTests, this
/// mutates the PROCESS-GLOBAL theme variant, which every AvaloniaFact test in this
/// assembly shares — save it at entry and restore it in a finally so this test can
/// never leak a non-default theme into a headless test that runs after it, in this
/// order or any other.
/// </summary>
public class ThemeSettingTests
{
    private static (Window Window, SettingsViewModel Settings, ComboBox Combo) MakeView()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}.json");
        var uiPath = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}-ui.json");
        var registry = new HostRegistry(new LatticeConfig(5, []), path);
        var settings = new SettingsViewModel(registry, () => new FakeGuiRpcClient(),
            new ThemePreference(new UiStateStore(uiPath)));

        var window = new Window { Width = 900, Height = 700, Content = new SettingsView { DataContext = settings } };
        window.Show();
        Layout(window);
        var combo = window.GetVisualDescendants().OfType<ComboBox>()
            .Single(c => ReferenceEquals(c.ItemsSource, SettingsViewModel.AllThemes));
        return (window, settings, combo);
    }

    [AvaloniaFact]
    public void Selecting_dark_applies_the_theme_variant_and_the_combo_reflects_it()
    {
        ThemeVariant original = Application.Current!.RequestedThemeVariant!;
        try
        {
            var (window, settings, combo) = MakeView();
            try
            {
                settings.SelectedTheme = AppTheme.Dark;
                Dispatcher.UIThread.RunJobs();

                Assert.Equal(ThemeVariant.Dark, Application.Current!.RequestedThemeVariant);
                Assert.Equal(AppTheme.Dark, combo.SelectedItem);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = original;
        }
    }

    [AvaloniaFact]
    public void The_combo_starts_on_the_persisted_theme()
    {
        ThemeVariant original = Application.Current!.RequestedThemeVariant!;
        var uiPath = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}-ui.json");
        try
        {
            var uiStore = new UiStateStore(uiPath);
            uiStore.Save(UiState.Default with { Theme = AppTheme.Light });
            var path = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}.json");
            var registry = new HostRegistry(new LatticeConfig(5, []), path);
            var settings = new SettingsViewModel(registry, () => new FakeGuiRpcClient(),
                new ThemePreference(uiStore));

            var window = new Window { Width = 900, Height = 700, Content = new SettingsView { DataContext = settings } };
            window.Show();
            Layout(window);
            try
            {
                var combo = window.GetVisualDescendants().OfType<ComboBox>()
                    .Single(c => ReferenceEquals(c.ItemsSource, SettingsViewModel.AllThemes));

                Assert.Equal(AppTheme.Light, combo.SelectedItem);
                Assert.Equal(ThemeVariant.Light, Application.Current!.RequestedThemeVariant);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = original;
            File.Delete(uiPath);
        }
    }
}
