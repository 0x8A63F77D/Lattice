using System.Globalization;
using Lattice.App.Localization;
using Xunit;

namespace Lattice.App.Tests;

public class LocalizationTests
{
    [Fact]
    public void Strings_class_is_generated_public_and_resolves_keys()
    {
        Assert.Equal("Lattice", Strings.AppTitle);
    }

    /// <summary>
    /// Regression pin for the #147 test-culture split. The zh-CN satellite makes
    /// <c>Strings.X</c> return Chinese under <c>CurrentUICulture=zh-CN</c> (this dev
    /// Mac's OS language), which would red every English-literal assertion locally
    /// while CI (en runners) stays green. <see cref="TestCulture"/>'s module
    /// initializer pins the test host to English so the suite is culture-independent
    /// on every machine. If this fails, the pin regressed — fix the pin, do not
    /// change the assertions elsewhere.
    /// </summary>
    [Fact]
    public void Test_host_ui_culture_is_pinned_to_english()
    {
        Assert.Equal("en", CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
        // Satellite is present in the test output, yet the pin keeps lookups English.
        Assert.Equal("Suspend", Strings.Suspend);
    }

    /// <summary>
    /// The zh-CN satellite (#147) must actually resolve to Chinese under
    /// <c>CurrentUICulture=zh-CN</c> — a missing or unbuilt satellite would silently
    /// fall back to the neutral English resx and every zh user would see English.
    /// This overrides the thread's UI culture locally (beating <see cref="TestCulture"/>'s
    /// default pin) and restores it, so it stays deterministic on any machine.
    /// </summary>
    [Fact]
    public void Zh_CN_satellite_resolves_localized_strings()
    {
        CultureInfo saved = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
            Assert.Equal("暂停", Strings.Suspend);
            Assert.Equal("设置", Strings.SettingsTitle);
            Assert.Equal("上报期限", Strings.ColDeadline);
            // Proper names/units stay unchanged across cultures.
            Assert.Equal("Lattice", Strings.AppTitle);
        }
        finally
        {
            CultureInfo.CurrentUICulture = saved;
        }
    }

    [Fact]
    public void Every_resx_key_resolves_to_a_nonempty_string()
    {
        // Drift guard: enumerate the embedded resource table and assert every
        // entry is a non-empty string. Key renames are caught at compile time
        // by the generated accessors; what this actually guards against is
        // entries with empty values (or non-string entries) slipping into the
        // table unnoticed.
        var rm = Strings.ResourceManager;
        var set = rm.GetResourceSet(System.Globalization.CultureInfo.InvariantCulture, true, true)!;
        var count = 0;
        foreach (System.Collections.DictionaryEntry entry in set)
        {
            Assert.True(entry.Value is string,
                $"resx key '{entry.Key}' is not a string (actual type: {entry.Value?.GetType().FullName ?? "null"})");
            Assert.False(string.IsNullOrEmpty((string)entry.Value!),
                $"resx key '{entry.Key}' has an empty value");
            count++;
        }
        Assert.True(count >= 1);
    }
}
