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

    /// <summary>Creates monitors for every registered host and subscribes to registry changes.</summary>
    public HostMonitorManager(HostRegistry registry, Func<IGuiRpcClient> clientFactory, TimeProvider timeProvider)
    {
        _registry = registry;
        _clientFactory = clientFactory;
        _time = timeProvider;
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

    private HostMonitor CreateMonitor(HostConfig host)
    {
        var monitor = new HostMonitor(host, _clientFactory, _time, _registry.PollingIntervalSeconds);
        monitor.StatusChanged += (_, s) => StatusChanged?.Invoke(this, s);
        monitor.SnapshotUpdated += (_, s) => SnapshotUpdated?.Invoke(this, s);
        monitor.MessagesAdded += (_, m) => MessagesAdded?.Invoke(this, m);
        _monitors[host.Id] = monitor;
        return monitor;
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
                lock (_gate)
                    foreach (HostMonitor monitor in _monitors.Values)
                        monitor.SetPollingInterval(_registry.PollingIntervalSeconds);
                break;
        }
    }
}
