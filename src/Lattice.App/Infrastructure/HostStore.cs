using System.Diagnostics;
using Lattice.Core;

namespace Lattice.App.Infrastructure;

/// <summary>One host's UI-facing state. Mutated on the UI thread only.</summary>
public sealed class HostEntry
{
    internal HostEntry(HostConfig config, ConnectionStatus status)
    {
        Config = config;
        Status = status;
    }

    public HostConfig Config { get; internal set; }
    public ConnectionStatus Status { get; internal set; }
    public HostSnapshot? Snapshot { get; internal set; }
}

/// <summary>
/// The single class that touches the event/UI thread boundary. Manager events
/// (background threads) are marshaled via IUiDispatcher.Post; registry events
/// already run on the UI thread (HostRegistry's single-threaded contract — all
/// mutations come from ViewModels). Everything downstream reads UI-thread state.
/// </summary>
public sealed class HostStore : IDisposable
{
    private readonly HostRegistry _registry;
    private readonly HostMonitorManager _manager;
    private readonly IUiDispatcher _dispatcher;
    private readonly List<HostEntry> _hosts = [];
    private bool _disposed;

    public HostStore(HostRegistry registry, HostMonitorManager manager, IUiDispatcher dispatcher)
    {
        _registry = registry;
        _manager = manager;
        _dispatcher = dispatcher;
        foreach (HostConfig host in registry.Hosts)
            _hosts.Add(new HostEntry(host, InitialStatus(host.Id)));
        registry.Changed += OnRegistryChanged;
        manager.StatusChanged += OnStatusChanged;
        manager.SnapshotUpdated += OnSnapshotUpdated;
        manager.MessagesAdded += OnMessagesAdded;
    }

    public IReadOnlyList<HostEntry> Hosts => _hosts;

    /// <summary>Passthrough for the "Polling every {0}s" status text (Tasks view et al).</summary>
    public int PollingIntervalSeconds => _registry.PollingIntervalSeconds;

    /// <summary>Ask the scoped monitor(s) to poll now. Null = all hosts.</summary>
    public void RequestRefresh(Guid? hostId = null)
    {
        foreach (HostMonitor monitor in _manager.Monitors)
            if (hostId is null || monitor.HostId == hostId)
                monitor.RequestRefresh();
    }

    /// <summary>Raised on the UI thread whenever any entry (or the list) changed.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Raised on the UI thread with each host's freshly polled message batch.
    /// Deliberately separate from <see cref="Changed"/>: messages arrive every
    /// poll tick and must not trigger a full rebuild of the snapshot-driven
    /// views. Consumers own retention/dedup (EventLogViewModel's MessageLog —
    /// reconnect replays are deduped there by message identity, so this event
    /// forwards batches verbatim, replay or not).
    /// </summary>
    public event EventHandler<MessagesAddedEventArgs>? MessagesReceived;

    public void Dispose()
    {
        // Runs on the UI thread, as do the queued Post closures below — the
        // flag is therefore a deterministic guard, not a racy one.
        _disposed = true;
        _registry.Changed -= OnRegistryChanged;
        _manager.StatusChanged -= OnStatusChanged;
        _manager.SnapshotUpdated -= OnSnapshotUpdated;
        _manager.MessagesAdded -= OnMessagesAdded;
        Changed = null;
        MessagesReceived = null;
    }

    private static ConnectionStatus InitialStatus(Guid hostId) =>
        new(hostId, HostConnectionState.Disconnected, 0, null, null, null);

    private HostEntry? Find(Guid hostId) => _hosts.FirstOrDefault(h => h.Config.Id == hostId);

    private void OnRegistryChanged(object? sender, RegistryChangedEventArgs e)
    {
        Debug.Assert(_dispatcher.CheckAccess(), "HostRegistry mutations must come from the UI thread.");
        switch (e.Kind)
        {
            case RegistryChangeKind.HostAdded:
                _hosts.Add(new HostEntry(e.Host!, InitialStatus(e.Host!.Id)));
                break;
            case RegistryChangeKind.HostRemoved:
                _hosts.RemoveAll(h => h.Config.Id == e.Host!.Id);
                break;
            case RegistryChangeKind.HostUpdated:
                if (Find(e.Host!.Id) is { } updated)
                    updated.Config = e.Host!;
                break;
            case RegistryChangeKind.IntervalChanged:
                // Settings reads the registry directly; nothing cached here. With no
                // entries there is no "Polling every {0}s" text on screen either, so
                // raising Changed would only trigger a redundant re-render.
                if (_hosts.Count == 0) return;
                break;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnStatusChanged(object? sender, ConnectionStatus status) =>
        _dispatcher.Post(() =>
        {
            if (_disposed) return;
            if (Find(status.HostId) is { } entry)
            {
                entry.Status = status;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        });

    private void OnSnapshotUpdated(object? sender, HostSnapshot snapshot) =>
        _dispatcher.Post(() =>
        {
            if (_disposed) return;
            if (Find(snapshot.HostId) is { } entry)
            {
                entry.Snapshot = snapshot;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        });

    private void OnMessagesAdded(object? sender, MessagesAddedEventArgs e) =>
        _dispatcher.Post(() =>
        {
            if (_disposed) return;
            // Same guard as the status/snapshot paths: a batch can be queued
            // behind the host's removal (or arrive while the removed monitor is
            // still disposing) — forwarding it would resurrect a deleted host's
            // rows after the prune already ran (Codex P2, round 2).
            if (Find(e.HostId) is null) return;
            MessagesReceived?.Invoke(this, e);
        });
}
