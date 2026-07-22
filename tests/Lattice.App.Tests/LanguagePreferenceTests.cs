using Lattice.App.Infrastructure;
using Xunit;

namespace Lattice.App.Tests;

/// <summary>
/// LanguagePreference is the single owner of UiState.Language: live value +
/// persistence (mirrors ThemePreference's shape, minus the runtime Apply — language
/// takes effect on restart). Unlike ThemePreference it touches no Application-global
/// state, so a plain [Fact] suffices.
/// </summary>
public class LanguagePreferenceTests
{
    private static string NewPath() => Path.Combine(Path.GetTempPath(), $"lattice-ui-{Guid.NewGuid():N}.json");

    [Fact]
    public void Default_is_system_when_nothing_persisted()
    {
        var path = NewPath();
        try
        {
            Assert.Equal(AppLanguage.System, new LanguagePreference(new UiStateStore(path)).Value);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Set_persists_and_reloads()
    {
        var path = NewPath();
        try
        {
            var pref = new LanguagePreference(new UiStateStore(path));
            pref.Set(AppLanguage.Chinese);

            Assert.Equal(AppLanguage.Chinese, pref.Value);
            // A fresh preference over the same store reads back the persisted choice.
            Assert.Equal(AppLanguage.Chinese, new LanguagePreference(new UiStateStore(path)).Value);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Set_preserves_sibling_ui_state_via_read_modify_write()
    {
        var path = NewPath();
        try
        {
            var store = new UiStateStore(path);
            store.Save(UiState.Default with { Theme = AppTheme.Dark });

            new LanguagePreference(store).Set(AppLanguage.English);

            // The theme another owner persisted must survive a language write.
            UiState reloaded = new UiStateStore(path).Load();
            Assert.Equal(AppTheme.Dark, reloaded.Theme);
            Assert.Equal(AppLanguage.English, reloaded.Language);
        }
        finally { File.Delete(path); }
    }
}
