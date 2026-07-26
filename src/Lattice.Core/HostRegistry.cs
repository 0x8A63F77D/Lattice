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
/// <para>
/// <b>Publication protocol.</b> WRITES are single-threaded: mutate from one thread only
/// (the UI thread in the app) — two concurrent mutators would lose one another's edit,
/// because each builds its next state from the <c>_config</c> it read (read-modify-write
/// with no lock). READS are free from any thread and need no lock, which is what
/// <c>HostControlService.FindConfig</c> relies on when it walks <see cref="Hosts"/> on a
/// thread-pool lane. Three facts make that safe, and all three must hold together:
/// </para>
/// <list type="number">
/// <item><description><see cref="LatticeConfig"/> is an immutable record and the
/// <see cref="HostConfig"/> list it carries is never mutated in place — <see cref="Mutate"/>
/// always builds a NEW list (<c>[.. _config.Hosts, host]</c>) inside a NEW record. A reader
/// enumerating the old list therefore cannot see it change underneath it, so no reader can
/// observe a half-applied edit or throw a collection-modified exception.</description></item>
/// <item><description>Publication is one reference assignment (<c>_config = next</c>),
/// and reference writes are atomic — a reader sees either the whole old config or the
/// whole new one, never a torn reference to neither.</description></item>
/// <item><description>The reference is published AFTER the record it points at is fully
/// built (the record is constructed and its list materialized before <see cref="Mutate"/>
/// even runs), so a reader that observes the new reference observes complete contents.</description></item>
/// </list>
/// <para>
/// The field is deliberately NOT <c>volatile</c>: the only thing volatility would add here
/// is a freshness bound, and no consumer needs one. Every reader is either edge-driven —
/// it acts on a <see cref="Changed"/> event, which is raised after the swap by the mutating
/// thread — or, like <c>FindConfig</c>, tolerant of one stale read by construction: a host
/// removed a microsecond ago still yields a connection attempt that the caller must already
/// handle failing (<c>HostRemovedException</c> covers the converse). Reading a stale
/// snapshot is a semantic outcome here, not a data race.
/// </para>
/// </summary>
public sealed class HostRegistry
{
    /// <summary>The polling intervals the Settings UI offers (seconds).</summary>
    public static readonly IReadOnlyList<int> AllowedPollingIntervals = LatticeConfig.AllowedPollingIntervals;

    private readonly string _path;
    // Single-writer, any-reader: swapped whole by Mutate on the one mutating thread,
    // read lock-free from any thread. See the class doc's publication protocol for why
    // that is safe without volatile — it hangs on this field only ever holding a fully
    // built immutable record.
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
