namespace Lattice.Core;

/// <summary>What a <see cref="HostRegistry.Changed"/> event describes.</summary>
public enum RegistryChangeKind
{
    /// <summary>A host was added; <see cref="RegistryChangedEventArgs.Host"/> is the new host.</summary>
    HostAdded,
    /// <summary>A host was updated; <see cref="RegistryChangedEventArgs.Host"/> is the new value.</summary>
    HostUpdated,
    /// <summary>A host was removed; <see cref="RegistryChangedEventArgs.Host"/> is the removed host.</summary>
    HostRemoved,
    /// <summary>The polling interval changed; <see cref="RegistryChangedEventArgs.Host"/> is null.</summary>
    IntervalChanged,
}

/// <summary>Payload of <see cref="HostRegistry.Changed"/>.</summary>
public sealed record RegistryChangedEventArgs(RegistryChangeKind Kind, HostConfig? Host);

/// <summary>
/// The mutable collection of monitored hosts plus the polling interval.
/// Every mutation persists to disk and raises <see cref="Changed"/>.
/// Not thread-safe: mutate from one thread (the UI thread in the app).
/// </summary>
public sealed class HostRegistry
{
    /// <summary>The polling intervals the Settings UI offers (seconds).</summary>
    public static readonly IReadOnlyList<int> AllowedPollingIntervals = LatticeConfig.AllowedPollingIntervals;

    private readonly string _path;
    private LatticeConfig _config;

    /// <summary>Wraps an in-memory config; <paramref name="path"/> is where mutations are saved.</summary>
    public HostRegistry(LatticeConfig config, string path)
    {
        _config = config;
        _path = path;
    }

    /// <summary>Loads the config at <paramref name="path"/> (missing file ⇒ defaults).</summary>
    public static HostRegistry Load(string path) => new(LatticeConfig.Load(path), path);

    /// <summary>The registered hosts, in insertion order.</summary>
    public IReadOnlyList<HostConfig> Hosts => _config.Hosts;

    /// <summary>Steady-state polling interval in seconds.</summary>
    public int PollingIntervalSeconds => _config.PollingIntervalSeconds;

    /// <summary>Whether the relaxed hidden-window polling floor is bypassed (issue #92).</summary>
    public bool FullSpeedHiddenPolling => _config.FullSpeedHiddenPolling;

    /// <summary>Raised after every persisted mutation.</summary>
    public event EventHandler<RegistryChangedEventArgs>? Changed;

    /// <summary>Adds a host. Throws if a host with the same Id is already registered.</summary>
    public void AddHost(HostConfig host)
    {
        if (IndexOf(host.Id) is not null)
            throw new ArgumentException($"A host with id {host.Id} is already registered.", nameof(host));
        Mutate(_config with { Hosts = [.. _config.Hosts, host] }, RegistryChangeKind.HostAdded, host);
    }

    /// <summary>Replaces the host with the same Id. Throws if no such host exists.</summary>
    public void UpdateHost(HostConfig host)
    {
        int index = IndexOf(host.Id)
            ?? throw new ArgumentException($"No host with id {host.Id}.", nameof(host));
        List<HostConfig> hosts = [.. _config.Hosts];
        hosts[index] = host;
        Mutate(_config with { Hosts = hosts }, RegistryChangeKind.HostUpdated, host);
    }

    /// <summary>Removes the host with the given Id. Throws if no such host exists.</summary>
    public void RemoveHost(Guid id)
    {
        int index = IndexOf(id)
            ?? throw new ArgumentException($"No host with id {id}.", nameof(id));
        HostConfig removed = _config.Hosts[index];
        List<HostConfig> hosts = [.. _config.Hosts];
        hosts.RemoveAt(index);
        Mutate(_config with { Hosts = hosts }, RegistryChangeKind.HostRemoved, removed);
    }

    /// <summary>Sets the polling interval. Only <see cref="AllowedPollingIntervals"/> values are accepted.</summary>
    public void SetPollingInterval(int seconds)
    {
        if (!AllowedPollingIntervals.Contains(seconds))
            throw new ArgumentOutOfRangeException(nameof(seconds), seconds,
                "Allowed polling intervals: 2, 5, 10, 30, 60 seconds.");
        Mutate(_config with { PollingIntervalSeconds = seconds }, RegistryChangeKind.IntervalChanged, null);
    }

    /// <summary>
    /// Toggles full-speed polling while the window is hidden (issue #92). Mirrors
    /// <see cref="SetPollingInterval"/> including the no-op-on-equal guard.
    /// </summary>
    public void SetFullSpeedHiddenPolling(bool enabled)
    {
        // Reuse IntervalChanged rather than adding a RegistryChangeKind case: this is a
        // cadence-parameter change like SetPollingInterval, HostMonitorManager recomputes
        // the effective interval via ApplyCadence either way, and no consumer distinguishes
        // the two causes. A new enum member would force the repo-wide exhaustive-switch
        // sweep for zero behavioral gain (plan Part 4).
        if (_config.FullSpeedHiddenPolling == enabled)
            return;
        Mutate(_config with { FullSpeedHiddenPolling = enabled }, RegistryChangeKind.IntervalChanged, null);
    }

    private int? IndexOf(Guid id)
    {
        for (int i = 0; i < _config.Hosts.Count; i++)
            if (_config.Hosts[i].Id == id)
                return i;
        return null;
    }

    private void Mutate(LatticeConfig next, RegistryChangeKind kind, HostConfig? host)
    {
        // Persist before swapping the in-memory state: if Save throws (unwritable
        // directory, full disk), _config must stay at its old value so memory, disk,
        // and every already-connected monitor's config remain consistent. Swapping
        // first would leave memory diverged from disk until the next app start.
        next.Save(_path);
        _config = next;
        Changed?.Invoke(this, new RegistryChangedEventArgs(kind, host));
    }
}
