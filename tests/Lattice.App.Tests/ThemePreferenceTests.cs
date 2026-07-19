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
            // Start from a non-System persisted theme so Set(System) is a real change
            // (Set no-ops when the value is unchanged, and construction no longer applies
            // anything — #101), forcing Apply to exercise the System => Default arm.
            var store = new UiStateStore(path);
            store.Save(UiState.Default with { Theme = AppTheme.Dark });
            var pref = new ThemePreference(store);
            pref.Set(AppTheme.System);
            Assert.Equal(ThemeVariant.Default, Application.Current!.RequestedThemeVariant);
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = original;
            File.Delete(path);
        }
    }

    /// <summary>#101 regression pin. Constructing <see cref="ThemePreference"/> must NOT touch
    /// the UI-thread-affine <c>Application.Current.RequestedThemeVariant</c> — a ctor that wrote
    /// it was the off-thread race. Set a sentinel variant, construct a preference whose PERSISTED
    /// theme differs, and assert construction left the sentinel intact; only the explicit
    /// composition-root <see cref="ThemePreference.ApplyInitial"/> applies it. Deterministic
    /// (runs on the UI thread) — a ctor that applied would flip the sentinel right here. A
    /// worker-thread "should throw" test cannot pin this deterministically: the write only throws
    /// when the pool thread happens to satisfy <c>Dispatcher.UIThread.CheckAccess()</c> yet differs
    /// from the object's affine thread, so this asserts the invariant the fix establishes instead.</summary>
    [AvaloniaFact]
    public void Construction_does_not_apply_the_theme_only_the_explicit_initial_apply_does()
    {
        ThemeVariant original = Application.Current!.RequestedThemeVariant!;
        var path = NewPath();
        try
        {
            var store = new UiStateStore(path);
            store.Save(UiState.Default with { Theme = AppTheme.Dark });
            Application.Current!.RequestedThemeVariant = ThemeVariant.Light; // sentinel a Dark ctor-apply would overwrite

            var pref = new ThemePreference(store);

            Assert.Equal(AppTheme.Dark, pref.Value);                                       // value loaded...
            Assert.Equal(ThemeVariant.Light, Application.Current!.RequestedThemeVariant);  // ...but the global untouched

            pref.ApplyInitial();
            Assert.Equal(ThemeVariant.Dark, Application.Current!.RequestedThemeVariant);   // explicit apply themes it
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = original;
            File.Delete(path);
        }
    }
}
