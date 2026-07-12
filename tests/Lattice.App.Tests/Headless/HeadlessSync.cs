using System.Diagnostics;
using Avalonia.Threading;

namespace Lattice.App.Tests.Headless;

/// <summary>
/// Condition-driven dispatcher pumping for dialog transitions. FAContentDialog's
/// close path awaits a real Task.Delay(200) (FinalCloseDialog), whose continuation
/// lands whenever the OS timer fires — fixed-sleep settles ("wait 300 ms, then
/// RunJobs") flake on loaded CI runners (macOS leg, Confirming_remove, 2026-07-10).
/// Poll the test's observable outcome instead; the generous ceiling only bounds
/// a genuinely hung dialog.
///
/// Responsibility split (don't invent a third helper): this is the dispatcher-pumped
/// UI end-state poll — it RunJobs()-pumps the Avalonia dispatcher between polls. The
/// non-dispatcher, background-loop equivalent is <c>Lattice.Tests.Wait.UntilAsync</c>,
/// which never touches the UI dispatcher.
/// </summary>
internal static class HeadlessSync
{
    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var stopwatch = Stopwatch.StartNew();
        Dispatcher.UIThread.RunJobs();
        while (!condition())
        {
            if (stopwatch.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException($"Condition not reached after {timeoutMs} ms of dispatcher pumping.");
            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }
    }
}
