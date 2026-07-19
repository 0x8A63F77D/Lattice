using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;

namespace Lattice.VisualTests;

/// <summary>
/// A CPU-side snapshot of a captured Skia frame for pixel probing. Wraps the
/// <see cref="Bitmap.CopyPixels"/> dance that the non-env-gated pixel gates in this project each
/// used to re-derive, so the stride / channel-order arithmetic lives in one place. Read-only;
/// sample with <see cref="Rgb"/> or <see cref="Sample"/>. Build on the Avalonia UI thread
/// (CopyPixels touches the bitmap).
/// </summary>
internal sealed class PixelBuffer
{
    private readonly byte[] _px;
    private readonly int _stride;
    private readonly bool _bgra;

    private PixelBuffer(byte[] px, int width, int height, int stride, bool bgra)
    {
        _px = px;
        Width = width;
        Height = height;
        _stride = stride;
        _bgra = bgra;
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>
    /// Copies <paramref name="bmp"/>'s pixels into a managed buffer, recording the channel order
    /// from the bitmap's own <see cref="PixelFormat"/>. The order must be read from the frame, not
    /// assumed: the headless Skia capture is Rgba8888 (verified on macOS-arm64), and hardcoding
    /// Bgra8888 silently swaps R/B — invisible for grey token colours but wrong for chromatic ones
    /// (e.g. the selection tint).
    /// </summary>
    public static PixelBuffer From(Bitmap bmp)
    {
        var size = bmp.PixelSize;
        int stride = size.Width * 4;
        var buf = new byte[stride * size.Height];
        var handle = Marshal.AllocHGlobal(buf.Length);
        try
        {
            bmp.CopyPixels(new PixelRect(0, 0, size.Width, size.Height), handle, buf.Length, stride);
            Marshal.Copy(handle, buf, 0, buf.Length);
        }
        finally
        {
            Marshal.FreeHGlobal(handle);
        }

        var format = (bmp as WriteableBitmap)?.Format
            ?? throw new InvalidOperationException("Captured frame exposes no pixel format to read channel order from.");
        bool bgra = format == PixelFormats.Bgra8888;
        if (!bgra && format != PixelFormats.Rgba8888)
            throw new InvalidOperationException($"Unsupported capture pixel format '{format}' — expected Rgba8888 or Bgra8888.");
        return new PixelBuffer(buf, size.Width, size.Height, stride, bgra);
    }

    /// <summary>The (R, G, B) at device pixel (x, y), mapped from the frame's actual channel order.</summary>
    public (int r, int g, int b) Rgb(int x, int y)
    {
        int i = y * _stride + x * 4;
        return _bgra
            ? (_px[i + 2], _px[i + 1], _px[i + 0])
            : (_px[i + 0], _px[i + 1], _px[i + 2]);
    }

    /// <summary>
    /// Samples the colour under a point given in <paramref name="element"/>'s local
    /// coordinates, translated into <paramref name="root"/> (the captured window's) space.
    /// </summary>
    public (int r, int g, int b) Sample(Visual root, Visual element, double localX, double localY)
    {
        var p = element.TranslatePoint(new Point(localX, localY), root)!.Value;
        return Rgb((int)Math.Round(p.X), (int)Math.Round(p.Y));
    }
}

/// <summary>
/// Shared colour-assertion vocabulary for the non-env-gated pixel gates. These gates assert solid,
/// AA-free region colours (not glyph-exact pixels), so they are stable across the ubuntu/windows/
/// macOS CI renderers and run in the normal <c>dotnet test</c> (ci.yml). The durable split in this
/// project is gated screenshot BASELINES (<c>LATTICE_RUN_VISUAL_TESTS</c>) vs. non-gated CI-blocking
/// assertion probes like these — NOT which test project the file lives in.
/// </summary>
internal static class PixelProbe
{
    /// <summary>Solid fills render exact; 8 absorbs minor rounding across renderers.</summary>
    public const int Tolerance = 8;

    /// <summary>True when the sampled colour is within <see cref="Tolerance"/> (L1) of <paramref name="expected"/>.</summary>
    public static bool Near((int r, int g, int b) sample, Color expected) =>
        Math.Abs(sample.r - expected.R) + Math.Abs(sample.g - expected.G) + Math.Abs(sample.b - expected.B) <= Tolerance;

    public static string Hex((int r, int g, int b) c) => $"#{c.r:X2}{c.g:X2}{c.b:X2}";

    /// <summary>Resolves a token brush's <see cref="Color"/> for the window's active theme variant.</summary>
    public static Color Resolve(Window window, string key) =>
        window.TryFindResource(key, window.ActualThemeVariant, out var value) && value is ISolidColorBrush brush
            ? brush.Color
            : throw new InvalidOperationException($"Resource '{key}' is not a SolidColorBrush.");
}
