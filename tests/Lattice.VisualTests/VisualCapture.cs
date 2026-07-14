using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Styling;

namespace Lattice.VisualTests;

internal static class VisualCapture
{
    /// <summary>
    /// Replicates Verify.Avalonia's <c>ControlToImage</c> capture so the calibration
    /// harness measures exactly what the snapshot gate captures: wrap the control in a
    /// size-to-content window, flip the app theme, capture the Skia frame, encode PNG.
    /// The captured <c>WriteableBitmap</c> is disposed after Save (Avalonia #19611
    /// libSkiaSharp segfault otherwise). Must be called on the Avalonia UI thread.
    /// </summary>
    public static byte[] CapturePng(Func<Control> factory, ThemeVariant variant)
    {
        // Set the theme BEFORE showing so the window is born in the target variant (no
        // Light→Dark flip mid-lifetime), then render on a fresh window. Combined with
        // VisualWarmup this makes captures deterministic across themes and test order.
        Application.Current!.RequestedThemeVariant = variant;
        var window = new Window
        {
            Content = factory(),
            SizeToContent = SizeToContent.WidthAndHeight,
        };
        window.Show();
        try
        {
            using var frame = window.CaptureRenderedFrame()
                ?? throw new InvalidOperationException("No rendered frame captured.");
            using var stream = new MemoryStream();
            frame.Save(stream, PngBitmapEncoderOptions.Default);
            return stream.ToArray();
        }
        finally
        {
            window.Close();
        }
    }
}
