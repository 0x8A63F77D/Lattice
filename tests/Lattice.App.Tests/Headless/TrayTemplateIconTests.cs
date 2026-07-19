using System.Buffers.Binary;
using Avalonia.Headless.XUnit;
using Avalonia.Platform;
using Lattice.App.Infrastructure;
using SkiaSharp;
using Xunit;

namespace Lattice.App.Tests.Headless;

/// <summary>
/// Guards the macOS tray template packaging (issue #108): the monochrome glyph ships
/// as an Avalonia asset (csproj <c>AvaloniaResource ... Link="Assets/latticeTrayTemplate@2x.png"</c>)
/// at the exact URI the tray policy hands the controller. A broken Link path, a removed
/// asset, or a URI typo would fail loudly here instead of silently shipping a blank
/// menu-bar mark on macOS. The rendered light/dark tinting is an owner-eye gate on
/// hardware — asset presence and resolution are what the machine can pin.
/// </summary>
public class TrayTemplateIconTests
{
    [AvaloniaFact]
    public void Template_asset_is_embedded_at_the_uri_the_policy_selects()
    {
        // The policy's macOS URI must resolve to a real embedded resource — this ties
        // the string constant to the csproj Link so the two cannot drift apart.
        var uri = new Uri(TrayIconAssetPolicy.MacTemplateIconUri);

        Assert.True(AssetLoader.Exists(uri));
        using Stream stream = AssetLoader.Open(uri);
        Assert.True(stream.Length > 0);
    }

    [AvaloniaFact]
    public void Embedded_template_is_the_36px_at2x_master()
    {
        // Avalonia's macOS tray path feeds AppKit a single bitmap, so the crisp-Retina
        // choice is the 36 px master (see TrayIconAssetPolicy remarks). Asserting the
        // pixel size catches accidentally wiring the 18 px @1x file, which would render
        // blurry on Retina menu bars. Read straight from the PNG IHDR header (offsets
        // 16/20, big-endian) rather than decoding a Bitmap: the non-Skia headless
        // backend stubs image decode to 1x1, so a real decode can't measure this — the
        // header bytes are the backend-independent source of truth for the embedded file.
        using Stream stream = AssetLoader.Open(new Uri(TrayIconAssetPolicy.MacTemplateIconUri));
        var header = new byte[24];
        Assert.Equal(header.Length, stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false));

        int width = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16, 4));
        int height = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20, 4));

        Assert.Equal(36, width);
        Assert.Equal(36, height);
    }

    [AvaloniaFact]
    public void Embedded_template_actually_carries_the_glyph()
    {
        // Regression gate for the broken-export bug (#108 owner catch): the design tool's
        // first PNG export mis-rasterized the mask+opacity+rotate SVG and shipped a near-
        // empty raster — only 59 of 1296 pixels had any alpha and NONE reached full opacity,
        // so the menu bar showed a lone fragment instead of the woven mark. Dimensions alone
        // (36x36) did not catch it; the correct file is regenerated from the SVG master.
        //
        // Decode real pixels with SkiaSharp (bundled transitively via Avalonia.Skia; native
        // libs ship for every CI RID and decode is deterministic across platforms) rather
        // than an Avalonia Bitmap, whose non-Skia headless decode stubs to 1x1. Assert the
        // two properties the broken export violated: substantial ink coverage, and at least
        // some fully-opaque pixels — the design's 100% center accent strand MUST survive
        // rasterization. A blank or fragmentary re-export reddens this.
        using Stream stream = AssetLoader.Open(new Uri(TrayIconAssetPolicy.MacTemplateIconUri));
        using SKBitmap bitmap = SKBitmap.Decode(stream);
        Assert.NotNull(bitmap);

        int inkPixels = 0;
        int fullyOpaquePixels = 0;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                byte alpha = bitmap.GetPixel(x, y).Alpha;
                if (alpha > 0) inkPixels++;
                if (alpha == 255) fullyOpaquePixels++;
            }
        }

        // Broken export: inkPixels=59, fullyOpaque=0. Correct SVG regen: 486 and 62.
        // Floors sit well between the two so a fragmentary export fails and faithful
        // re-exports (minor antialiasing drift) still pass.
        Assert.True(inkPixels >= 150, $"template has too little ink ({inkPixels} px) — likely a broken raster");
        Assert.True(fullyOpaquePixels >= 10, $"template lost its 100% center strand ({fullyOpaquePixels} full-opacity px)");
    }
}
