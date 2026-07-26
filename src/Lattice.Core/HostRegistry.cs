using System.Collections.ObjectModel;

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
/// because each builds its next state from the config it read (read-modify-write with no
/// lock). READS are free from any thread and need no lock, which is what
/// <c>HostControlService.FindConfig</c> relies on when it walks <see cref="Hosts"/> on a
/// thread-pool lane. Three facts make that safe, and all three must hold together:
/// </para>
/// <list type="number">
/// <item><description>The published <see cref="HostConfig"/> list cannot be mutated in place
/// BY ANYONE, which <see cref="Sealed"/> enforces at the boundary rather than leaving to
/// convention: every config entering this class — through the public constructor as much as
/// through <see cref="Mutate"/> — is re-published with a fresh
/// <see cref="ReadOnlyCollection{T}"/> of hosts. A caller that keeps a reference to the
/// <c>List</c> it passed in holds a reference to a list this registry no longer uses, and
/// <see cref="Hosts"/> cannot be downcast back to <c>List</c>. So a reader enumerating a
/// published list on any thread cannot see it change underneath it — no half-applied edit,
/// no collection-modified exception — and that holds for arbitrary callers, not just the
/// well-behaved ones in this repo.</description></item>
/// <item><description>Publication is one reference assignment, and reference writes are
/// atomic — a reader sees either the whole old config or the whole new one, never a torn
/// reference to neither.</description></item>
/// <item><description>That assignment goes through <c>Volatile.Write</c> and every read
/// through <c>Volatile.Read</c> (the <see cref="Config"/> accessor below), which is what makes
/// the read well-defined against a concurrent writer: the JIT may not cache or reorder it, so
/// each call genuinely re-reads the field rather than reusing a value it hoisted earlier.
/// </description></item>
/// </list>
/// <para>
/// <b>What this does and does not give <c>HostControlService</c>'s I-CL2.</b> Be precise here,
/// because the tempting over-claim is that the reader always observes the newest write. It does
/// not, and no primitive would provide that — not <c>Volatile</c>, and not a lock: if an edit
/// and a lane turn's read are genuinely concurrent, nothing orders them, since there is no
/// happens-before edge between "the user finished editing" and "the queued turn started". A
/// lock would add a total order over its own acquisitions; it would not make the lane turn
/// acquire second.
/// </para>
/// <para>
/// I-CL2 — "a config edit made between the user's click and execution wins" — is satisfied by
/// READ PLACEMENT in program order, not by a memory barrier. The lane turn reads the registry
/// inside the turn (<c>HostControlService.RunAfterAsync</c>), after awaiting its predecessor,
/// instead of capturing a <see cref="HostConfig"/> at submission time and carrying it through
/// the queue. Capturing early is the defect I-CL2 rules out, and it is a structural property of
/// where the call sits. This field's job is only to make that late read a real read; the
/// invariant is upheld by <c>HostControlService</c>, not here.
/// </para>
/// <para>
/// So the residual case is not a stale read but an ordering outcome: a turn that reads before a
/// concurrent edit lands is simply an op that started first. Removal is the same shape from the
/// other side, and the turn already handles losing that race (<c>HostRemovedException</c>).
/// </para>
/// </summary>
public sealed class HostRegistry
{
    /// <summary>The polling intervals the Settings UI offers (seconds).</summary>
    public static readonly IReadOnlyList<int> AllowedPollingIntervals = LatticeConfig.AllowedPollingIntervals;

    private readonly string _path;
    // Single-writer, any-reader: swapped whole by Mutate on the one mutating thread, read
    // lock-free from any thread. Never touched directly — Config below is the only accessor,
    // so no call site can silently drop the Volatile pairing the class doc's protocol needs.
    private LatticeConfig _config;

    /// <summary>
    /// The published config. Same shape as HostMonitor's Status/Snapshot: the field is
    /// private and every read/write goes through this property, so the release/acquire
    /// pairing is structural rather than a rule each call site has to remember.
    /// </summary>
    private LatticeConfig Config
    {
        get => Volatile.Read(ref _config);
        set => Volatile.Write(ref _config, value);
    }

    /// <summary>Wraps an in-memory config; <paramref name="path"/> is where mutations are saved.</summary>
    public HostRegistry(LatticeConfig config, string path)
    {
        // The one place a plain field write is the right one: nothing can observe the
        // instance until the constructor returns, and .NET guarantees that publication
        // barrier already. Going through Config here would only trip CS8618.
        _config = Sealed(config);
        _path = path;
    }

    /// <summary>
    /// Normalizes a config into publishable form: the host list becomes a fresh
    /// <see cref="ReadOnlyCollection{T}"/>. This is what makes the class doc's
    /// immutability leg a property of the TYPE rather than of caller discipline, and it
    /// closes both ways a caller could otherwise mutate a list a lane is enumerating —
    /// keeping a reference to the <c>List</c> it passed to the public constructor, or
    /// downcasting <see cref="Hosts"/> back to <c>List</c>. Copying breaks the first,
    /// the wrapper breaks the second. Runs once per mutation (a user action), so the
    /// copy is not on any hot path.
    /// </summary>
    private static LatticeConfig Sealed(LatticeConfig config) =>
        config with { Hosts = new ReadOnlyCollection<HostConfig>([.. config.Hosts]) };

    /// <summary>Loads the config at <paramref name="path"/> (missing file ⇒ defaults).</summary>
    public static HostRegistry Load(string path) => new(LatticeConfig.Load(path), path);

    /// <summary>The registered hosts, in insertion order.</summary>
    public IReadOnlyList<HostConfig> Hosts => Config.Hosts;

    /// <summary>Steady-state polling interval in seconds.</summary>
    public int PollingIntervalSeconds => Config.PollingIntervalSeconds;

    /// <summary>Whether the relaxed hidden-window polling floor is bypassed (issue #92).</summary>
    public bool FullSpeedHiddenPolling => Config.FullSpeedHiddenPolling;

    /// <summary>Raised after every persisted mutation.</summary>
    public event EventHandler<RegistryChangedEventArgs>? Changed;

    /// <summary>Adds a host. Throws if a host with the same Id is already registered.</summary>
    public void AddHost(HostConfig host)
    {
        if (IndexOf(host.Id) is not null)
            throw new ArgumentException($"A host with id {host.Id} is already registered.", nameof(host));
        Mutate(Config with { Hosts = [.. Config.Hosts, host] }, RegistryChangeKind.HostAdded, host);
    }

    /// <summary>Replaces the host with the same Id. Throws if no such host exists.</summary>
    public void UpdateHost(HostConfig host)
    {
        int index = IndexOf(host.Id)
            ?? throw new ArgumentException($"No host with id {host.Id}.", nameof(host));
        List<HostConfig> hosts = [.. Config.Hosts];
        hosts[index] = host;
        Mutate(Config with { Hosts = hosts }, RegistryChangeKind.HostUpdated, host);
    }

    /// <summary>Removes the host with the given Id. Throws if no such host exists.</summary>
    public void RemoveHost(Guid id)
    {
        int index = IndexOf(id)
            ?? throw new ArgumentException($"No host with id {id}.", nameof(id));
        HostConfig removed = Config.Hosts[index];
        List<HostConfig> hosts = [.. Config.Hosts];
        hosts.RemoveAt(index);
        Mutate(Config with { Hosts = hosts }, RegistryChangeKind.HostRemoved, removed);
    }

    /// <summary>Sets the polling interval. Only <see cref="AllowedPollingIntervals"/> values are accepted.</summary>
    public void SetPollingInterval(int seconds)
    {
        if (!AllowedPollingIntervals.Contains(seconds))
            throw new ArgumentOutOfRangeException(nameof(seconds), seconds,
                "Allowed polling intervals: 2, 5, 10, 30, 60 seconds.");
        Mutate(Config with { PollingIntervalSeconds = seconds }, RegistryChangeKind.IntervalChanged, null);
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
        if (Config.FullSpeedHiddenPolling == enabled)
            return;
        Mutate(Config with { FullSpeedHiddenPolling = enabled }, RegistryChangeKind.IntervalChanged, null);
    }

    private int? IndexOf(Guid id)
    {
        for (int i = 0; i < Config.Hosts.Count; i++)
            if (Config.Hosts[i].Id == id)
                return i;
        return null;
    }

    private void Mutate(LatticeConfig next, RegistryChangeKind kind, HostConfig? host)
    {
        // Persist before swapping the in-memory state: if Save throws (unwritable
        // directory, full disk), Config must stay at its old value so memory, disk,
        // and every already-connected monitor's config remain consistent. Swapping
        // first would leave memory diverged from disk until the next app start.
        next = Sealed(next);
        next.Save(_path);
        Config = next;
        Changed?.Invoke(this, new RegistryChangedEventArgs(kind, host));
    }
}
