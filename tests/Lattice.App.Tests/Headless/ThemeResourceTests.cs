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
    [InlineData("LatticeStrokeSubtleBrush")]
    [InlineData("LatticeTextPrimaryBrush")]
    [InlineData("LatticeTextSecondaryBrush")]
    [InlineData("LatticeTextTertiaryBrush")]
    [InlineData("LatticeAccentBrush")]
    [InlineData("LatticeSelectedTintBrush")]
    [InlineData("LatticeSuccessBrush")]
    [InlineData("LatticeWarningFgBrush")]
    [InlineData("LatticeWarningTintBrush")]
    // Restored with the M3 PR H snooze-pill border (see Tokens.axaml note); machine guard
    // that the reintroduced token stays present and theme-distinct.
    [InlineData("LatticeWarningInfoBarBorderBrush")]
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
    [InlineData("LatticeGridDividerBrush", "#FFEDEBE9", "#FF333333")]
    // Dark hover deliberately subtle (owner visual feedback: #383838 was dazzling / eye-straining).
    [InlineData("LatticeRowHoverBrush",    "#FFF5F5F5", "#FF2D2D2D")]
    public void New_grid_tokens_resolve_to_spec_colors(string key, string lightHex, string darkHex)
    {
        AssertBrush(key, ThemeVariant.Light, lightHex);
        AssertBrush(key, ThemeVariant.Dark, darkHex);
    }

    private static void AssertBrush(string key, ThemeVariant variant, string expectedHex)
    {
        Assert.True(Application.Current!.TryGetResource(key, variant, out var res), $"{key} missing for {variant}");
        var brush = Assert.IsAssignableFrom<ISolidColorBrush>(res);
        Assert.Equal(Color.Parse(expectedHex), brush.Color);
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
    [InlineData("IconPlayRegular")]
    [InlineData("IconPauseRegular")]
    [InlineData("IconClockRegular")]
    [InlineData("IconArrowUploadRegular")]
    [InlineData("IconWarningFilled")]
    [InlineData("IconSearchRegular")]
    [InlineData("IconMoreHorizontalRegular")]
    [InlineData("IconTextLineSpacingRegular")]
    [InlineData("IconAppsListRegular")]
    [InlineData("IconGroupListRegular")]
    public void Icon_geometry_resolves(string key)
    {
        Assert.True(Application.Current!.TryGetResource(key, null, out var value), key);
        Assert.IsType<StreamGeometry>(value);
    }
}
