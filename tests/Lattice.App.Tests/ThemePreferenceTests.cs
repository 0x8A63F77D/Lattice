using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Lattice.App.Infrastructure;
using Xunit;

namespace Lattice.App.Tests;

/// <summary>
/// ThemePreference is the single owner of the app theme (UiState.Theme): live
/// value + persistence, applied to Application.Current.RequestedThemeVariant
/// (mirrors DensityPreference's shape). These tests mutate the PROCESS-GLOBAL
/// Application.Current.RequestedThemeVariant, which is shared across every
/// AvaloniaFact test in this assembly — each test saves the variant in place
/// at entry and restores it in a finally, so a run here can never leak a
/// non-default theme into an unrelated headless test that runs after it.
/// </summary>
public class ThemePreferenceTests
{
    private static string NewPath() => Path.Combine(Path.GetTempPath(), $"lattice-ui-{Guid.NewGuid():N}.json");

    [AvaloniaFact]
    public void Setting_dark_applies_and_persists()
    {
        ThemeVariant original = Application.Current!.RequestedThemeVariant!;
        var path = NewPath();
        try
        {
            var pref = new ThemePreference(new UiStateStore(path));
            pref.Set(AppTheme.Dark);

            Assert.Equal(ThemeVariant.Dark, Application.Current!.RequestedThemeVariant);
            Assert.Equal(AppTheme.Dark, new ThemePreference(new UiStateStore(path)).Value);
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = original;
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void System_maps_to_the_default_variant()
    {
        ThemeVariant original = Application.Current!.RequestedThemeVariant!;
        var path = NewPath();
        try
        {
            var pref = new ThemePreference(new UiStateStore(path));
            pref.Set(AppTheme.System);
            Assert.Equal(ThemeVariant.Default, Application.Current!.RequestedThemeVariant);
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = original;
            File.Delete(path);
        }
    }
}
