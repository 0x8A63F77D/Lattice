using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Xunit;

namespace Lattice.App.Tests.Headless;

public class ThemeResourceTests
{
    [AvaloniaTheory]
    [InlineData("LatticeCanvasBrush")]
    [InlineData("LatticeSurfaceBrush")]
    [InlineData("LatticeNavSurfaceBrush")]
    [InlineData("LatticeStrokeBrush")]
    [InlineData("LatticeTextPrimaryBrush")]
    [InlineData("LatticeTextSecondaryBrush")]
    [InlineData("LatticeTextTertiaryBrush")]
    [InlineData("LatticeAccentBrush")]
    [InlineData("LatticeSelectedTintBrush")]
    [InlineData("LatticeSuccessBrush")]
    [InlineData("LatticeWarningFgBrush")]
    [InlineData("LatticeWarningTintBrush")]
    [InlineData("LatticeDangerFgBrush")]
    [InlineData("LatticeDangerTintBrush")]
    [InlineData("LatticeNeutralFgBrush")]
    [InlineData("LatticeDisabledBrush")]
    public void Token_brush_resolves_differently_per_theme_variant(string key)
    {
        Assert.True(Application.Current!.TryGetResource(key, ThemeVariant.Light, out var light));
        Assert.True(Application.Current!.TryGetResource(key, ThemeVariant.Dark, out var dark));
        Assert.IsType<SolidColorBrush>(light);
        Assert.IsType<SolidColorBrush>(dark);
        Assert.NotEqual(((SolidColorBrush)light!).Color, ((SolidColorBrush)dark!).Color);
    }

    [AvaloniaTheory]
    [InlineData("IconServerRegular")]
    [InlineData("IconCheckmarkCircleRegular")]
    [InlineData("IconArrowSyncRegular")]
    [InlineData("IconArrowClockwiseRegular")]
    [InlineData("IconDismissCircleRegular")]
    [InlineData("IconKeyRegular")]
    [InlineData("IconSettingsRegular")]
    [InlineData("IconAddRegular")]
    [InlineData("IconTaskListSquareLtrRegular")]
    [InlineData("IconGridRegular")]
    [InlineData("IconArrowSwapRegular")]
    [InlineData("IconDocumentTextRegular")]
    [InlineData("IconTaskListSquareLtrFilled")]
    [InlineData("IconGridFilled")]
    [InlineData("IconArrowSwapFilled")]
    [InlineData("IconDocumentTextFilled")]
    [InlineData("IconSettingsFilled")]
    [InlineData("IconChevronDownRegular")]
    [InlineData("IconChevronUpRegular")]
    [InlineData("IconChevronRightRegular")]
    [InlineData("IconDismissRegular")]
    [InlineData("IconWarningRegular")]
    [InlineData("IconInfoRegular")]
    [InlineData("IconErrorCircleRegular")]
    public void Icon_geometry_resolves(string key)
    {
        Assert.True(Application.Current!.TryGetResource(key, null, out var value), key);
        Assert.IsType<StreamGeometry>(value);
    }
}
