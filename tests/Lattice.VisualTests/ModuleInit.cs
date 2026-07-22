using System.Globalization;
using System.Runtime.CompilerServices;
using VerifyTests;

namespace Lattice.VisualTests;

internal static class ModuleInit
{
    // Swap Verify's default hash/byte-exact PNG comparer (which anti-aliasing jitter would fail)
    // for our dual-tolerance one — mean-error band + supra-tolerance pixel-error-count — then
    // finalize plugins. Snapshots are verified as PNG streams (see FirstRunViewVisualTests), so
    // no Verify.Avalonia control converter / IncludeThemeVariant is involved.
    [ModuleInitializer]
    public static void Init()
    {
        // Visual baselines are English-only (#147): pin the UI culture so the zh-CN
        // satellite never bleeds Chinese glyphs into a snapshot on a zh-localized dev
        // machine. Only UI culture (resource lookup) — number/date stay as-is.
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        TolerantPngComparer.Register();
        VerifierSettings.InitializePlugins();
    }
}
