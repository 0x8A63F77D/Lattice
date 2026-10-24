using SkiaSharp;

namespace Lattice.VisualTests;

/// <summary>
/// A decoded image as straight (unpremultiplied) 8-bit RGBA, row-major, 4 bytes per pixel.
/// The visual gate's metrics (<see cref="GateMetrics"/>, <see cref="RmseMetric"/>) read pixels
/// through this one decoder so they all see the same values.
/// </summary>
/// <remarks>
/// Decoding goes through SkiaSharp (already in the tree via <c>Avalonia.Skia</c>) into a fixed
/// <see cref="SKColorType.Rgba8888"/> / <see cref="SKAlphaType.Unpremul"/> layout with no target
/// color space, so every channel is the value stored in the file: no premultiply round trip and
/// no color conversion. That is what ImageSharp's decoder produced for the gate's 8-bit PNGs
/// before #222 replaced it, which keeps the #82-calibrated thresholds meaningful.
/// </remarks>
internal sealed class PngRaster
{
    PngRaster(int width, int height, byte[] rgba)
    {
        Width = width;
        Height = height;
        Rgba = rgba;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Pixel bytes in R, G, B, A order; pixel (x, y) starts at <c>(y * Width + x) * 4</c>.</summary>
    public byte[] Rgba { get; }

    /// <summary>Decodes an encoded image (PNG in practice). Throws <see cref="InvalidDataException"/> if it cannot.</summary>
    public static PngRaster Decode(byte[] encoded)
    {
        using var data = SKData.CreateCopy(encoded);
        using var codec = SKCodec.Create(data)
            ?? throw new InvalidDataException("Image data is not in a format SkiaSharp can decode.");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var result = codec.GetPixels(info, out var pixels);
        if (result != SKCodecResult.Success)
        {
            throw new InvalidDataException($"Image decode failed: {result}.");
        }

        return new PngRaster(info.Width, info.Height, pixels);
    }

    /// <summary>Wraps raw RGBA bytes (length must be <c>width * height * 4</c>).</summary>
    public static PngRaster FromRgba(int width, int height, byte[] rgba)
    {
        if (rgba.Length != width * height * 4)
        {
            throw new ArgumentException("RGBA buffer length does not match the dimensions.", nameof(rgba));
        }

        return new PngRaster(width, height, rgba);
    }

    public bool HasSameSize(PngRaster other) => Width == other.Width && Height == other.Height;

    /// <summary>Encodes as an opaque PNG (alpha is ignored).</summary>
    public byte[] EncodeOpaquePng()
    {
        var info = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var image = SKImage.FromPixelCopy(info, Rgba)
            ?? throw new InvalidOperationException("Could not wrap the pixel buffer for encoding.");
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
