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

    // These exercise the injected-setter overload so they NEVER mutate the process-global
    // DefaultThreadCurrentUICulture — which, under xunit's parallel collections, could leak
    // zh-CN into a sibling test resolving Strings and make the suite order-dependent (Codex P2).
    [Fact]
    public void ApplyAtStartup_applies_the_resolved_culture_for_an_explicit_language()
    {
        CultureInfo? applied = null;
        LanguageCulture.ApplyAtStartup(AppLanguage.Chinese, c => applied = c);
        Assert.Equal("zh-CN", applied!.Name);
    }

    [Fact]
    public void ApplyAtStartup_does_not_apply_for_system()
    {
        var calls = 0;
        LanguageCulture.ApplyAtStartup(AppLanguage.System, _ => calls++);
        Assert.Equal(0, calls);
    }
}
