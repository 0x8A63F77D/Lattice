using Lattice.Boinc.GuiRpc;

namespace Lattice.Core;

/// <summary>Internal signal: authorization was refused; unwind to the AuthFailed state.</summary>
internal sealed class HostAuthException : Exception { }

/// <summary>
/// Internal signal: PollAsync deliberately escalated a second consecutive mid-poll
/// <see cref="BoincUnauthorizedException"/> after an already-successful silent re-auth.
/// This is a transient-session failure (not a bad password), so it must fall into
/// RunAsync's generic Retrying/backoff path rather than the pre-Connected
/// unauthorized-to-AuthFailed mapping.
/// </summary>
internal sealed class HostSessionLostException(Exception inner) : Exception(inner.Message, inner) { }

/// <summary>Payload of <see cref="HostMonitor.MessagesAdded"/>: only the newly arrived messages.</summary>
public sealed record MessagesAddedEventArgs(Guid HostId, IReadOnlyList<Message> Messages);

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
    // own. Guarded by _gate: created at the top of RunAttemptAsync, canceled from
    // UpdateConfig, disposed and nulled in RunAttemptAsync's finally.
    private CancellationTokenSource? _connectionCts;
    private volatile int _pollingIntervalSeconds;
    private TaskCompletionSource _wake = NewWake();
    private Task _loop = Task.CompletedTask;
    private bool _started;
    private VersionInfo? _daemonVersion;
    private ConnectionStatus _status;
    private HostSnapshot? _snapshot;

    /// <summary>Message buffer cap: the most recent 5,000 messages are retained per host.</summary>
    internal const int MessageCapacity = 5000;

    private readonly MessageLog _messages = new(MessageCapacity);
    private int _lastSeqno;

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
    /// The retained message buffer (oldest first, capped at 5,000). Cleared and
    /// refetched from seqno 0 at the start of every new connection (including
    /// reconnects), since a restarted daemon's seqno counter may have reset.
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
    /// Raised when a tick returns new messages; carries only the new ones. Because the
    /// message buffer resets on every reconnect, the first tick after a reconnect
    /// re-raises this for the daemon's current buffer from seqno 0, even if some of
    /// those messages were already seen before the disconnect.
    /// Subscriber exceptions are ignored — they never affect the state machine.
    /// </summary>
    public event EventHandler<MessagesAddedEventArgs>? MessagesAdded;

    /// <summary>Starts the background loop. Idempotent.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_started)
                return;
            _started = true;
            _loop = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
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

    /// <summary>Stops the loop, disposes the client, and settles in Disconnected.</summary>
    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        Wake();
        try { await _loop.ConfigureAwait(false); }
        catch { /* the loop reports failures via Status, never by throwing */ }
        _cts.Dispose();
    }

    /// <summary>Backoff schedule: 1, 2, 4, 8, 16, 32 then capped at 60 seconds.</summary>
    internal static TimeSpan BackoffDelay(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, attempt - 1)));

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
    // state machine. SetStatus publishes from INSIDE catch blocks (the AuthFailed and
    // Retrying publishes), where an escaping subscriber exception would propagate out
    // of RunAsync and fault the loop task silently; TickAsync's publishes would tear
    // down a healthy connection into Retrying instead. Subscribers marshal to their
    // own context (see class doc) and there is nowhere meaningful to report their
    // failures from here, so they are swallowed.
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

    // What one connection attempt did, as seen by the dispatcher. RunAttemptAsync folds
    // every path (including every exception) into one of these so the dispatcher never
    // touches a live client or an in-flight exception.
    private enum AttemptResult
    {
        Disposal,      // outer disposal token canceled: exit the loop.
        ConfigChanged, // aborted for a new config (or PollAsync returned): reconnect now.
        AuthFailed,    // password refused / unauthorized before Connected: park.
        Failed,        // any other failure: Retrying + backoff.
    }

    // ReachedConnected is only meaningful on a Failed outcome: true when the failure
    // happened AFTER Connected was published (i.e. inside PollAsync/TickAsync — a
    // healthy connection going bad mid-poll), false for a failure before Connected
    // was ever reached. The dispatcher uses it to tell "reset the attempt counter,
    // then count this as attempt 1" apart from "just count another failed attempt".
    private readonly record struct AttemptOutcome(AttemptResult Result, string? Error, bool ReachedConnected = false)
    {
        public static readonly AttemptOutcome Disposal = new(AttemptResult.Disposal, null);
        public static readonly AttemptOutcome ConfigChanged = new(AttemptResult.ConfigChanged, null);
        public static AttemptOutcome AuthFailed(string error) => new(AttemptResult.AuthFailed, error);
        public static AttemptOutcome Failed(string error, bool reachedConnected = false) =>
            new(AttemptResult.Failed, error, reachedConnected);
    }

    // The dispatcher. It never holds a client: each iteration hands the whole client
    // lifetime to RunAttemptAsync and only ever sees an AttemptOutcome. Structural
    // invariants that used to be maintained by hand at every exit path:
    //  (a) RunAttemptAsync owns the client — when it returns, this iteration's client
    //      and its linked CTS are already disposed (by scope, in its finally). So every
    //      park/backoff wait below provably runs with NO GUI RPC connection open. BOINC
    //      daemons allow very few concurrent GUI RPC connections; a lingering one can
    //      lock the user's official Manager out.
    //  (b) RunAttemptAsync cannot throw — its catch set folds every exception into an
    //      AttemptOutcome — so this loop can never fault the background task.
    //  (c) The AuthFailed/Retrying publishes below therefore happen after disposal; a
    //      buggy subscriber can neither escape (RaiseSafe swallows it) nor hold the
    //      already-closed connection open.
    private async Task RunAsync(CancellationToken ct)
    {
        int attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            HostConfig config;
            lock (_gate)
            {
                config = _config;
                _configChanged = false;
            }
            AttemptOutcome outcome = await RunAttemptAsync(config, attempt, ct).ConfigureAwait(false);

            if (outcome.Result == AttemptResult.Disposal)
                break;

            if (outcome.Result == AttemptResult.ConfigChanged)
            {
                attempt = 0;
                continue;
            }

            if (outcome.Result == AttemptResult.AuthFailed)
            {
                // Terminal until a config change: only UpdateConfig (never RequestRefresh)
                // revives it. Publishing here — after RunAttemptAsync's finally — is why a
                // refused-password connection is provably closed for the whole parked wait.
                SetStatus(HostConnectionState.AuthFailed, 0, lastError: outcome.Error);
                try { await WaitForConfigChangeAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                attempt = 0;
                continue;
            }

            // AttemptResult.Failed: back off, then retry. A config change during the wait
            // short-circuits the attempt count so the reconnect starts fresh. A failure
            // that happened AFTER Connected was published resets the counter first (the
            // prior run of failures is irrelevant once a connection succeeded) and only
            // then counts this failure, landing on attempt 1 — matching the pre-restructure
            // behavior where `attempt = 0` ran right before publishing Connected.
            attempt = outcome.ReachedConnected ? 1 : attempt + 1;
            TimeSpan delay = BackoffDelay(attempt);
            SetStatus(HostConnectionState.Retrying, attempt, _time.GetUtcNow() + delay, outcome.Error);
            try { await WaitAsync(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            if (_configChanged)
                attempt = 0;
        }
        SetStatus(HostConnectionState.Disconnected, 0);
    }

    // One connection attempt, cradle to grave. It owns the entire client lifetime:
    // it creates the linked _connectionCts (so UpdateConfig can cancel this attempt),
    // builds the client, walks Connecting→Authorizing→FetchingState→Connected→PollAsync,
    // and in its finally disposes both the CTS and the client. By the time it returns,
    // the connection is dead — by scope, not by convention — and every exception has
    // been folded into an AttemptOutcome, so nothing escapes. The attempt argument is
    // only for the pre-Connected status publishes (they describe this live attempt);
    // it is never reset in here — resetting the dispatcher's counter on success is the
    // dispatcher's job, driven by AttemptOutcome.ReachedConnected (see `connected` below).
    private async Task<AttemptOutcome> RunAttemptAsync(HostConfig config, int attempt, CancellationToken ct)
    {
        CancellationToken connCt;
        lock (_gate)
        {
            _connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connCt = _connectionCts.Token;
        }
        // Left null until the factory call succeeds so a throwing factory — e.g. a future
        // SSH-tunnel-wrapping transport that can fail before producing a client — folds
        // into the generic catch (→ Failed) instead of escaping this method.
        IGuiRpcClient? client = null;
        // Tracks whether Connected was published this attempt, so a later failure (from
        // PollAsync/TickAsync) can tell the dispatcher to reset the attempt counter
        // instead of continuing to count against the pre-Connected run of failures.
        bool connected = false;
        try
        {
            client = _clientFactory();
            SetStatus(HostConnectionState.Connecting, attempt);
            await client.ConnectAsync(config.Address, config.Port, connCt).ConfigureAwait(false);

            SetStatus(HostConnectionState.Authorizing, attempt);
            if (config.Password.Length > 0
                && !await client.AuthorizeAsync(config.Password, connCt).ConfigureAwait(false))
                throw new HostAuthException();

            SetStatus(HostConnectionState.FetchingState, attempt);
            _daemonVersion = await client.ExchangeVersionsAsync(connCt).ConfigureAwait(false);
            CcState state = await client.GetStateAsync(connCt).ConfigureAwait(false);

            // A new connection may be talking to a freshly (re)started daemon whose
            // seqno counter reset to zero: a stale high _lastSeqno would silently
            // miss every message it now considers new. Reset the cursor and the
            // retained buffer so the first tick refetches the daemon's current
            // buffer from scratch (matches official BOINC Manager semantics).
            _lastSeqno = 0;
            _messages.Clear();

            // A config change may have landed after the fetch above completed but
            // before Connected is published: never surface Connected (or the
            // snapshot/messages that would follow) for a connection to the OLD
            // config. The finally below tears this client down; the dispatcher
            // reconnects against the new config on the very next iteration.
            if (_configChanged)
                return AttemptOutcome.ConfigChanged;
            SetStatus(HostConnectionState.Connected, 0);
            connected = true;
            await PollAsync(client, config, state, connCt, ct).ConfigureAwait(false);
            // PollAsync returns only on config change: reconnect immediately.
            return AttemptOutcome.ConfigChanged;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return AttemptOutcome.Disposal;
        }
        catch (OperationCanceledException)
        {
            // Not disposal: UpdateConfig canceled connCt to abort in-flight work on
            // the old config. Reconnect immediately against the new one, no backoff.
            return AttemptOutcome.ConfigChanged;
        }
        catch (HostAuthException)
        {
            return AttemptOutcome.AuthFailed("The host refused the password.");
        }
        catch (BoincUnauthorizedException ex)
        {
            // The real client THROWS (rather than returning false) when a remote
            // host isn't allow-listed, or when a mid-connect RPC (exchange_versions,
            // get_state) is unauthorized before Connected is ever reached. Same
            // terminal handling as a refused password: never spin forever on it.
            return AttemptOutcome.AuthFailed(ex.Message);
        }
        catch (Exception ex)
        {
            return AttemptOutcome.Failed(ex.Message, connected);
        }
        finally
        {
            lock (_gate)
            {
                _connectionCts?.Dispose();
                _connectionCts = null;
            }
            // The connection is being torn down regardless of outcome; a disposal
            // failure has nowhere meaningful to go and must not fault the loop.
            // client is null when the factory itself threw before producing one.
            if (client is not null)
            {
                try { await client.DisposeAsync().ConfigureAwait(false); }
                catch { /* ignored: dispose failures do not affect the state machine */ }
            }
        }
    }

    // Steady state: tick immediately on entry (first snapshot right after Connected),
    // then wait the polling interval between ticks. Returns only on config change.
    // connCt cancels RPC calls (including the silent re-auth) when the config changes
    // mid-tick; ct (the outer disposal token) is used for the interval wait, whose
    // existing wake/flag mechanism already handles config changes without needing
    // cancellation — and must not be aborted into a tight retry loop on cancel.
    private async Task PollAsync(IGuiRpcClient client, HostConfig config, CcState state,
                                 CancellationToken connCt, CancellationToken ct)
    {
        bool reauthedSinceLastSuccess = false;
        while (true)
        {
            try
            {
                state = await TickAsync(client, config, state, connCt).ConfigureAwait(false);
                reauthedSinceLastSuccess = false;
            }
            catch (BoincUnauthorizedException ex)
            {
                // Daemon restarted with a new password, or the session expired: one
                // silent re-auth per successful tick. If we already re-authed since
                // the last successful tick and are still unauthorized, the daemon is
                // accepting AuthorizeAsync but refusing RPCs regardless — escalate as a
                // plain (non-BoincUnauthorizedException) failure so RunAsync's generic
                // handler moves the host to Retrying with backoff, instead of either
                // hammering the daemon in a zero-delay loop or being mapped to the
                // pre-Connected unauthorized-to-AuthFailed path (this is a transient
                // mid-session failure, not a refused password).
                if (reauthedSinceLastSuccess)
                    throw new HostSessionLostException(ex);
                if (config.Password.Length == 0
                    || !await client.AuthorizeAsync(config.Password, connCt).ConfigureAwait(false))
                    throw new HostAuthException();
                reauthedSinceLastSuccess = true;
                continue;
            }
            if (_configChanged)
                return;
            await WaitAsync(TimeSpan.FromSeconds(_pollingIntervalSeconds), ct).ConfigureAwait(false);
            if (_configChanged)
                return;
        }
    }

    private async Task<CcState> TickAsync(IGuiRpcClient client, HostConfig config, CcState state, CancellationToken ct)
    {
        CcStatus ccStatus = await client.GetCcStatusAsync(ct).ConfigureAwait(false);
        IReadOnlyList<Result> results = await client.GetResultsAsync(ct: ct).ConfigureAwait(false);
        IReadOnlyList<FileTransfer> transfers = await client.GetFileTransfersAsync(ct).ConfigureAwait(false);

        IReadOnlyList<Message> newMessages = await client.GetMessagesAsync(_lastSeqno, ct).ConfigureAwait(false);

        // A config change may have landed (and canceled connCt) while get_messages was
        // in flight: never append/publish messages fetched from the OLD connection.
        // This mirrors the guard below the state refetch — that one protects the
        // snapshot, this one protects the message log and MessagesAdded, which would
        // otherwise be able to fire under this HostId with stale-connection data even
        // though the snapshot guard has not been reached yet.
        ct.ThrowIfCancellationRequested();
        if (_configChanged)
            return state;
        if (newMessages.Count > 0)
        {
            _lastSeqno = newMessages.Max(m => m.Seqno);
            _messages.Append(newMessages);
            RaiseSafe(MessagesAdded, new MessagesAddedEventArgs(HostId, newMessages));
        }

        // A result naming a workunit we haven't cached means new work arrived since
        // the last get_state: re-fetch the join tables once.
        HashSet<string> knownWorkunits = [.. state.Workunits.Select(w => w.Name)];
        if (results.Any(r => !knownWorkunits.Contains(r.WorkunitName)))
            state = await client.GetStateAsync(ct).ConfigureAwait(false);

        // A config change may have landed (and canceled connCt) while the RPCs above
        // were in flight: never publish a snapshot that could straddle the old and
        // new config under this HostId.
        ct.ThrowIfCancellationRequested();
        if (_configChanged)
            return state;

        HostSnapshot snapshot = SnapshotBuilder.Build(
            HostId, config.DisplayName, _time.GetUtcNow(), state, ccStatus, results, transfers);
        Snapshot = snapshot;
        RaiseSafe(SnapshotUpdated, snapshot);
        return state;
    }
}
