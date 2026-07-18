using Lattice.Boinc.GuiRpc;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;

namespace Lattice.Core;

/// <summary>Payload of <see cref="HostMonitor.MessagesAdded"/>: only the newly arrived messages.</summary>
public sealed record MessagesAddedEventArgs(Guid HostId, IReadOnlyList<Message> Messages);

/// <summary>
/// Named interleaving points of the actor loop, in loop order. Test-only seam:
/// production leaves <see cref="HostMonitor.InterleaveProbe"/> null. Placement rules
/// (verification/README.md): never inside a lock block; only where environment
/// threads can already interleave in production. Every new shared-state touch point
/// added to HostMonitor MUST add a point here (correspondence rule 3).
///
/// Each name aliases the authoritative <see cref="ProbePoints"/> literal in the F#
/// decision core (HostMachine.fs): the core emits <c>Probe</c> commands carrying these
/// exact strings, so the two lists cannot drift.
/// </summary>
internal static class InterleavePoints
{
    public const string BeforeSnapshot = ProbePoints.BeforeSnapshot;
    public const string AfterSnapshot = ProbePoints.AfterSnapshot;
    public const string BeforeAcceptGuard = ProbePoints.BeforeAcceptGuard;
    public const string BeforeConnectedPublish = ProbePoints.BeforeConnectedPublish;
    public const string TickBeforeMsgGuard = ProbePoints.TickBeforeMsgGuard;
    public const string TickBeforeMsgPublish = ProbePoints.TickBeforeMsgPublish;
    public const string TickBeforeSnapGuard = ProbePoints.TickBeforeSnapGuard;
    public const string TickBeforeBuild = ProbePoints.TickBeforeBuild;
    public const string TickBeforeSnapPublish = ProbePoints.TickBeforeSnapPublish;
    public const string PollBeforeWait = ProbePoints.PollBeforeWait;
    public const string PollAfterWait = ProbePoints.PollAfterWait;
    public const string FinallyEnter = ProbePoints.FinallyEnter;
    public const string AfterCtsDispose = ProbePoints.AfterCtsDispose;
    public const string BeforeRetryPublish = ProbePoints.BeforeRetryPublish;
    public const string BeforeParkWait = ProbePoints.BeforeParkWait;

    public static readonly string[] All =
    [
        BeforeSnapshot, AfterSnapshot, BeforeAcceptGuard, BeforeConnectedPublish,
        TickBeforeMsgGuard, TickBeforeMsgPublish, TickBeforeSnapGuard, TickBeforeBuild,
        TickBeforeSnapPublish, PollBeforeWait, PollAfterWait, FinallyEnter,
        AfterCtsDispose, BeforeRetryPublish, BeforeParkWait,
    ];
}

/// <summary>
/// The per-host actor: one background loop owns one <see cref="IGuiRpcClient"/> and
/// walks the connection state machine. Events fire on the loop's thread-pool context,
/// sequentially per host — subscribers marshal to their own context (the app uses
/// Dispatcher.UIThread).
/// </summary>
public sealed class HostMonitor : IAsyncDisposable
{
    private readonly Func<IGuiRpcClient> _clientFactory;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private HostConfig _config;
    private volatile bool _configChanged;
    // Owns cancellation for one connection attempt (connect through steady-state
    // polling). Linked to the disposal token; UpdateConfig cancels it to abort
    // in-flight work on the OLD config instead of waiting for it to finish on its
    // own. Guarded by _gate: created by the interpreter's SnapshotConfig command
    // (one _gate block), canceled from UpdateConfig, disposed and nulled by
    // DisposeConnectionCts (and defensively in RunAsync's finally).
    private CancellationTokenSource? _connectionCts;
    private volatile int _pollingIntervalSeconds;
    private TaskCompletionSource _wake = NewWake();
    // internal (not private) solely so the interleaving sweep can assert the loop
    // task never faults (A5); no production code outside this class touches it.
    internal Task _loop = Task.CompletedTask;
    private bool _started;
    private bool _disposed;
    private VersionInfo? _daemonVersion;
    private ConnectionStatus _status;
    private HostSnapshot? _snapshot;

    /// <summary>Message buffer cap: the most recent 5,000 messages are retained per host.</summary>
    internal const int MessageCapacity = 5000;

    // Test-only interleaving seam (see InterleavePoints). Null in production: the
    // probe call is a single null check on the loop's paths. Set via
    // InternalsVisibleTo by the sweep harness ONLY before Start().
    internal Func<string, Task>? InterleaveProbe;

    private Task ProbeAsync(string point) =>
        InterleaveProbe?.Invoke(point) ?? Task.CompletedTask;

    private readonly MessageLog _messages = new(MessageCapacity);

    /// <summary>Creates a monitor; call <see cref="Start"/> to begin connecting.</summary>
    public HostMonitor(HostConfig config, Func<IGuiRpcClient> clientFactory,
                       TimeProvider timeProvider, int pollingIntervalSeconds)
    {
        _config = config;
        _clientFactory = clientFactory;
        _time = timeProvider;
        _pollingIntervalSeconds = pollingIntervalSeconds;
        HostId = config.Id;
        _status = new ConnectionStatus(config.Id, HostConnectionState.Disconnected, 0, null, null, null);
    }

    /// <summary>Stable identity of the monitored host (never changes across config updates).</summary>
    public Guid HostId { get; }

    /// <summary>The latest connection status.</summary>
    public ConnectionStatus Status
    {
        get => Volatile.Read(ref _status);
        private set => Volatile.Write(ref _status, value);
    }

    /// <summary>The latest snapshot, or null before the first successful poll tick.</summary>
    public HostSnapshot? Snapshot
    {
        get => Volatile.Read(ref _snapshot);
        private set => Volatile.Write(ref _snapshot, value);
    }

    /// <summary>
    /// The retained message buffer (oldest first, capped at 5,000). Retains
    /// messages from the previous connection until the new connection's first tick
    /// completes, at which point the log is atomically replaced with the daemon's
    /// current buffer (from seqno 0). This retention applies the same "last known"
    /// doctrine as Snapshot, ensuring a freshly (re)started daemon whose seqno
    /// counter reset cannot silently lose messages already seen.
    /// </summary>
    public IReadOnlyList<Message> Messages => _messages.Snapshot();

    /// <summary>
    /// Raised once per state transition, from the monitor's loop context.
    /// Subscriber exceptions are ignored — they never affect the state machine.
    /// </summary>
    public event EventHandler<ConnectionStatus>? StatusChanged;

    /// <summary>
    /// Raised after every poll tick with the freshly built snapshot.
    /// Subscriber exceptions are ignored — they never affect the state machine.
    /// </summary>
    public event EventHandler<HostSnapshot>? SnapshotUpdated;

    /// <summary>
    /// Raised when a tick returns new messages; carries only the new ones.
    /// Semantics unchanged by retention: the first tick of a new connection
    /// atomically replaces the log and raises this with the daemon's current buffer
    /// from seqno 0, even if some of those messages were already seen before
    /// disconnect. Subsequent ticks within the same connection append new messages.
    /// Subscriber exceptions are ignored — they never affect the state machine.
    /// </summary>
    public event EventHandler<MessagesAddedEventArgs>? MessagesAdded;

    /// <summary>Starts the background loop. Idempotent and inert after disposal.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_started || _disposed)
                return;
            _started = true;
            // Token captured under _gate: DisposeAsync sets _disposed under this
            // same lock BEFORE it ever cancels/disposes _cts, so a Start that got
            // here holds a token read strictly before any dispose (I5).
            CancellationToken token = _cts.Token;
            _loop = Task.Run(() => RunAsync(token), CancellationToken.None);
        }
    }

    /// <summary>
    /// Wakes the loop immediately: skips remaining backoff when Retrying, or triggers
    /// an immediate poll tick when Connected. Deliberately does NOT revive AuthFailed.
    /// </summary>
    public void RequestRefresh() => Wake();

    /// <summary>
    /// Applies a new address/port/password: cancels any in-flight connection work
    /// (connect, auth, RPC calls, and the current poll tick) for the previous config,
    /// then tears down and reconnects from scratch.
    /// </summary>
    public void UpdateConfig(HostConfig config)
    {
        if (config.Id != HostId)
            throw new ArgumentException($"Config id {config.Id} does not match host {HostId}.", nameof(config));
        lock (_gate)
        {
            _config = config;
            _configChanged = true;
            _connectionCts?.Cancel();
        }
        Wake();
    }

    /// <summary>Changes the steady-state polling interval; takes effect from the next wait.</summary>
    public void SetPollingInterval(int seconds)
    {
        _pollingIntervalSeconds = seconds;
        Wake();
    }

    /// <summary>
    /// Changes the steady-state polling interval WITHOUT waking the loop (issue #92):
    /// the new value is picked up at the next wait boundary, never interrupting an
    /// in-progress backoff or poll wait. The cadence funnel
    /// (<see cref="HostMonitorManager"/>.ApplyCadence) uses this so hiding to the tray
    /// cannot cut a Retrying host's exponential backoff short — immediate refresh remains
    /// the sole job of <see cref="RequestRefresh"/>. Volatile write only; no _gate needed.
    /// </summary>
    public void SetPollingIntervalQuiet(int seconds) => _pollingIntervalSeconds = seconds;

    /// <summary>Stops the loop, disposes the client, and settles in Disconnected. Idempotent.</summary>
    public async ValueTask DisposeAsync()
    {
        // Exactly one caller wins the _disposed test-and-set under _gate; only that
        // caller touches _cts (a second Cancel/Dispose would throw ObjectDisposed).
        // Every caller — winner or not — awaits the SAME loop task read under the
        // lock, so no DisposeAsync returns before teardown actually finished, and
        // _cts.Dispose() is sequenced strictly after the loop's last token use.
        bool first;
        Task loop;
        lock (_gate)
        {
            first = !_disposed;
            _disposed = true;
            loop = _loop;
        }
        if (first)
        {
            _cts.Cancel();
            Wake();
        }
        try { await loop.ConfigureAwait(false); }
        catch { /* the loop reports failures via Status, never by throwing */ }
        if (first)
            _cts.Dispose();
    }

    /// <summary>Backoff schedule: 1, 2, 4, 8, 16, 32 then capped at 60 seconds.</summary>
    internal static TimeSpan BackoffDelay(int attempt) => HostMachine.backoffDelay(attempt);

    private static TaskCompletionSource NewWake() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void Wake()
    {
        lock (_gate)
            _wake.TrySetResult();
    }

    // Wakes are sticky until consumed: a Wake() that lands while the loop is busy is
    // observed by the next wait instead of being lost.
    private async Task<bool> WaitAsync(TimeSpan delay, CancellationToken ct)
    {
        Task wake;
        lock (_gate)
        {
            if (_wake.Task.IsCompleted)
            {
                _wake = NewWake();
                return true;
            }
            wake = _wake.Task;
        }
        Task delayTask = Task.Delay(delay, _time, ct);
        Task done = await Task.WhenAny(delayTask, wake).ConfigureAwait(false);
        if (done == wake)
        {
            lock (_gate)
                if (_wake.Task.IsCompleted)
                    _wake = NewWake();
            return true;
        }
        await delayTask.ConfigureAwait(false); // propagate cancellation
        return false;
    }

    // AuthFailed parking: only a config change releases the loop. Stale wakes from
    // RequestRefresh are consumed and ignored.
    private async Task WaitForConfigChangeAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Task wake;
            lock (_gate)
            {
                if (_configChanged)
                    return;
                if (_wake.Task.IsCompleted)
                {
                    _wake = NewWake();
                    continue;
                }
                wake = _wake.Task;
            }
            await wake.WaitAsync(ct).ConfigureAwait(false);
        }
        ct.ThrowIfCancellationRequested();
    }

    // All event dispatch goes through here: a buggy subscriber must never affect the
    // state machine. A subscriber exception escaping a PublishStatus/PublishSnapshot
    // execution would be classified as a command fault — tearing down a healthy
    // connection into Retrying, or exiting the loop outright from the post-teardown
    // publishes (AuthFailed/Retrying execute in phase Parked/BackoffWaiting).
    // Subscribers marshal to their own context (see class doc) and there is nowhere
    // meaningful to report their failures from here, so they are swallowed.
    private void RaiseSafe<T>(EventHandler<T>? handler, T args)
    {
        try { handler?.Invoke(this, args); }
        catch { /* ignored: subscriber failures must not affect the state machine */ }
    }

    private void SetStatus(HostConnectionState state, int attempt,
                           DateTimeOffset? nextAttemptAt = null, string? lastError = null)
    {
        var status = new ConnectionStatus(HostId, state, attempt, nextAttemptAt, lastError, _daemonVersion);
        Status = status;
        RaiseSafe(StatusChanged, status);
    }

    // ---------------------------------------------------------------------------
    // The interpreter. All phase sequencing, guard routing, attempt counting, and
    // auth handling live in the pure decision core HostMachine.step (HostMachine.fs,
    // the authoritative contract). This loop owns ONLY I/O, locks, waits, and events:
    // it executes the Commands the core emits and feeds the resulting Input back.
    //
    // Contract (mirrored from HostMachine.fs's module doc):
    //  - each step returns a next State plus a Command batch: zero or more
    //    fire-and-forget commands then at most one trailing request command; the
    //    request's produced value is the next Input, else EffectOk.
    //  - if executing ANY command throws, the rest of the batch is skipped and the
    //    core is fed Faulted, classified by exception TYPE ONLY (see Classify). The
    //    loop task must never fault (I5/A5); the core settles unexpected pairs in
    //    Disconnected rather than throwing.
    //
    // The structural invariants the old hand-written dispatcher maintained by
    // convention are now theorems of the verified core: teardown (client + linked
    // CTS dispose) is always commanded BEFORE any park/backoff/park wait (I2), so
    // every wait provably runs with no GUI RPC connection open; and AuthFailed/Retrying
    // publishes are commanded after that teardown. The finally below is defense in
    // depth only (see its comment), not decision logic.
    private async Task RunAsync(CancellationToken ct)
    {
        var state = HostMachine.initial;
        HostMachine.Input input = HostMachine.Input.Started;

        // Attempt-scoped resources and payloads, owned by the interpreter: the core
        // decides, these execute. Reset by the SnapshotConfig / DisposeClient commands
        // that bracket one attempt in the core's plan.
        IGuiRpcClient? client = null;
        HostConfig config = _config;
        CancellationToken connCt = default;
        VersionInfo? fetchedVersion = null;
        CcState? ccState = null;
        CcStatus? ccStatus = null;
        IReadOnlyList<Result> results = [];
        IReadOnlyList<FileTransfer> transfers = [];
        IReadOnlyList<Message> newMessages = [];
        HostSnapshot? builtSnapshot = null;

        // Executes one command. Returns the produced Input for request commands, or
        // null for fire-and-forget ones. Each case reproduces the pre-restructure
        // method's exact behavior (line refs are to HostMonitor.cs @ d3950c2).
        async Task<HostMachine.Input?> ExecuteAsync(HostMachine.Command cmd)
        {
            switch (cmd)
            {
                case HostMachine.Command.Probe p:
                    await ProbeAsync(p.point).ConfigureAwait(false);
                    return null;

                case HostMachine.Command.PublishStatus ps:
                    // rider A: daemon version is stamped ONLY at the Connected publish.
                    if (ps.stampDaemonVersion)
                        _daemonVersion = fetchedVersion;
                    DateTimeOffset? nextAttemptAt = FSharpOption<TimeSpan>.get_IsSome(ps.backoff)
                        ? _time.GetUtcNow() + ps.backoff.Value
                        : null;
                    SetStatus(ps.status, ps.attempt, nextAttemptAt, ToNullable(ps.error));
                    return null;

                case HostMachine.Command.RunTickRpcs t:
                    // The four tick RPCs in the old TickAsync order (d3950c2:581-585).
                    ccStatus = await client!.GetCcStatusAsync(connCt).ConfigureAwait(false);
                    results = await client!.GetResultsAsync(ct: connCt).ConfigureAwait(false);
                    transfers = await client!.GetFileTransfersAsync(connCt).ConfigureAwait(false);
                    newMessages = await client!.GetMessagesAsync(t.lastSeqno, connCt).ConfigureAwait(false);
                    // A result naming an uncached workunit means new work arrived since
                    // the last get_state (d3950c2:616-618): route triggers the refetch.
                    HashSet<string> knownWorkunits = [.. ccState!.Workunits.Select(w => w.Name)];
                    bool hasUnknownWorkunit = results.Any(r => !knownWorkunits.Contains(r.WorkunitName));
                    FSharpOption<int> maxSeqno = newMessages.Count > 0
                        ? FSharpOption<int>.Some(newMessages.Max(m => m.Seqno))
                        : FSharpOption<int>.None;
                    return HostMachine.Input.NewTickFetched(
                        new HostMachine.TickInfo(maxSeqno, hasUnknownWorkunit));

                case HostMachine.Command.PublishMessages pm:
                    // Exact old branching (d3950c2:600-613). The cursor advance itself
                    // is the core's job (applied at PostBuildGuard); here we only touch
                    // the log and raise MessagesAdded.
                    if (pm.replaceLog)
                    {
                        // First tick of this connection: atomically swap old-connection
                        // content for the daemon's current buffer (may be empty).
                        _messages.ReplaceAll(newMessages);
                        if (newMessages.Count > 0)
                            RaiseSafe(MessagesAdded, new MessagesAddedEventArgs(HostId, newMessages));
                    }
                    else if (newMessages.Count > 0)
                    {
                        _messages.Append(newMessages);
                        RaiseSafe(MessagesAdded, new MessagesAddedEventArgs(HostId, newMessages));
                    }
                    return null;

                case HostMachine.Command.WaitBackoff wb:
                    await WaitAsync(wb.Item, ct).ConfigureAwait(false);
                    return HostMachine.Input.WaitEnded;

                default:
                    // Fieldless commands, matched by tag (no nested type to cast to).
                    if (cmd.IsObserveDispatch)
                        return HostMachine.Input.NewDispatchObserved(ct.IsCancellationRequested);

                    if (cmd.IsSnapshotConfig)
                    {
                        // THE one atomic lock block (correspondence rule 1): snapshot
                        // config, clear _configChanged, and create the linked CTS in a
                        // SINGLE _gate acquisition. Splitting these reopens a window
                        // where _connectionCts is null and UpdateConfig's cancel becomes
                        // a no-op, letting a stale-config connect run unabortable until
                        // the OS TCP timeout (d3950c2:401-419, 425-431).
                        lock (_gate)
                        {
                            config = _config;
                            _configChanged = false;
                            _connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            connCt = _connectionCts.Token;
                        }
                        // Attempt payloads belong to the new attempt: reset here so a
                        // stale value can never survive into it.
                        client = null;
                        fetchedVersion = null;
                        ccState = null;
                        ccStatus = null;
                        results = [];
                        transfers = [];
                        newMessages = [];
                        builtSnapshot = null;
                        return HostMachine.Input.NewConfigSnapshotted(config.Password.Length > 0);
                    }

                    if (cmd.IsCreateClient)
                    {
                        // Factory only. A throwing factory — e.g. a future SSH-tunnel
                        // transport that fails before producing a client — folds to
                        // Failure via Classify (d3950c2:433-436, 443).
                        client = _clientFactory();
                        return null;
                    }

                    if (cmd.IsConnect)
                    {
                        await client!.ConnectAsync(config.Address, config.Port, connCt).ConfigureAwait(false);
                        return HostMachine.Input.EffectOk;
                    }

                    if (cmd.IsAuthorize)
                        return HostMachine.Input.NewAuthResult(
                            await client!.AuthorizeAsync(config.Password, connCt).ConfigureAwait(false));

                    if (cmd.IsFetchVersionAndState)
                    {
                        // Attempt-local until accepted: a failed attempt must not
                        // pollute Status with an unaccepted daemon version (I1).
                        fetchedVersion = await client!.ExchangeVersionsAsync(connCt).ConfigureAwait(false);
                        ccState = await client!.GetStateAsync(connCt).ConfigureAwait(false);
                        return HostMachine.Input.FetchOk;
                    }

                    if (cmd.IsRefetchState)
                    {
                        ccState = await client!.GetStateAsync(connCt).ConfigureAwait(false);
                        return HostMachine.Input.EffectOk;
                    }

                    if (cmd.IsBuildSnapshot)
                    {
                        // Pure in-memory build into an attempt local (d3950c2:630-631).
                        builtSnapshot = SnapshotBuilder.Build(
                            HostId, config.DisplayName, _time.GetUtcNow(),
                            ccState!, ccStatus!, results, transfers);
                        return null;
                    }

                    if (cmd.IsPublishSnapshot)
                    {
                        Snapshot = builtSnapshot;
                        RaiseSafe(SnapshotUpdated, builtSnapshot!);
                        return null;
                    }

                    if (cmd.IsObserveConfigChanged)
                        // Plain volatile read (d3950c2:465, 567, 572, 395).
                        return HostMachine.Input.NewGuardObserved(_configChanged);

                    if (cmd.IsObserveTickGuard)
                    {
                        // Throw-then-read: connCt cancellation aborts the tick before
                        // the guard is consulted (d3950c2:594-595, 625-626, 639-640).
                        connCt.ThrowIfCancellationRequested();
                        return HostMachine.Input.NewGuardObserved(_configChanged);
                    }

                    if (cmd.IsWaitPollInterval)
                    {
                        // Interval read at wait time (volatile, d3950c2:570).
                        await WaitAsync(TimeSpan.FromSeconds(_pollingIntervalSeconds), ct).ConfigureAwait(false);
                        return HostMachine.Input.WaitEnded;
                    }

                    if (cmd.IsParkForConfigChange)
                    {
                        await WaitForConfigChangeAsync(ct).ConfigureAwait(false);
                        return HostMachine.Input.WaitEnded;
                    }

                    if (cmd.IsDisposeConnectionCts)
                    {
                        lock (_gate)
                        {
                            _connectionCts?.Dispose();
                            _connectionCts = null;
                        }
                        return null;
                    }

                    if (cmd.IsDisposeClient)
                    {
                        // Swallow dispose failures: teardown must never fault the loop
                        // (d3950c2:513-517). client is null when the factory threw.
                        if (client is not null)
                        {
                            try { await client.DisposeAsync().ConfigureAwait(false); }
                            catch { /* ignored: dispose failures do not affect the state machine */ }
                        }
                        client = null;
                        return null;
                    }

                    // ExitLoop is handled by the driver before dispatch; any other
                    // fieldless command reaching here is an interpreter bug.
                    return null;
            }
        }

        try
        {
            while (true)
            {
                var result = HostMachine.step(state, input);
                state = result.Item1;
                FSharpList<HostMachine.Command> commands = result.Item2;
                input = HostMachine.Input.EffectOk;   // default when the batch has no request
                bool exit = false;
                foreach (var cmd in commands)
                {
                    if (cmd.IsExitLoop) { exit = true; break; }
                    try
                    {
                        HostMachine.Input? produced = await ExecuteAsync(cmd).ConfigureAwait(false);
                        if (produced is not null)
                            input = produced;
                    }
                    catch (Exception ex)
                    {
                        input = Classify(ex, ct);
                        break;                        // skip the rest of the batch
                    }
                }
                if (exit)
                    break;
            }
        }
        finally
        {
            // Defense in depth, NOT decision logic: the core always commands teardown
            // before waits (verified: I2), so these are no-ops on every verified path.
            // They exist so an unexpected shell exception can never leak a client —
            // BOINC daemons allow very few concurrent GUI RPC connections.
            lock (_gate)
            {
                _connectionCts?.Dispose();
                _connectionCts = null;
            }
            if (client is not null)
            {
                try { await client.DisposeAsync().ConfigureAwait(false); }
                catch { /* ignored */ }
            }
        }
    }

    // Exception classification: the shell looks ONLY at the exception type; routing by
    // phase/history is the core's job (see HostMachine.fs faultInAttempt / fault fold).
    private static HostMachine.Input Classify(Exception ex, CancellationToken outerCt) => ex switch
    {
        OperationCanceledException when outerCt.IsCancellationRequested =>
            HostMachine.Input.NewFaulted(HostMachine.FailureKind.Disposal),
        OperationCanceledException =>
            HostMachine.Input.NewFaulted(HostMachine.FailureKind.ConnCanceled),
        BoincUnauthorizedException u =>
            HostMachine.Input.NewFaulted(HostMachine.FailureKind.NewUnauthorized(u.Message)),
        _ =>
            HostMachine.Input.NewFaulted(HostMachine.FailureKind.NewFailure(ex.Message)),
    };

    // F# option -> C# nullable helpers (None is represented as null; use get_IsSome).
    private static TimeSpan? ToNullable(FSharpOption<TimeSpan> o) =>
        FSharpOption<TimeSpan>.get_IsSome(o) ? o.Value : null;

    private static string? ToNullable(FSharpOption<string> o) =>
        FSharpOption<string>.get_IsSome(o) ? o.Value : null;
}
