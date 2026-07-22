using System.Globalization;
using System.Runtime.CompilerServices;

namespace Lattice.App.Tests;

/// <summary>
/// Pins the test host's UI culture to English so localized <c>Strings.X</c> lookups
/// resolve to the neutral (English) resx on every machine, independent of the
/// developer's OS language. Since #147 shipped the zh-CN satellite, an unpinned run
/// on a zh-localized dev machine (this one) returns Chinese from <c>Strings.X</c>
/// while CI's en runners stay English — an environment-split false red on every
/// English-literal assertion.
///
/// Only <see cref="CultureInfo.DefaultThreadCurrentUICulture"/> (resource lookup) is
/// pinned. <c>CurrentCulture</c> (number/date formatting) is deliberately left alone:
/// the app formats numbers with <see cref="CultureInfo.InvariantCulture"/> explicitly,
/// so the suite is already independent of it (green on both this zh dev Mac and the
/// en CI runners before #147). A module initializer runs once, before any test, and
/// sets the default for every thread whose culture is not explicitly assigned — which
/// covers the xunit worker pool.
/// </summary>
internal static class TestCulture
{
    [ModuleInitializer]
    internal static void PinEnglishUiCulture()
        => CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("en-US");
}
