using Avalonia.Controls;
using Lattice.App.Infrastructure;
using Xunit;

namespace Lattice.App.Tests;

public class MicaBackdropPolicyTests
{
    [Fact]
    public void Granted_mica_takes_the_transparent_path()
    {
        var choice = MicaBackdropPolicy.Resolve(WindowTransparencyLevel.Mica);

        Assert.True(choice.WindowTransparent);
        Assert.True(choice.RegionSurfacesTransparent);
    }

    // The whole point of #11: a DENIED Mica comes back as None, and every other
    // level the OS might grant must fold to the OPAQUE fallback — never a broken
    // transparent-without-material state. Mutation-sensitive: flipping the
    // else-branch to transparent, or widening the Mica test, reddens these.
    [Theory]
    [MemberData(nameof(NonMicaLevels))]
    public void Every_non_mica_level_falls_back_to_opaque(WindowTransparencyLevel granted)
    {
        var choice = MicaBackdropPolicy.Resolve(granted);

        Assert.False(choice.WindowTransparent);
        Assert.False(choice.RegionSurfacesTransparent);
    }

    public static TheoryData<WindowTransparencyLevel> NonMicaLevels() =>
    [
        WindowTransparencyLevel.None,
        WindowTransparencyLevel.Transparent,
        WindowTransparencyLevel.Blur,
        WindowTransparencyLevel.AcrylicBlur,
    ];
}
