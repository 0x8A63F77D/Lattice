using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Lattice.VisualTests;

internal static class RmseMetric
{
    /// <summary>
    /// Root-mean-square error over the RGB channels, normalized to 0..1 (0 = identical).
    /// This is the ImageMagick-style RMSE metric Avalonia's own render tests use for their
    /// tolerance gate, and the number the #82 calibration is expressed in. Returns NaN if
    /// the images differ in size.
    /// </summary>
    public static double Rmse(byte[] pngA, byte[] pngB)
    {
        using var a = Image.Load<Rgb24>(pngA);
        using var b = Image.Load<Rgb24>(pngB);
        if (a.Width != b.Width || a.Height != b.Height)
        {
            return double.NaN;
        }

        double sumSq = 0;
        for (var y = 0; y < a.Height; y++)
        {
            for (var x = 0; x < a.Width; x++)
            {
                var pa = a[x, y];
                var pb = b[x, y];
                int dr = pa.R - pb.R;
                int dg = pa.G - pb.G;
                int db = pa.B - pb.B;
                sumSq += (dr * dr) + (dg * dg) + (db * db);
            }
        }

        var mse = sumSq / (a.Width * (double)a.Height * 3.0);
        return Math.Sqrt(mse) / 255.0;
    }
}
