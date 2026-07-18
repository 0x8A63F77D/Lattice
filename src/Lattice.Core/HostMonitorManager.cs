using Lattice.Boinc.GuiRpc;

namespace Lattice.Core;

/// <summary>Outcome of a one-shot connection test (Settings "Test connection" / Add-host dialog).</summary>
public sealed record TestConnectionResult(bool Success, string? Error, VersionInfo? Version);

/// <summary>
/// Composition root of the Core layer: maps registry entries to <see cref="HostMonitor"/>s,
/// follows registry changes, and re-raises the per-host events on a single surface.
/// </summary>
public sealed class HostMonitorManager : IAsyncDisposable
{
    private readonly HostRegistry _registry;
    private readonly Func<IGuiRpcClient> _clientFactory;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, HostMonitor> _monitors = [];
    private bool _started;
    // The window's visibility, the sole tray-residency cadence input (issue #92).
    // Guarded by _gate alongside _monitors; defaults visible (the app boots with the
    // window shown). Monitors never see this — only the effective interval it produces.
    private bool _windowVisible = true;
    // The effective interval last pushed to monitors (I-CAD's tracked value). Lets
    // ApplyCadence skip when the effective interval is unchanged, so a visibility flip or
    // full-speed toggle that does not move the interval causes no work — and, critically,
    // no wake. Seeded in the constructor to the interval new monitors are created with.
    private int _appliedInterval;

    /// <summary>Creates monitors for every registered host and subscribes to registry changes.</summary>
    public HostMonitorManager(HostRegistry registry, Func<IGuiRpcClient> clientFactory, TimeProvider timeProvider)
    {
        _registry = registry;
        _clientFactory = clientFactory;
        _time = timeProvider;
        _appliedInterval = CurrentEffectiveInterval();
        foreach (HostConfig host in registry.Hosts)
            CreateMonitor(host);
        registry.Changed += OnRegistryChanged;
    }

    /// <summary>The live monitors, one per registered host.</summary>
    public IReadOnlyList<HostMonitor> Monitors
    {
        get { lock (_gate) return [.. _monitors.Values]; }
    }

    /// <summary>Re-raised from every monitor; args carry the HostId.</summary>
    public event EventHandler<ConnectionStatus>? StatusChanged;

    /// <summary>Re-raised from every monitor; args carry the HostId.</summary>
    public event EventHandler<HostSnapshot>? SnapshotUpdated;

    /// <summary>Re-raised from every monitor; args carry the HostId.</summary>
    public event EventHandler<MessagesAddedEventArgs>? MessagesAdded;

    /// <summary>Starts all monitors; hosts added later start automatically.</summary>
    public void Start()
    {
        lock (_gate)
        {
            _started = true;
            foreach (HostMonitor monitor in _monitors.Values)
                monitor.Start();
        }
    }

    /// <summary>
    /// Records whether the main window is visible and re-applies the resulting cadence
    /// (issue #92). Hiding relaxes each host to the floor; showing restores the
    /// configured interval. Idempotent — a no-op when the state is unchanged.
    /// </summary>
    public void SetWindowVisible(bool visible)
    {
        lock (_gate)
        {
            if (_windowVisible == visible)
                return;
            _windowVisible = visible;
            ApplyCadence();
        }
    }

    /// <summary>
    /// Wakes every monitor for an immediate poll tick (the window-restore refresh burst,
    /// issue #92). Fire-and-forget: each <see cref="HostMonitor.RequestRefresh"/> only
    /// wakes the poll loop; it does not block on the RPC round-trip.
    /// </summary>
    public void RequestRefreshAll()
    {
        lock (_gate)
            foreach (HostMonitor monitor in _monitors.Values)
                monitor.RequestRefresh();
    }

    /// <summary>
    /// One-shot connect + auth + exchange_versions against a candidate config.
    /// Independent of the running monitors; never throws (cancellation excepted).
    /// </summary>
    public static async Task<TestConnectionResult> TestConnectionAsync(
        HostConfig config, Func<IGuiRpcClient> clientFactory, CancellationToken ct = default)
    {
        try
        {
            await using IGuiRpcClient client = clientFactory();
            await client.ConnectAsync(config.Address, config.Port, ct).ConfigureAwait(false);
            if (config.Password.Length > 0
                && !await client.AuthorizeAsync(config.Password, ct).ConfigureAwait(false))
                return new TestConnectionResult(false, "The host refused the password.", null);
            VersionInfo version = await client.ExchangeVersionsAsync(ct).ConfigureAwait(false);
            return new TestConnectionResult(true, null, version);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new TestConnectionResult(false, ex.Message, null);
        }
    }

    /// <summary>Unsubscribes from the registry and disposes every monitor.</summary>
    public async ValueTask DisposeAsync()
    {
        _registry.Changed -= OnRegistryChanged;
        List<HostMonitor> monitors;
        lock (_gate)
        {
            monitors = [.. _monitors.Values];
            _monitors.Clear();
        }
        foreach (HostMonitor monitor in monitors)
            await monitor.DisposeAsync().ConfigureAwait(false);
    }

    // The effective interval right now, given the configured interval, window visibility,
    // and the full-speed flag (issue #92). Reads _windowVisible: safe because every call
    // site holds _gate or runs single-threaded before any monitor exists (the constructor).
    private int CurrentEffectiveInterval() =>
        PollingCadencePolicy.EffectiveIntervalSeconds(
            _registry.PollingIntervalSeconds, _windowVisible, _registry.FullSpeedHiddenPolling);

    // Seeds new monitors with the EFFECTIVE (not raw) interval so hosts added while the
    // window is hidden start relaxed, satisfying I-CAD on creation (issue #92).
    private HostMonitor CreateMonitor(HostConfig host)
    {
        var monitor = new HostMonitor(host, _clientFactory, _time, CurrentEffectiveInterval());
        monitor.StatusChanged += (_, s) => StatusChanged?.Invoke(this, s);
        monitor.SnapshotUpdated += (_, s) => SnapshotUpdated?.Invoke(this, s);
        monitor.MessagesAdded += (_, m) => MessagesAdded?.Invoke(this, m);
        _monitors[host.Id] = monitor;
        return monitor;
    }

    /// <summary>
    /// The single cadence recompute funnel (issue #92). Invariant I-CAD: at every instant
    /// after any of {monitor created, visibility changed, IntervalChanged raised}, every
    /// live monitor's active interval equals
    /// EffectiveIntervalSeconds(registry.PollingIntervalSeconds, visible, fullSpeedHidden).
    /// The three triggers may not call SetPollingInterval directly — they funnel here.
    /// Caller must hold _gate.
    ///
    /// Uses the QUIET (non-waking) setter: a cadence change takes effect at each monitor's
    /// NEXT wait boundary, never interrupting an in-progress backoff or poll wait. Waking a
    /// Retrying host on hide would cut its exponential backoff short and increase reconnect
    /// churn (Codex #105 P2); immediate refresh is the sole job of RequestRefreshAll. The
    /// no-op-on-equal guard means an interval-preserving flip (e.g. hiding a 60s host, or
    /// toggling full-speed while visible) does no work at all.
    /// </summary>
    private void ApplyCadence()
    {
        int effective = CurrentEffectiveInterval();
        if (effective == _appliedInterval)
            return;
        _appliedInterval = effective;
        foreach (HostMonitor monitor in _monitors.Values)
            monitor.SetPollingIntervalQuiet(effective);
    }

    private void OnRegistryChanged(object? sender, RegistryChangedEventArgs e)
    {
        switch (e.Kind)
        {
            case RegistryChangeKind.HostAdded:
                lock (_gate)
                {
                    HostMonitor added = CreateMonitor(e.Host!);
                    if (_started)
                        added.Start();
                }
                break;

            case RegistryChangeKind.HostRemoved:
                HostMonitor? removed;
                lock (_gate)
                    _monitors.Remove(e.Host!.Id, out removed);
                if (removed is not null)
                    // Fire-and-forget teardown: the registry mutation must not block on
                    // the monitor's shutdown. Core has no logging facility, so there is
                    // nowhere useful to report a failure — but the continuation still
                    // observes .Exception so a future change to HostMonitor.DisposeAsync
                    // can never surface as an unobserved task exception (which crashes
                    // the process on GC when TaskScheduler.UnobservedTaskException isn't
                    // suppressed).
                    removed.DisposeAsync().AsTask()
                        .ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                break;

            case RegistryChangeKind.HostUpdated:
                lock (_gate)
                    if (_monitors.TryGetValue(e.Host!.Id, out HostMonitor? monitor))
                        monitor.UpdateConfig(e.Host);
                break;

            case RegistryChangeKind.IntervalChanged:
                // Funnels through ApplyCadence so a raw interval change is floored while
                // hidden exactly like a visibility flip (I-CAD). This case also fires for
                // SetFullSpeedHiddenPolling, which reuses IntervalChanged (HostRegistry).
                lock (_gate)
                    ApplyCadence();
                break;
        }
    }
}
