using System.Globalization;

namespace Lattice.App.Infrastructure;

/// <summary>Pure mapping from the stored <see cref="AppLanguage"/> preference to the
/// UI culture to apply, plus the startup application seam (#147). Resolve is
/// referentially transparent (no I/O, no globals); ApplyAtStartup is the single
/// thin side-effecting shell that writes the process-wide default UI culture.</summary>
public static class LanguageCulture
{
    /// <summary>The UI culture a stored language maps to, or <c>null</c> for
    /// <see cref="AppLanguage.System"/> — meaning "leave the OS UI culture in place".
    /// Exhaustive switch (no <c>_</c> arm): a new language must extend this map or the
    /// build breaks.</summary>
#pragma warning disable CS8524 // No `_` arm on purpose: CS8509 (a new NAMED AppLanguage left
    // unhandled) must stay a build error so this map is revisited. CS8524 is the residual
    // "unnamed enum value" case (an out-of-range cast like (AppLanguage)3, unreachable for a
    // well-formed value) and is suppressed here; a `_` arm would silence CS8509 too and defeat
    // the guard. Same pattern as ThemePreference / ThemeLabelConverter / RailTierProjection.
    public static CultureInfo? Resolve(AppLanguage language) => language switch
    {
        AppLanguage.System => null,
        AppLanguage.English => CultureInfo.GetCultureInfo("en"),
        AppLanguage.Chinese => CultureInfo.GetCultureInfo("zh-CN"),
    };
#pragma warning restore CS8524

    /// <summary>Applies the language's UI culture as the process-wide default at startup,
    /// BEFORE any UI is built (x:Static resource lookups read the culture once at load).
    /// Only <see cref="CultureInfo.DefaultThreadCurrentUICulture"/> (resource lookup) is
    /// set — number/date formatting stays as-is (the app formats numbers with
    /// InvariantCulture explicitly, #147). System leaves the OS UI culture untouched.</summary>
    public static void ApplyAtStartup(AppLanguage language)
        => ApplyAtStartup(language, static c => CultureInfo.DefaultThreadCurrentUICulture = c);

    /// <summary>Testable core: applies the resolved culture through <paramref name="setUiCulture"/>
    /// (System is a no-op). The public overload injects the real process-wide setter; tests inject a
    /// capturing lambda so they verify the mapping WITHOUT mutating the global default culture — which,
    /// under xunit's parallel collections, would leak <c>zh-CN</c> into a sibling test resolving
    /// <c>Strings</c> and make the suite order-dependent (Codex P2, PR #149).</summary>
    internal static void ApplyAtStartup(AppLanguage language, Action<CultureInfo> setUiCulture)
    {
        if (Resolve(language) is { } culture)
            setUiCulture(culture);
    }
}
