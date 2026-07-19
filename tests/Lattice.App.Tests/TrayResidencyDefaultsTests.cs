using Lattice.App.Infrastructure;
using Xunit;

namespace Lattice.App.Tests;

/// <summary>
/// Transition table for the close-to-tray platform default (issue #92, plan Part 4):
/// Windows/macOS → close-to-tray ON (ExitOnClose false); Linux → exit-on-close
/// (ExitOnClose true), because tray presence is not programmatically detectable (F13)
/// and a default must never strand the app invisible.
/// </summary>
public class TrayResidencyDefaultsTests
{
    [Theory]
    [InlineData(TrayPlatform.Windows, false)]
    [InlineData(TrayPlatform.MacOS, false)]
    [InlineData(TrayPlatform.Linux, true)]
    public void ExitOnCloseDefault_matches_the_platform_table(TrayPlatform platform, bool expected)
    {
        Assert.Equal(expected, TrayResidencyDefaults.ExitOnCloseDefault(platform));
    }

    [Theory]
    [InlineData(TrayPlatform.Windows)]
    [InlineData(TrayPlatform.MacOS)]
    [InlineData(TrayPlatform.Linux)]
    public void Resolve_null_falls_to_the_platform_default(TrayPlatform platform)
    {
        Assert.Equal(TrayResidencyDefaults.ExitOnCloseDefault(platform),
            TrayResidencyDefaults.Resolve(null, platform));
    }

    [Theory]
    // A concrete stored bool ALWAYS wins over the platform default — even on Linux,
    // where the default disagrees with the stored true/false.
    [InlineData(true, TrayPlatform.Windows)]
    [InlineData(false, TrayPlatform.Windows)]
    [InlineData(true, TrayPlatform.Linux)]
    [InlineData(false, TrayPlatform.Linux)]
    public void Resolve_stored_bool_wins_over_the_default(bool stored, TrayPlatform platform)
    {
        Assert.Equal(stored, TrayResidencyDefaults.Resolve(stored, platform));
    }
}
