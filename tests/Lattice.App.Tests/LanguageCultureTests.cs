using System.Globalization;
using Lattice.App.Infrastructure;
using Xunit;

namespace Lattice.App.Tests;

/// <summary>
/// LanguageCulture.Resolve is the pure stored-preference → UI-culture mapping (#147).
/// System resolves to null (leave the OS UI culture in place); the two explicit
/// languages resolve to their fixed cultures. A transition table pins every arm so a
/// new <see cref="AppLanguage"/> case cannot be added without extending the map (the
/// exhaustive switch is a build error otherwise).
/// </summary>
public class LanguageCultureTests
{
    [Fact]
    public void System_resolves_to_null_meaning_follow_the_os()
        => Assert.Null(LanguageCulture.Resolve(AppLanguage.System));

    [Fact]
    public void English_resolves_to_en()
        => Assert.Equal("en", LanguageCulture.Resolve(AppLanguage.English)!.Name);

    [Fact]
    public void Chinese_resolves_to_zh_CN()
        => Assert.Equal("zh-CN", LanguageCulture.Resolve(AppLanguage.Chinese)!.Name);

    [Fact]
    public void ApplyAtStartup_sets_the_ui_culture_for_an_explicit_language_only()
    {
        // Save/restore the process-global default around the mutation so this test
        // cannot leak a culture into a sibling test.
        CultureInfo? savedUi = CultureInfo.DefaultThreadCurrentUICulture;
        CultureInfo? savedFormat = CultureInfo.DefaultThreadCurrentCulture;
        try
        {
            CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("en-US");

            LanguageCulture.ApplyAtStartup(AppLanguage.Chinese);
            Assert.Equal("zh-CN", CultureInfo.DefaultThreadCurrentUICulture!.Name);
            // Number/date culture is never touched (stays InvariantCulture as-is, #147).
            Assert.Equal("en-US", CultureInfo.DefaultThreadCurrentCulture!.Name);
        }
        finally
        {
            CultureInfo.DefaultThreadCurrentUICulture = savedUi;
            CultureInfo.DefaultThreadCurrentCulture = savedFormat;
        }
    }

    [Fact]
    public void ApplyAtStartup_leaves_the_ui_culture_untouched_for_system()
    {
        CultureInfo? savedUi = CultureInfo.DefaultThreadCurrentUICulture;
        try
        {
            var sentinel = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.DefaultThreadCurrentUICulture = sentinel;

            LanguageCulture.ApplyAtStartup(AppLanguage.System);
            Assert.Equal("fr-FR", CultureInfo.DefaultThreadCurrentUICulture!.Name);
        }
        finally
        {
            CultureInfo.DefaultThreadCurrentUICulture = savedUi;
        }
    }
}
