using Lattice.Boinc.GuiRpc;

namespace Lattice.Core;

/// <summary>Internal signal: authorization was refused; unwind to the AuthFailed state.</summary>
internal sealed class HostAuthException : Exception { }

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

    /// <summary>The retained message buffer (oldest first, capped at 5,000).</summary>
    public IReadOnlyList<Message> Messages => _messages.Snapshot();

    /// <summary>Raised once per state transition, from the monitor's loop context.</summary>
    public event EventHandler<ConnectionStatus>? StatusChanged;

    /// <summary>Raised after every poll tick with the freshly built snapshot.</summary>
    public event EventHandler<HostSnapshot>? SnapshotUpdated;

    /// <summary>Raised when a tick returns new messages; carries only the new ones.</summary>
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

    /// <summary>Applies a new address/port/password: tears down and reconnects from scratch.</summary>
    public void UpdateConfig(HostConfig config)
    {
        if (config.Id != HostId)
            throw new ArgumentException($"Config id {config.Id} does not match host {HostId}.", nameof(config));
        lock (_gate)
        {
            _config = config;
            _configChanged = true;
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

    private void SetStatus(HostConnectionState state, int attempt,
                           DateTimeOffset? nextAttemptAt = null, string? lastError = null)
    {
        var status = new ConnectionStatus(HostId, state, attempt, nextAttemptAt, lastError, _daemonVersion);
        Status = status;
        StatusChanged?.Invoke(this, status);
    }

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
            IGuiRpcClient client = _clientFactory();
            try
            {
                SetStatus(HostConnectionState.Connecting, attempt);
                await client.ConnectAsync(config.Address, config.Port, ct).ConfigureAwait(false);

                SetStatus(HostConnectionState.Authorizing, attempt);
                if (config.Password.Length > 0
                    && !await client.AuthorizeAsync(config.Password, ct).ConfigureAwait(false))
                    throw new HostAuthException();

                SetStatus(HostConnectionState.FetchingState, attempt);
                _daemonVersion = await client.ExchangeVersionsAsync(ct).ConfigureAwait(false);
                CcState state = await client.GetStateAsync(ct).ConfigureAwait(false);

                attempt = 0;
                SetStatus(HostConnectionState.Connected, 0);
                await PollAsync(client, config, state, ct).ConfigureAwait(false);
                // PollAsync returns only on config change: reconnect immediately.
                attempt = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (HostAuthException)
            {
                SetStatus(HostConnectionState.AuthFailed, 0, lastError: "The host refused the password.");
                try { await WaitForConfigChangeAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                attempt = 0;
            }
            catch (Exception ex)
            {
                attempt++;
                TimeSpan delay = BackoffDelay(attempt);
                SetStatus(HostConnectionState.Retrying, attempt, _time.GetUtcNow() + delay, ex.Message);
                try { await WaitAsync(delay, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                if (_configChanged)
                    attempt = 0;
            }
            finally
            {
                // The connection is being torn down regardless of outcome; a disposal
                // failure has nowhere meaningful to go and must not fault the loop.
                try { await client.DisposeAsync().ConfigureAwait(false); }
                catch { /* ignored: dispose failures do not affect the state machine */ }
            }
        }
        SetStatus(HostConnectionState.Disconnected, 0);
    }

    // Steady state: tick immediately on entry (first snapshot right after Connected),
    // then wait the polling interval between ticks. Returns only on config change.
    private async Task PollAsync(IGuiRpcClient client, HostConfig config, CcState state, CancellationToken ct)
    {
        while (true)
        {
            try
            {
                state = await TickAsync(client, config, state, ct).ConfigureAwait(false);
            }
            catch (BoincUnauthorizedException)
            {
                // Daemon restarted with a new password, or the session expired:
                // one silent re-auth, then AuthFailed if still refused.
                if (config.Password.Length == 0
                    || !await client.AuthorizeAsync(config.Password, ct).ConfigureAwait(false))
                    throw new HostAuthException();
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
        if (newMessages.Count > 0)
        {
            _lastSeqno = newMessages.Max(m => m.Seqno);
            _messages.Append(newMessages);
            MessagesAdded?.Invoke(this, new MessagesAddedEventArgs(HostId, newMessages));
        }

        // A result naming a workunit we haven't cached means new work arrived since
        // the last get_state: re-fetch the join tables once.
        HashSet<string> knownWorkunits = [.. state.Workunits.Select(w => w.Name)];
        if (results.Any(r => !knownWorkunits.Contains(r.WorkunitName)))
            state = await client.GetStateAsync(ct).ConfigureAwait(false);

        HostSnapshot snapshot = SnapshotBuilder.Build(
            HostId, config.DisplayName, _time.GetUtcNow(), state, ccStatus, results, transfers);
        Snapshot = snapshot;
        SnapshotUpdated?.Invoke(this, snapshot);
        return state;
    }
}
