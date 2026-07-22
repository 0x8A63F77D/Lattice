using System.Globalization;

namespace Lattice.App.ViewModels;

/// <summary>
/// Pure formatter for a byte-rate (bytes/second) into an adaptive-unit display
/// string — <c>KB/s</c>, <c>MB/s</c> or <c>GB/s</c> on binary 1024 steps,
/// matching the <c>Mb()</c> convention (1 KB = 1024 B) used across the Transfers
/// view. Chosen so a slow transfer reads as <c>120 KB/s</c> instead of the old
/// fixed-MB rendering's <c>0.1 MB/s</c> / <c>0 MB/s</c>.
///
/// Units are Latin literals, identical in every locale (#149 convention: unit
/// symbols are never translated), so they live here as constants rather than as
/// resx entries.
///
/// Precision keeps the existing <c>0.#</c> spirit: one optional decimal. The unit
/// is promoted as soon as the mantissa would <em>render</em> as 1024 (i.e. rounds
/// to ≥ 1024 at one decimal), so the number always reads in [0, 1024) below the
/// top unit and we never print <c>1024 KB/s</c>. <c>GB/s</c> is terminal: an
/// extreme rate reads as a large GB number rather than inventing a TB/s unit.
/// </summary>
public static class ByteRateFormat
{
    private const double Step = 1024.0;
    private const int Decimals = 1;
    private const string Precision = "0.#";
    private static readonly string[] Units = { "KB/s", "MB/s", "GB/s" };

    /// <summary>
    /// Formats a rate in bytes/second. Non-positive input renders as <c>0 KB/s</c>.
    /// </summary>
    public static string Format(double bytesPerSecond)
    {
        // Start in the smallest unit (KB/s): the old MB-only rendering floored
        // sub-MB rates to "0.x"; KB/s is the readable floor.
        double value = Math.Max(bytesPerSecond, 0) / Step;
        int unit = 0;
        // Promote on the value AS IT WILL DISPLAY (rounded to one decimal), not the
        // raw value, so a rate whose KB reading rounds up to 1024 shows "1 MB/s"
        // instead of "1024 KB/s". AwayFromZero mirrors "0.#" rendering at the .x5
        // tie; every promote boundary here is exact (1024.0) so the tie never bites.
        while (unit < Units.Length - 1 &&
               Math.Round(value, Decimals, MidpointRounding.AwayFromZero) >= Step)
        {
            value /= Step;
            unit++;
        }

        return string.Concat(
            value.ToString(Precision, CultureInfo.InvariantCulture), " ", Units[unit]);
    }
}
