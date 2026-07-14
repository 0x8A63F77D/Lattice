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
        TolerantPngComparer.Register();
        VerifierSettings.InitializePlugins();
    }
}
