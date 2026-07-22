using Lattice.App.ViewModels;
using Xunit;

namespace Lattice.App.Tests;

/// <summary>
/// Exhaustive boundary table for the adaptive byte-rate formatter. The unit
/// steps are binary (1024), so every edge is anchored at a power-of-1024 byte
/// count; the promote rule (never render "1024 KB/s") and the "0.#" precision
/// are pinned here so a change to either is a red, not a silent drift.
/// </summary>
public class ByteRateFormatTests
{
    [Theory]
    // --- zero / sub-unit floor ---
    [InlineData(0.0, "0 KB/s")]              // non-positive floors to 0 KB/s
    [InlineData(1.0, "0 KB/s")]              // 1 B/s: below display resolution, reads 0 KB/s (not "—"; caller gates >0)
    [InlineData(512.0, "0.5 KB/s")]          // 512 B/s = half a KB
    // --- KB/s band ---
    [InlineData(1024.0, "1 KB/s")]           // exactly 1 KiB/s
    [InlineData(102912.0, "100.5 KB/s")]     // 100.5 KiB/s — one-decimal, exact (no midpoint ambiguity)
    [InlineData(122880.0, "120 KB/s")]       // 120 KiB/s — the reported bug case (was "0.1 MB/s")
    [InlineData(786432.0, "768 KB/s")]       // 768 KiB/s
    [InlineData(1047552.0, "1023 KB/s")]     // 1023 KiB/s — top of the KB band, stays KB
    // --- KB -> MB promote edge (must never show "1024 KB/s") ---
    [InlineData(1048525.0, "1 MB/s")]        // 1023.96 KiB/s: display would round to 1024 KB, so promote
    [InlineData(1048575.0, "1 MB/s")]        // one byte below 1 MiB: same promotion
    [InlineData(1048576.0, "1 MB/s")]        // exactly 1 MiB/s
    // --- MB/s band ---
    [InlineData(1572864.0, "1.5 MB/s")]      // 1.5 MiB/s
    [InlineData(157286400.0, "150 MB/s")]    // 150 MiB/s
    // --- MB -> GB edge ---
    [InlineData(1073741824.0, "1 GB/s")]     // exactly 1 GiB/s
    [InlineData(1610612736.0, "1.5 GB/s")]   // 1.5 GiB/s
    // --- GB/s is terminal: absurd-large reads as a large GB number ---
    [InlineData(536870912000.0, "500 GB/s")] // 500 GiB/s
    public void Format_picks_adaptive_unit_at_binary_steps(double bytesPerSecond, string expected)
        => Assert.Equal(expected, ByteRateFormat.Format(bytesPerSecond));

    [Fact]
    public void Format_never_renders_1024_of_a_lower_unit()
    {
        // Sweep the whole KB band top: any rate whose KB value would display as
        // 1024 must promote to MB. Guards the promote rule against the classic
        // "1024 KB/s" off-by-one.
        for (double kb = 1023.90; kb < 1025.0; kb += 0.01)
        {
            var s = ByteRateFormat.Format(kb * 1024.0);
            Assert.DoesNotContain("1024 KB/s", s);
        }
    }
}
