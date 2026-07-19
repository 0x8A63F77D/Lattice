using Lattice.App.Infrastructure;
using Xunit;

namespace Lattice.App.Tests;

/// <summary>
/// Platform → tray-icon-asset table (issue #108): macOS gets the monochrome TEMPLATE
/// glyph (OS-tinted for light/dark menu bars); Windows and Linux keep the full-color
/// <c>lattice.ico</c>. The rendered menu-bar appearance is an owner-eye visual gate on
/// hardware — what is machine-checkable is exactly this selection, so it is asserted here.
/// </summary>
public class TrayIconAssetPolicyTests
{
    [Fact]
    public void MacOS_gets_the_template_glyph_marked_as_template()
    {
        var asset = TrayIconAssetPolicy.Select(TrayPlatform.MacOS);

        Assert.Equal(TrayIconAssetPolicy.MacTemplateIconUri, asset.Uri);
        Assert.True(asset.IsTemplate);
    }

    [Theory]
    // Windows and Linux keep the full-color .ico their notification areas render
    // natively, and NEVER request macOS template tinting. Mutation-sensitive: flipping
    // either IsTemplate to true, or swapping in the template URI, reddens these.
    [InlineData(TrayPlatform.Windows)]
    [InlineData(TrayPlatform.Linux)]
    public void Non_mac_platforms_keep_the_color_ico_and_are_not_templates(TrayPlatform platform)
    {
        var asset = TrayIconAssetPolicy.Select(platform);

        Assert.Equal(TrayIconAssetPolicy.ColorIconUri, asset.Uri);
        Assert.False(asset.IsTemplate);
    }

    [Fact]
    public void Only_macOS_is_a_template_across_the_whole_platform_domain()
    {
        // Guards the invariant behind the platform-conditional wiring: template tinting
        // is macOS-exclusive. If a future TrayPlatform member is added, the policy's
        // exhaustive switch fails to compile first (CS8509) — this pins the current shape.
        Assert.True(TrayIconAssetPolicy.Select(TrayPlatform.MacOS).IsTemplate);
        Assert.False(TrayIconAssetPolicy.Select(TrayPlatform.Windows).IsTemplate);
        Assert.False(TrayIconAssetPolicy.Select(TrayPlatform.Linux).IsTemplate);
    }
}
