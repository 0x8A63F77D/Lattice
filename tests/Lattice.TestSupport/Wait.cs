using System.Diagnostics;
using Microsoft.Extensions.Time.Testing;

namespace Lattice.Tests;

/// <summary>Bounded real-time polling helpers for asserting on background-loop state.</summary>
public static class Wait
{
    /// <summary>Polls until the condition holds; fails the test after 5 seconds.</summary>
    public static async Task UntilAsync(Func<bool> condition, string? because = null)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > 5000)
                throw new TimeoutException($"Timed out waiting for condition{(because is null ? "" : $": {because}")}");
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// Repeatedly advances fake time by <paramref name="step"/> until the condition holds.
    /// Repeated stepping is what makes this race-free: a timer registered after an
    /// advance is caught by the next advance.
    /// </summary>
    public static async Task AdvanceUntilAsync(FakeTimeProvider time, Func<bool> condition, TimeSpan step)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > 5000)
                throw new TimeoutException("Timed out advancing fake time");
            time.Advance(step);
            await Task.Delay(10);
        }
    }
}
