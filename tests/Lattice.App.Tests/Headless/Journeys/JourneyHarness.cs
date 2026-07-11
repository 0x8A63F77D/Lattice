using Avalonia;
using Avalonia.Threading;
using Lattice.App.Infrastructure;
using Lattice.App.Tests.Fakes;
using Lattice.App.ViewModels;
using Lattice.App.Views;
using Lattice.Boinc.GuiRpc;
using Lattice.Core;
using Lattice.Tests;

namespace Lattice.App.Tests.Headless.Journeys;

/// <summary>
/// Builds the exact object graph <see cref="App.OnFrameworkInitializationCompleted"/>
/// wires for the desktop lifetime — LoadRegistryWithFallback → HostMonitorManager →
/// HostStore → clock → UiStateStore → ShellViewModel → ShellWindow — substituting
/// only the client factory (routes each host address to a caller-registered
/// <see cref="FakeGuiRpcClient"/> via <see cref="RoutingGuiRpcClient"/>), a temp
/// config.json path, and a temp ui-state.json path (App.axaml.cs now ALSO builds a
/// UiStateStore and threads it through ShellViewModel — a third substitution beyond
/// the task spec's "exactly two" wording, which predates that wiring; approved
/// deviation).
///
/// PIN: any composition change in App.axaml.cs's OnFrameworkInitializationCompleted
/// MUST be mirrored here in the SAME commit — this class is the composition-root
/// coverage the ledger asked for.
///
/// Two further substitutions match the established idiom of every other headless
/// fixture in this directory (ShellWindowTests, ShellRailTests, AuthFailedLinkageTests,
/// AddHostDialogTests, TasksViewTests) rather than introducing anything new:
/// <see cref="ManualUiClock"/> replaces the real DispatcherUiClock (no timer needed;
/// tests advance time explicitly), and <see cref="QueueUiDispatcher"/> replaces
/// AvaloniaUiDispatcher.Instance.
///
/// QueueUiDispatcher, not the simpler Immediate/LockingUiDispatcher some fixtures
/// use, and this needs spelling out: a real journey both (a) runs a monitor whose
/// manager is ALREADY started when a NEW host is registered (AddHostJourney adds a
/// host through the dialog after Start(), exactly like the real app — every host
/// added after first launch hits this), and (b) sometimes runs two monitors at
/// once. (a) is the sharper hazard: HostRegistry.Changed's subscriber list runs
/// HostMonitorManager's handler BEFORE HostStore's (construction order), so adding
/// a host can start its monitor, which can publish its first StatusChanged and
/// reach HostStore.OnStatusChanged, WHILE the SAME registry-mutation call stack is
/// still unwinding into HostStore.OnRegistryChanged on the ORIGINAL (test) thread.
/// In production this can never race: Dispatcher.UIThread.Post truly enqueues, so
/// the posted continuation cannot run until the UI thread's message loop next
/// pumps — strictly after the current synchronous frame (the registry mutation)
/// returns. LockingUiDispatcher does not reproduce that ordering guarantee: a
/// Post from a DIFFERENT thread runs immediately once it wins the lock, with no
/// requirement to wait for the calling thread's frame to finish, so it can still
/// interleave with the tail of that frame — which manifested as ReconcileHosts
/// inserting the SAME host's HostRailItemViewModel twice (observed as
/// System.Linq's MoreThanOneMatchException, reproduced only when the full test
/// project ran, i.e. under real scheduling pressure). QueueUiDispatcher.Post only
/// enqueues (thread-safe, never executes); every queued action is executed later,
/// single-threaded, only when Drain() is called — from <see cref="Layout"/> and
/// <see cref="SettleAsync"/> here, always on the test's own thread — which
/// reproduces the real dispatcher's "never runs until control returns to the UI
/// loop" guarantee exactly, for both hazards (a) and (b).
/// </summary>
internal sealed class JourneyHarness : IAsyncDisposable
{
    private readonly Dictionary<string, FakeGuiRpcClient> _fakes = [];
    private readonly Func<IGuiRpcClient> _factory;
    private readonly QueueUiDispatcher _dispatcher = new();

    public string ConfigPath { get; }
    public string UiStatePath { get; }
    public HostRegistry Registry { get; }
    public HostMonitorManager Manager { get; }
    public HostStore Store { get; }
    public ManualUiClock Clock { get; }
    public UiStateStore UiState { get; }
    public ShellViewModel Shell { get; }
    public ShellWindow Window { get; }

    /// <param name="configPath">
    /// Pass an existing path to reopen the same on-disk config (PersistenceJourney);
    /// null generates a fresh temp path (every other journey).
    /// </param>
    public JourneyHarness(string? configPath = null)
    {
        ConfigPath = configPath ?? Path.Combine(Path.GetTempPath(), $"lattice-journey-{Guid.NewGuid():N}.json");
        UiStatePath = Path.Combine(
            Path.GetTempPath(), $"{Path.GetFileNameWithoutExtension(ConfigPath)}-ui.json");

        // Mirrors App.OnFrameworkInitializationCompleted line for line (see class doc
        // for the three approved substitutions).
        Registry = App.LoadRegistryWithFallback(ConfigPath);
        _factory = () => new RoutingGuiRpcClient(_fakes);
        Manager = new HostMonitorManager(Registry, _factory, TimeProvider.System);
        Store = new HostStore(Registry, Manager, _dispatcher);
        Clock = new ManualUiClock();
        UiState = new UiStateStore(UiStatePath);
        Shell = new ShellViewModel(Registry, Store, Clock, UiState, _factory);
        Window = new ShellWindow { DataContext = Shell };
    }

    /// <summary>
    /// Registers (or replaces) the fake a subsequent ConnectAsync for this address
    /// resolves to (RoutingGuiRpcClient looks the address up lazily on ConnectAsync
    /// — the only call carrying host identity through the shared, host-agnostic
    /// factory). Must land before the host's monitor next attempts to connect: before
    /// AddHost/Start for a fresh host, or before RequestRefresh when swapping a live
    /// host's fake mid-journey (PartialResultsJourney).
    /// </summary>
    public void RegisterFake(string address, FakeGuiRpcClient fake) => _fakes[address] = fake;

    /// <summary>The fake currently registered for an address (throws if none was registered).</summary>
    public FakeGuiRpcClient ClientFor(string address) => _fakes[address];

    /// <summary>Registers the fake for <paramref name="address"/>, then adds the host
    /// to the registry (persists to <see cref="ConfigPath"/> synchronously).</summary>
    public HostConfig AddHost(string address, FakeGuiRpcClient fake, string? name = null, string password = "pw")
    {
        RegisterFake(address, fake);
        HostConfig host = TestData.MakeHostConfig(name: name ?? address, address: address, password: password);
        Registry.AddHost(host);
        return host;
    }

    /// <summary>Passthrough for <see cref="HostMonitorManager.Start"/>.</summary>
    public void Start() => Manager.Start();

    /// <summary>
    /// Headless Show() does not run a full layout pass, so the NavigationView's
    /// PaneCustomContent presenter (which hosts the rail) and page content stay
    /// unrealized until measured — a single measure/arrange realizes the tree,
    /// matching what a real render loop does at startup (precedent: every fixture's
    /// private Layout() helper in this directory). Also drains any HostStore events
    /// background monitors posted since the last drain (see class doc), so a
    /// Layout() call right after a synchronous mutation observes its effects.
    /// </summary>
    public void Layout()
    {
        _dispatcher.Drain();
        Window.Measure(new Size(Window.Width, Window.Height));
        Window.Arrange(new Rect(0, 0, Window.Width, Window.Height));
        Dispatcher.UIThread.RunJobs();
        _dispatcher.Drain();
    }

    /// <summary>Condition-driven wait (never a fixed sleep — transient states like
    /// Retrying race) delegating to <see cref="HeadlessSync.WaitUntilAsync"/>; the
    /// reason is folded into the timeout exception so a failing journey names the
    /// step it never reached. Every poll first drains the queued HostStore events
    /// (see class doc) so background monitor progress becomes visible one step at
    /// a time, never interleaved with whatever the test thread is doing. Default
    /// ceiling is more generous than plain HeadlessSync's (10s vs 5s): a journey
    /// builds the FULL composition root (real background HostMonitor loops,
    /// sometimes two at once) rather than one narrow VM/view slice, so it is more
    /// exposed to scheduling contention when every other test class's AvaloniaFact
    /// runs concurrently in the same process — this only widens the ceiling for a
    /// genuinely hung condition, per HeadlessSync's own doc.</summary>
    public async Task SettleAsync(Func<bool> condition, string reason, int timeoutMs = 10000)
    {
        bool DrainThenCheck()
        {
            _dispatcher.Drain();
            return condition();
        }

        try
        {
            await HeadlessSync.WaitUntilAsync(DrainThenCheck, timeoutMs);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"{reason} ({ex.Message})", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Shell.Dispose();
        Store.Dispose();
        await Manager.DisposeAsync();
        // ManualUiClock is not IDisposable (unlike the production DispatcherUiClock
        // it substitutes) — nothing to dispose there.
        Window.Close();
    }
}
