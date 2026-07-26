using Avalonia;
using Avalonia.Headless;
using Lattice.App.Tests;
using Lattice.Tests;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

// Issue #169 — the ubuntu-CI "different thread owns it" flake, and why this assembly
// must never run tests in parallel.
//
// Avalonia's UI-thread identity is a process-global, first-toucher-wins claim:
//   Dispatcher.UIThread  → s_uiThread ?? CurrentDispatcher
//   CurrentDispatcher    → new Dispatcher(null), whose ctor does `s_uiThread ??= this`
//                          and binds it to Thread.CurrentThread
// and EVERY AvaloniaObject claims it implicitly at construction, because AvaloniaObject
// declares `public Dispatcher Dispatcher { get; } = Dispatcher.CurrentDispatcher;`.
//
// Under the default AvaloniaTestIsolationLevel.PerTest, HeadlessUnitTestSession rebuilds
// the application for every [AvaloniaFact]: EnsureIsolatedApplication() calls
// Dispatcher.ResetBeforeUnitTests() — which nulls s_uiThread — and only re-claims it a
// moment later, on the session's dispatch thread, deep inside AppBuilder.SetupUnsafe().
// Any OTHER thread that constructs an AvaloniaObject inside that window wins the claim,
// and the session thread's next Dispatcher.UIThread.VerifyAccess() (ServerCompositor's
// DefaultRenderLoop.Add) throws "The calling thread cannot access this object because a
// different thread owns it" — surfaced by xunit as a Test Case Cleanup Failure on
// whichever test happened to be starting. Upstream has fixed individual instances of this
// shape (AvaloniaUI/Avalonia#12979 for MediaContext) and acknowledges the class in #21770.
//
// xunit's default per-collection parallelism supplies exactly those other threads: this
// assembly's plain [Fact]/[Theory] bodies run on xunit worker threads concurrently with the
// [AvaloniaFact]s on the session thread, and a measured 5 of them constructed Dispatchers
// off-session in a single local run. Serializing the assembly removes the concurrency the
// race needs — no other test thread is ever running while the session rebuilds the app.
// It costs little: the session thread already serializes the 243 [AvaloniaFact]s, so the
// suite was ~115% CPU-bound before this.
//
// Do NOT replace this with a sleep, a retry, or AvaloniaTestIsolationLevel.PerAssembly.
// PerAssembly (the other upstream workaround) would leak Application/Dispatcher state
// across tests — trading a visible flake for invisible false greens — and still leaves the
// claim window open ahead of the first [AvaloniaFact].
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Lattice.App.Tests;

public class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Lattice.App.App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions())
            // Issue #133: the SplitView pane's animated width otherwise makes the host rail's
            // container realization a function of wall-clock time. HeadlessPaneMotion carries
            // the mechanism; NavPaneWidthPolicyTests pins it.
            .WithoutPaneWidthAnimation();
}
