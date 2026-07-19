using Lattice.Boinc.GuiRpc;

namespace Lattice.Core;

/// <summary>
/// Runs write-path control operations (task suspend/resume/abort, project
/// suspend/resume/update/detach, run modes) against BOINC daemons on a per-host
/// serialized <em>control lane</em> of short-lived side connections — the same
/// side-connection precedent as <c>HostMonitorManager.TestConnectionAsync</c> (connect →
/// auth-when-a-password-is-configured → op RPC → dispose). The lane itself is exposed to
/// <see cref="AttachFlowRunner"/> via the internal <see cref="RunOnLaneAsync{T}"/> hook, so
/// an attach flow occupies the lane (and its ONE connection) for the flow's whole
/// duration and other control ops on that host queue behind it (design §2.1/§2.3). The
/// read-path <see cref="HostMonitor"/> is never touched: on success the service only
/// nudges the target monitor to re-poll (<see cref="HostMonitor.RequestRefresh"/>), so
/// <c>HostSnapshot</c> stays single-writer and converges within ~1 poll tick.
///
/// Concurrency invariants (enforced by the tail-task chain below, not by locks sprinkled
/// through the op body):
///   I-CL1  At most one control connection per host exists at any instant.
///   I-CL2  A lane turn reads its <see cref="HostConfig"/> fresh from the registry inside
///          the turn, so a config edit made between the user's click and execution wins.
///   I-CL3  The public op methods never throw — every path (cancellation included)
///          returns a <see cref="ControlOpResult"/>. The internal lane hook is the one
///          throwing surface: it propagates failures to its caller, which owns the
///          mapping to that caller's result type.
///   I-CL4  The side client is disposed on every path (<c>await using</c>).
///   I-CL5  Same-host lane turns execute in submission order (FIFO). A <see cref="SemaphoreSlim"/>
///          would NOT do: its blocked waiters have no documented release order, so
///          Suspend-then-Resume could run as Resume-then-Suspend and land the wrong final
///          state. Instead each submission atomically appends itself to a per-host tail
///          task (awaiting the previous turn's completion, whose OUTCOME it ignores), so
///          submission order is execution order by construction and a canceled/faulted
///          turn — an attach flow dying mid-lookup included — can never break the chain:
///          the next op still runs.
///
/// The lane map entries are never removed for the service's lifetime: removing a host
/// leaves a bounded, already-completed orphan task in the map (no consumer loop, no
/// disposal lifecycle to reason about). Control ops are user-initiated and rare, so this
/// costs nothing.
///
/// Design note (per the design doc §2.1, authoritative over the plan's wording): the plan
/// sketched a <c>ConcurrentDictionary</c> "atomic swap", but <c>ConcurrentDictionary.AddOrUpdate</c>
/// may invoke its update delegate more than once under contention — with a side-effecting
/// delegate that starts a task, that would double-run an op. The swap is therefore guarded
/// by a plain lock held ONLY for the O(1) map read-modify-write (never across any await or
/// I/O), which is the genuinely-atomic tail-chain the design mandates.
/// </summary>
public sealed class HostControlService
{
    // The host was removed from the registry between the user's click and this op's turn.
    private const string HostRemovedMessage = "The host is no longer registered.";
    // AuthorizeAsync returned false: the daemon rejected the configured password. Mirrors
    // TestConnectionAsync's wording (this is a non-exception AuthFailed path).
    private const string PasswordRefusedMessage = "The host refused the password.";

    private readonly HostRegistry _registry;
    private readonly HostMonitorManager _manager;
    private readonly Func<IGuiRpcClient> _clientFactory;

    // Per-host tail of the control lane. Guarded by _laneGate for the swap only.
    private readonly object _laneGate = new();
    private readonly Dictionary<Guid, Task> _lanes = [];

    /// <summary>Creates the service over the shared registry, monitor manager, and client factory.</summary>
    public HostControlService(HostRegistry registry, HostMonitorManager manager, Func<IGuiRpcClient> clientFactory)
    {
        _registry = registry;
        _manager = manager;
        _clientFactory = clientFactory;
    }

    /// <summary>Suspends, resumes, or aborts one task on the given host.</summary>
    public Task<ControlOpResult> PerformTaskOpAsync(
        Guid hostId, TaskOp op, string projectUrl, string taskName, CancellationToken ct = default) =>
        SubmitAsync(hostId, (client, token) => client.PerformTaskOpAsync(op, projectUrl, taskName, token), ct);

    /// <summary>Suspends, resumes, updates, or detaches one project on the given host.</summary>
    public Task<ControlOpResult> PerformProjectOpAsync(
        Guid hostId, ProjectOp op, string projectUrl, CancellationToken ct = default) =>
        SubmitAsync(hostId, (client, token) => client.PerformProjectOpAsync(op, projectUrl, token), ct);

    /// <summary>Sets a run-mode lane (or snooze / restore) on the given host.</summary>
    public Task<ControlOpResult> SetModeAsync(
        Guid hostId, ModeLane lane, RunMode mode, TimeSpan duration, CancellationToken ct = default) =>
        SubmitAsync(hostId, (client, token) => client.SetModeAsync(lane, mode, duration, token), ct);

    // One op = one lane turn whose body is the single op RPC. Converts the hook's thrown
    // failures back to the I-CL3 contract (op methods never throw), by exception TYPE only.
    private async Task<ControlOpResult> SubmitAsync(
        Guid hostId, Func<IGuiRpcClient, CancellationToken, Task> rpc, CancellationToken ct)
    {
        try
        {
            // The hook is generic over a body payload; an op RPC has none.
            await RunOnLaneAsync<object?>(hostId,
                async (client, token) => { await rpc(client, token).ConfigureAwait(false); return null; },
                ct).ConfigureAwait(false);
        }
        catch (HostRemovedException) { return new ControlOpResult(ControlOpOutcome.Unreachable, HostRemovedMessage); }
        catch (PasswordRefusedException) { return new ControlOpResult(ControlOpOutcome.AuthFailed, PasswordRefusedMessage); }
        catch (Exception ex) { return ControlOpResult.FromException(ex); }

        // Success: converge the read path within ~1 tick. No optimistic snapshot
        // mutation — the monitor remains the sole writer of HostSnapshot.
        NudgeMonitor(hostId);
        return ControlOpResult.Success;
    }

    /// <summary>
    /// The per-host control lane in one place: waits for the host's previous lane turn,
    /// then — on a fresh side connection — connect → auth-when-a-password-is-configured →
    /// <paramref name="body"/> → dispose. The op methods run their single RPC as the body;
    /// <see cref="AttachFlowRunner"/> runs its whole machine-driven flow as the body,
    /// holding the lane (and the ONE connection the daemon's lookup state lives on) for
    /// the flow's full duration (design §2.1/§2.3). Unlike the public op methods (I-CL3),
    /// this hook PROPAGATES failures — the host-removed / password-refused signals below,
    /// transport and RPC exceptions, cancellation — and each caller maps them to its own
    /// result type.
    ///
    /// Appends the turn to the host's lane tail and returns the task for THIS turn. The
    /// lock is held ONLY for the map read-modify-write: the turn is launched via Task.Run,
    /// which queues it to the thread pool (TaskScheduler.Default) and returns immediately,
    /// so no turn body, factory call, or I/O ever runs under _laneGate. Two reasons this
    /// must run on the pool rather than inline: (1) _laneGate is a single global gate
    /// shared across hosts, so running turn work under it would let a slow client factory
    /// or a synchronously-completing turn on one host block submissions for UNRELATED
    /// hosts (breaking different-host independence); (2) the lane must be independent of
    /// whatever context submitted the turn — Task.Run detaches from the caller's
    /// SynchronizationContext (the UI dispatcher in the app), which an inline async body
    /// or Task.Yield would otherwise capture.
    /// </summary>
    internal Task<T> RunOnLaneAsync<T>(
        Guid hostId, Func<IGuiRpcClient, CancellationToken, Task<T>> body, CancellationToken ct)
    {
        lock (_laneGate)
        {
            Task previous = _lanes.TryGetValue(hostId, out Task? tail) ? tail : Task.CompletedTask;
            Task<T> current = Task.Run(() => RunAfterAsync(previous, hostId, body, ct));
            _lanes[hostId] = current;
            return current;
        }
    }

    // I-CL1 / I-CL5: wait for the previous same-host lane turn, then run this one. The
    // predecessor's outcome is deliberately never observed. Since the attach flow joined
    // the lane this guard is load-bearing, not defense in depth: a lane turn CAN fault or
    // cancel (the hook propagates), and the next turn must still run — a chain that
    // rethrows its predecessor would wedge every later op on the host.
    private async Task<T> RunAfterAsync<T>(
        Task previous, Guid hostId, Func<IGuiRpcClient, CancellationToken, Task<T>> body, CancellationToken ct)
    {
        try { await previous.ConfigureAwait(false); }
        catch { /* predecessor outcome intentionally ignored — the chain must not break */ }

        ct.ThrowIfCancellationRequested();
        // I-CL2: read the config fresh inside the lane turn. Checked before the factory
        // call so an unknown host never creates a connection.
        HostConfig config = FindConfig(hostId) ?? throw new HostRemovedException();

        // I-CL4: await using disposes the connection on every exit path.
        await using IGuiRpcClient client = _clientFactory();
        await client.ConnectAsync(config.Address, config.Port, ct).ConfigureAwait(false);
        if (config.Password.Length > 0
            && !await client.AuthorizeAsync(config.Password, ct).ConfigureAwait(false))
            throw new PasswordRefusedException();

        return await body(client, ct).ConfigureAwait(false);
    }

    private HostConfig? FindConfig(Guid hostId)
    {
        foreach (HostConfig host in _registry.Hosts)
            if (host.Id == hostId)
                return host;
        return null;
    }

    private void NudgeMonitor(Guid hostId)
    {
        foreach (HostMonitor monitor in _manager.Monitors)
            if (monitor.HostId == hostId)
            {
                monitor.RequestRefresh();
                return;
            }
    }

    // Lane-turn refusal signals: the generic hook cannot return a result-typed refusal,
    // so the two non-RPC failure modes travel as typed exceptions carrying their display
    // message. SubmitAsync maps them back by TYPE (never message text); AttachFlowRunner's
    // generic fault path surfaces Message as-is.
    private sealed class HostRemovedException : Exception
    {
        public HostRemovedException() : base(HostRemovedMessage) { }
    }

    private sealed class PasswordRefusedException : Exception
    {
        public PasswordRefusedException() : base(PasswordRefusedMessage) { }
    }
}
