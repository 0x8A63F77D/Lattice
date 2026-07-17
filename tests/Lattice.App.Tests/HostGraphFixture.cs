using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;
using Lattice.App.Infrastructure;
using Lattice.App.Tests.Fakes;
using Lattice.Boinc.GuiRpc;
using Lattice.Core;
using Lattice.Tests;
using Microsoft.Extensions.Time.Testing;

namespace Lattice.App.Tests;

/// <summary>
/// Wraps the FakeGuiRpcClient dictionary and hands out a distinct fake per
/// host address, resolved on ConnectAsync — the only call that carries the
/// host identity through the shared, host-agnostic IGuiRpcClient factory.
/// </summary>
internal sealed class RoutingGuiRpcClient(IReadOnlyDictionary<string, FakeGuiRpcClient> fakes) : IGuiRpcClient
{
    private FakeGuiRpcClient? _target;

    public Task ConnectAsync(string host, int port = 31416, CancellationToken ct = default)
    {
        _target = fakes[host];
        return _target.ConnectAsync(host, port, ct);
    }

    public Task<bool> AuthorizeAsync(string password, CancellationToken ct = default) =>
        _target!.AuthorizeAsync(password, ct);

    public Task<VersionInfo> ExchangeVersionsAsync(CancellationToken ct = default) =>
        _target!.ExchangeVersionsAsync(ct);

    public Task<CcState> GetStateAsync(CancellationToken ct = default) =>
        _target!.GetStateAsync(ct);

    public Task<CcStatus> GetCcStatusAsync(CancellationToken ct = default) =>
        _target!.GetCcStatusAsync(ct);

    public Task<IReadOnlyList<Result>> GetResultsAsync(bool activeOnly = false, CancellationToken ct = default) =>
        _target!.GetResultsAsync(activeOnly, ct);

    public Task<IReadOnlyList<Message>> GetMessagesAsync(int seqno = 0, CancellationToken ct = default) =>
        _target!.GetMessagesAsync(seqno, ct);

    public Task<IReadOnlyList<FileTransfer>> GetFileTransfersAsync(CancellationToken ct = default) =>
        _target!.GetFileTransfersAsync(ct);

    public ValueTask DisposeAsync() => _target?.DisposeAsync() ?? ValueTask.CompletedTask;
}

/// <summary>
/// The shared composition-root fixture (issue #67): ONE copy of the
/// registry → fakes → HostMonitorManager → HostStore → clock/ui-state graph
/// that the per-view suites each hand-rolled (~8 near-identical
/// MakeView/InitializeAsync copies), with the determinism discipline built in
/// rather than opted into per suite:
///
/// - The monitor clock is a frozen <see cref="FakeTimeProvider"/> BY
///   CONSTRUCTION (non-optional). Nothing ever auto-elapses: every settle is
///   reached by the immediate first poll on Start, an explicit
///   RequestRefresh/UpdateHost wake, or a fact advancing
///   <see cref="MonitorTime"/> (<see cref="AdvanceUntilAsync"/>).
/// - All manager→store marshalling goes through ONE <see cref="QueueUiDispatcher"/>:
///   posts from background monitor threads only ever execute at an explicit
///   <see cref="Drain"/>/<see cref="Layout"/>/settle on the test's own thread,
///   reproducing the production dispatcher's "never runs until control returns
///   to the UI loop" ordering exactly (see JourneyHarness's class doc for the
///   full hazard analysis). This retires the suites' ImmediateUiDispatcher /
///   LockingUiDispatcher / production-AvaloniaUiDispatcher variants, whose
///   run-posts-on-the-caller's-thread semantics let a background Rebuild
///   interleave with (and clobber) test-thread VM mutations — the
///   ProjectsViewModelTests flake class.
/// - Settles are drain-to-end-state (<see cref="SettleAsync"/>), not a
///   real-time poll with a small wall-clock cap: the old Wait.UntilAsync /
///   HeadlessSync 5 s ceilings false-failed on contended runners (PR #69, CI
///   run 29193683247). The only time bound left is a hang DIAGNOSTIC far above
///   any plausible scheduling delay — see <see cref="HangCeiling"/>.
///
/// Lives in Lattice.App.Tests (not Lattice.TestSupport): the graph is made of
/// Lattice.App types, and TestSupport stays App-free and xunit-free (#16).
/// The fixture itself is xunit-free (plain IAsyncDisposable / IDisposable), so
/// suites use it from both [Fact] and [AvaloniaFact] contexts; only the latter
/// may <see cref="Host"/> a view.
///
/// Teardown comes in two forms because the two suite families differ:
/// IAsyncLifetime VM suites (and any view fact that Start()s the manager) await
/// <see cref="DisposeAsync"/>; synchronous [AvaloniaFact] view facts — which
/// never start the manager, so its monitors' loops are still Task.CompletedTask
/// and tear down without any UI-thread await — use <see cref="Dispose"/>.
/// </summary>
public sealed class HostGraphFixture : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// Hang diagnostic only — NOT a settle mechanism. Exceeding it means the
    /// awaited end state is unreachable (a genuine regression), not a slow
    /// runner: the worst contention observed (40 concurrent test processes on
    /// one dev machine) delayed healthy settles past 5 s, never past seconds
    /// more. Do not lower it back into scheduling-jitter range.
    /// </summary>
    private static readonly TimeSpan HangCeiling = TimeSpan.FromSeconds(60);

    private readonly string _configPath =
        Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}.json");
    private readonly string? _ownedUiStatePath;
    private readonly Dictionary<string, FakeGuiRpcClient> _fakes = [];
    private readonly QueueUiDispatcher _dispatcher = new();
    private Window? _window;

    /// <param name="uiState">
    /// Pass a pre-seeded store to test persisted-preference behaviour (the
    /// suite then owns that file); null creates a fresh temp-file store.
    /// </param>
    public HostGraphFixture(UiStateStore? uiState = null)
    {
        Registry = new HostRegistry(new LatticeConfig(5, []), _configPath);
        MonitorTime = new FakeTimeProvider();
        Manager = new HostMonitorManager(Registry, () => new RoutingGuiRpcClient(_fakes), MonitorTime);
        Store = new HostStore(Registry, Manager, _dispatcher);
        Clock = new ManualUiClock();
        if (uiState is null)
        {
            _ownedUiStatePath = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}-ui.json");
            uiState = new UiStateStore(_ownedUiStatePath);
        }
        UiState = uiState;
        // Shared by sibling VMs, as ShellViewModel wires Tasks/Transfers in
        // production — cross-VM preference facts rely on both funneling
        // through the same DensityPreference/UiStateStore.
        Density = new DensityPreference(UiState);
    }

    public HostRegistry Registry { get; }
    public HostMonitorManager Manager { get; }
    public HostStore Store { get; }
    public ManualUiClock Clock { get; }
    public UiStateStore UiState { get; }
    public DensityPreference Density { get; }

    /// <summary>
    /// The monitor's frozen clock. Facts drive poll/backoff transitions by
    /// advancing it (<see cref="AdvanceUntilAsync"/>) — the deterministic
    /// idiom of the sibling HostMonitorPollingTests. Facts that never advance
    /// it leave the monitor frozen between the immediate first poll and
    /// explicit RequestRefresh/UpdateHost wakes.
    /// </summary>
    public FakeTimeProvider MonitorTime { get; }

    /// <summary>Registers the fake for <paramref name="address"/>, then adds the
    /// host to the registry (persists to the fixture's temp config synchronously).</summary>
    public HostConfig AddHost(string address, FakeGuiRpcClient? fake = null, string? name = null)
    {
        var host = TestData.MakeHostConfig(name: name ?? address, address: address);
        _fakes[address] = fake ?? new FakeGuiRpcClient();
        Registry.AddHost(host);
        return host;
    }

    /// <summary>Passthrough for <see cref="HostMonitorManager.Start"/>.</summary>
    public void Start() => Manager.Start();

    /// <summary>
    /// Executes every queued manager→store post on the calling (test) thread —
    /// the ONLY place they ever run. Use bare Drain when the fact needs store
    /// events delivered without pumping the Avalonia dispatcher (e.g. the
    /// event-log auto-scroll guard fact); everything else goes through
    /// <see cref="Layout"/> or a settle.
    /// </summary>
    public int Drain() => _dispatcher.Drain();

    /// <summary>
    /// Wraps <paramref name="view"/> in the fixture's window. 1280×800 is
    /// ShellWindow's default size; a suite whose facts depend on the width for
    /// their own reasons (Tasks/Transfers responsive breakpoints) documents
    /// that rationale at its call site.
    /// </summary>
    public Window Host(Control view, double width = 1280, double height = 800)
    {
        _window = new Window { Width = width, Height = height, Content = view };
        return _window;
    }

    /// <summary>
    /// Drain → measure/arrange → RunJobs → drain, the JourneyHarness idiom:
    /// realizes the tree the way a real render loop would, delivering any
    /// queued store events on this thread first so the pass observes their
    /// effects, and draining again for posts the layout itself produced.
    /// Requires a <see cref="Host"/>ed view.
    /// </summary>
    public void Layout()
    {
        _dispatcher.Drain();
        HeadlessLayout.Layout(_window ?? throw new InvalidOperationException("No view hosted — call Host() first."));
        _dispatcher.Drain();
    }

    /// <summary>
    /// Drain-to-end-state settle: each iteration delivers all queued posts on
    /// this thread (plus a dispatcher pump when a view is hosted), then checks
    /// the condition. Settle on the EXPECTED END STATE (row count / text /
    /// observed fake calls), never a transient state or a boolean that can go
    /// true early. There is no small real-time ceiling to outwait — only the
    /// <see cref="HangCeiling"/> diagnostic for genuinely unreachable states.
    /// </summary>
    public async Task SettleAsync(Func<bool> condition, string? reason = null)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            Pump();
            if (condition()) return;
            if (sw.Elapsed > HangCeiling)
                throw new TimeoutException(
                    $"End state not reached after {HangCeiling.TotalSeconds:F0} s of drain-pumping"
                    + (reason is null ? "" : $": {reason}")
                    + " — this bound is a hang diagnostic, not a settle ceiling; the state is unreachable.");
            // Yield so background monitor threads can progress and post; the
            // delay has no correctness role (any queued post is delivered by
            // the next Pump), it only schedules the re-check.
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// Repeatedly advances <see cref="MonitorTime"/> by <paramref name="step"/>
    /// until the condition holds, draining between advances. Repeated stepping
    /// is what makes this race-free: a monitor wait entered AFTER one advance
    /// is caught by the next (the Wait.AdvanceUntilAsync idiom, drain-pumped).
    /// </summary>
    public async Task AdvanceUntilAsync(Func<bool> condition, TimeSpan step, string? reason = null)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            Pump();
            if (condition()) return;
            if (sw.Elapsed > HangCeiling)
                throw new TimeoutException(
                    $"End state not reached after {HangCeiling.TotalSeconds:F0} s of fake-time advancing"
                    + (reason is null ? "" : $": {reason}"));
            MonitorTime.Advance(step);
            await Task.Delay(10);
        }
    }

    private void Pump()
    {
        _dispatcher.Drain();
        if (_window is null) return;
        // A hosted view means production code posts to the REAL Avalonia
        // dispatcher too (bindings, ScrollIntoView, guard clears): flush those,
        // then deliver any store events they in turn produced.
        Dispatcher.UIThread.RunJobs();
        _dispatcher.Drain();
    }

    public async ValueTask DisposeAsync()
    {
        Store.Dispose();
        await Manager.DisposeAsync();
        Cleanup();
    }

    /// <summary>
    /// Synchronous teardown for view facts that never started the manager (its
    /// monitors' loops are still Task.CompletedTask, so the DisposeAsync await
    /// completes inline with no UI-thread post to deadlock the GetResult). A
    /// started-manager fact must use <see cref="DisposeAsync"/> instead.
    /// </summary>
    public void Dispose()
    {
        Store.Dispose();
        Manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Cleanup();
    }

    private void Cleanup()
    {
        _window?.Close();
        File.Delete(_configPath);
        if (_ownedUiStatePath is not null) File.Delete(_ownedUiStatePath);
    }
}
