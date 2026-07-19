using Lattice.Boinc.GuiRpc;

namespace Lattice.Core;

/// <summary>
/// Runs write-path control operations (task suspend/resume/abort, project
/// suspend/resume/update/detach, run modes) against BOINC daemons on a per-host
/// serialized <em>control lane</em> of short-lived side connections — the same
/// side-connection precedent as <c>HostMonitorManager.TestConnectionAsync</c> (connect →
/// auth-when-a-password-is-configured → op RPC → dispose). The read-path
/// <see cref="HostMonitor"/> is never touched: on success the service only nudges the
/// target monitor to re-poll (<see cref="HostMonitor.RequestRefresh"/>), so
/// <c>HostSnapshot</c> stays single-writer and converges within ~1 poll tick.
///
/// Concurrency invariants (enforced by the tail-task chain below, not by locks sprinkled
/// through the op body):
///   I-CL1  At most one control connection per host exists at any instant.
///   I-CL2  An op reads its <see cref="HostConfig"/> fresh from the registry inside its
///          lane turn, so a config edit made between the user's click and execution wins.
///   I-CL3  Ops never throw — every path (cancellation included) returns a
///          <see cref="ControlOpResult"/>.
///   I-CL4  The side client is disposed on every path (<c>await using</c>).
///   I-CL5  Same-host ops execute in submission order (FIFO). A <see cref="SemaphoreSlim"/>
///          would NOT do: its blocked waiters have no documented release order, so
///          Suspend-then-Resume could run as Resume-then-Suspend and land the wrong final
///          state. Instead each submission atomically appends itself to a per-host tail
///          task (awaiting the previous op's completion, whose OUTCOME it ignores), so
///          submission order is execution order by construction and a canceled/faulted op
///          can never break the chain — the next op still runs.
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

    // Appends the op to the host's lane tail and returns the task for THIS op. The lock is
    // held only for the map read-modify-write; RunAfterAsync suspends at its first await
    // (the predecessor, or — for an empty lane — the op's own first I/O await), so no op
    // body and no I/O ever runs under _laneGate.
    private Task<ControlOpResult> SubmitAsync(
        Guid hostId, Func<IGuiRpcClient, CancellationToken, Task> rpc, CancellationToken ct)
    {
        lock (_laneGate)
        {
            Task previous = _lanes.TryGetValue(hostId, out Task? tail) ? tail : Task.CompletedTask;
            Task<ControlOpResult> current = RunAfterAsync(previous, hostId, rpc, ct);
            _lanes[hostId] = current;
            return current;
        }
    }

    // I-CL1 / I-CL5: wait for the previous same-host op, then run this one. The predecessor's
    // result is deliberately never observed — a faulted or canceled predecessor must not break
    // the lane. Because ExecuteAsync never throws (I-CL3), `previous` always completes
    // successfully in practice; the guard is defense in depth so the chain is provably
    // unbreakable regardless.
    private async Task<ControlOpResult> RunAfterAsync(
        Task previous, Guid hostId, Func<IGuiRpcClient, CancellationToken, Task> rpc, CancellationToken ct)
    {
        try { await previous.ConfigureAwait(false); }
        catch { /* predecessor outcome intentionally ignored — the chain must not break */ }
        return await ExecuteAsync(hostId, rpc, ct).ConfigureAwait(false);
    }

    // One op on a fresh short-lived connection. Never throws (I-CL3).
    private async Task<ControlOpResult> ExecuteAsync(
        Guid hostId, Func<IGuiRpcClient, CancellationToken, Task> rpc, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            // I-CL2: read the config fresh inside the lane turn.
            HostConfig? config = FindConfig(hostId);
            if (config is null)
                return new ControlOpResult(ControlOpOutcome.Unreachable, HostRemovedMessage);

            // I-CL4: await using disposes the connection on every exit path.
            await using IGuiRpcClient client = _clientFactory();
            await client.ConnectAsync(config.Address, config.Port, ct).ConfigureAwait(false);
            if (config.Password.Length > 0
                && !await client.AuthorizeAsync(config.Password, ct).ConfigureAwait(false))
                return new ControlOpResult(ControlOpOutcome.AuthFailed, PasswordRefusedMessage);

            await rpc(client, ct).ConfigureAwait(false);

            // Success: converge the read path within ~1 tick. No optimistic snapshot
            // mutation — the monitor remains the sole writer of HostSnapshot.
            NudgeMonitor(hostId);
            return ControlOpResult.Success;
        }
        catch (Exception ex)
        {
            // I-CL3: every failure — cancellation included — becomes a ControlOpResult,
            // classified by exception type only.
            return ControlOpResult.FromException(ex);
        }
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
}
