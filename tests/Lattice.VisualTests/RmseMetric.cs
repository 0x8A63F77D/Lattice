namespace Lattice.VisualTests;

internal static class RmseMetric
{
    /// <summary>
    /// Root-mean-square error over the RGB channels (alpha ignored), normalized to 0..1
    /// (0 = identical). This is the ImageMagick-style RMSE metric Avalonia's own render tests use
    /// for their tolerance gate, and the number the #82 calibration is expressed in. Returns NaN
    /// if the images differ in size.
    /// </summary>
    public static double Rmse(byte[] pngA, byte[] pngB)
    {
        var a = PngRaster.Decode(pngA);
        var b = PngRaster.Decode(pngB);
        if (!a.HasSameSize(b))
        {
            return double.NaN;
        }

        double sumSq = 0;
        for (var i = 0; i < a.Rgba.Length; i += 4)
        {
            int dr = a.Rgba[i] - b.Rgba[i];
            int dg = a.Rgba[i + 1] - b.Rgba[i + 1];
            int db = a.Rgba[i + 2] - b.Rgba[i + 2];
            sumSq += (dr * dr) + (dg * dg) + (db * db);
        }

        var mse = sumSq / (a.Width * (double)a.Height * 3.0);
        return Math.Sqrt(mse) / 255.0;
    }
}
