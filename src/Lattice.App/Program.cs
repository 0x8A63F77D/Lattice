using System.Diagnostics;
using Avalonia;
using Lattice.App.Infrastructure;

namespace Lattice.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Single-instance guard (#92 Q3, invariant I-GUARD). Run before Avalonia init:
        // the decision is process-level and must precede any window/config work.
        switch (SingleInstanceGuard.TryAcquire(
            SingleInstanceGuard.DefaultLockPath, SingleInstanceGuard.DefaultPipeName))
        {
            case AcquireResult.Acquired acquired:
                // We are the primary. The activation listener is already live (started
                // inside TryAcquire, R5) with a no-op callback; App swaps in the real
                // show-window callback once its window exists, and disposes the guard
                // on Exit.
                App.ActivationGuard = acquired.Guard;
                break;

            case AcquireResult.Contended:
                // Probable existing instance — SignalPrimary is the liveness oracle
                // (I-GUARD). A live primary answered ⇒ it surfaced its window, so this
                // launch is done (the ONLY path that ends a launch). No answer ⇒ the
                // contention is refuted (stale-looking lock, foreign holder, broken
                // pipe) ⇒ launch anyway without the guard.
                if (SingleInstanceGuard.SignalPrimary(SingleInstanceGuard.DefaultPipeName))
                    return 0;
                Trace.TraceWarning(
                    "Lattice: instance lock is held but no primary answered the activation ping; launching without the guard.");
                break;

            case AcquireResult.Unavailable:
                // The guard cannot operate (unusable lock path / permissions). Fail-open:
                // a broken lock file must never brick a monitoring app. Never exit.
                Trace.TraceWarning(
                    "Lattice: single-instance guard unavailable; launching without it.");
                break;
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Also used by previewer tooling; keep the signature.</summary>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure<App>()
        .UsePlatformDetect()
        .LogToTrace();
}
