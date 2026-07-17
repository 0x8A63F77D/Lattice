using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using Lattice.Core;

namespace Lattice.App.Infrastructure;

/// <summary>
/// The outcome of <see cref="SingleInstanceGuard.TryAcquire"/>. A small closed
/// hierarchy so the call site (Program.Main) switches exhaustively over the three
/// cases; only <see cref="Acquired"/> carries the live guard.
/// </summary>
public abstract record AcquireResult
{
    private AcquireResult() { }

    /// <summary>This process is the primary: it holds the lock and its activation
    /// listener is already live.</summary>
    public sealed record Acquired(SingleInstanceGuard Guard) : AcquireResult;

    /// <summary>The lock is held — <em>probably</em> by a live primary. Only a
    /// <see cref="SingleInstanceGuard.SignalPrimary"/> round-trip that is actually
    /// answered confirms it (I-GUARD): a held lock with no answering listener means
    /// a dying/foreign holder, and the launch proceeds.</summary>
    public sealed record Contended : AcquireResult;

    /// <summary>The guard could not operate (unusable path, permissions, path-shape
    /// error). This is <em>not</em> evidence another instance exists — the launch
    /// proceeds fail-open, without the guard.</summary>
    public sealed record Unavailable : AcquireResult;
}

/// <summary>
/// Single-instance guard for #92 (Q3): an exclusive lock file decides primary vs.
/// secondary, and a named pipe lets a secondary ask the primary to surface its
/// window. Avalonia has no built-in support (plan F11), so this uses OS primitives.
///
/// Invariant I-GUARD: the guard may end a launch ONLY when a live primary actually
/// answers the activation ping. Every failure that is not a confirmed live primary
/// (unreadable/read-only/mis-shaped lock path, permission change, missing dir, pipe
/// errors) degrades to launching WITHOUT the guard — a broken lock file must never
/// brick a monitoring app (same philosophy as App.LoadRegistryWithFallback). A held
/// lock only yields <see cref="AcquireResult.Contended"/> (probable contention); the
/// pipe round-trip in Program.Main is the liveness oracle that confirms or refutes it.
///
/// The OS releases the lock on process death, so there is no stale-lock recovery.
/// A named <c>Mutex</c> was rejected because <c>Local\</c> is logon-session-scoped
/// and would let two Unix terminal sessions each acquire it — exactly the raw-binary
/// double-launch case this guard exists to catch.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private static readonly UnixFileMode UserOnlyDir =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>Connect timeout for the activation ping — generously above the few
    /// milliseconds between the lock open and the pipe listen inside TryAcquire.</summary>
    private const int SignalTimeoutMs = 2000;

    private readonly FileStream _lockStream;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _listener;
    private readonly object _sync = new();

    private Action _onActivate = static () => { };
    private NamedPipeServerStream? _currentServer;
    private bool _disposed;

    private SingleInstanceGuard(FileStream lockStream, string pipeName, NamedPipeServerStream firstServer)
    {
        _lockStream = lockStream;
        _pipeName = pipeName;
        _listener = Task.Run(() => ListenLoopAsync(firstServer, _cts.Token));
    }

    /// <summary>The default lock file: <c>instance.lock</c> beside <c>config.json</c>
    /// in the per-user Lattice config dir.</summary>
    public static string DefaultLockPath =>
        Path.Combine(Path.GetDirectoryName(LatticeConfig.DefaultPath)!, "instance.lock");

    /// <summary>The default activation pipe name, suffixed per-user so it never
    /// collides with another user's Lattice (pipe endpoints are machine-global on
    /// Windows and live in a shared temp dir on Unix).</summary>
    public static string DefaultPipeName =>
        "lattice-activate-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName)))[..16].ToLowerInvariant();

    /// <summary>
    /// Attempts to become the primary. On success the activation listener is started
    /// BEFORE returning (R5 atomicity): lock-acquire + listen are one step as observed
    /// from outside, so a held lock with no answering listener can only be a dying or
    /// foreign holder — never a primary merely slow to boot. Classification:
    /// success → <see cref="AcquireResult.Acquired"/>; lock-contention IOException →
    /// <see cref="AcquireResult.Contended"/>; permission/path-shape errors →
    /// <see cref="AcquireResult.Unavailable"/> (the guard cannot operate; fail-open).
    /// </summary>
    public static AcquireResult TryAcquire(string lockPath, string pipeName)
    {
        try
        {
            EnsureParentDirectory(lockPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cannot even prepare the lock dir: the guard cannot operate.
            return new AcquireResult.Unavailable();
        }

        FileStream lockStream;
        try
        {
            lockStream = new FileStream(
                lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (UnauthorizedAccessException)
        {
            // Permission denied, or the path is a directory: not another instance.
            return new AcquireResult.Unavailable();
        }
        catch (DirectoryNotFoundException)
        {
            // DirectoryNotFoundException derives from IOException — classify it as a
            // path-shape error (Unavailable) BEFORE the generic IOException arm.
            return new AcquireResult.Unavailable();
        }
        catch (IOException)
        {
            // FileShare.None sharing violation on both platforms: the lock is held.
            return new AcquireResult.Contended();
        }

        // Lock held. Start the listener atomically before returning (R5).
        try
        {
            var firstServer = CreatePipeServer(pipeName);
            return new AcquireResult.Acquired(new SingleInstanceGuard(lockStream, pipeName, firstServer));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Held the lock but cannot listen: fail-open rather than run a primary
            // that no secondary can ever reach.
            lockStream.Dispose();
            return new AcquireResult.Unavailable();
        }
    }

    /// <summary>Swaps the callback invoked when a secondary pings this primary. The
    /// initial value is a no-op (a cold-starting primary is about to show its window
    /// anyway); callers marshal to the UI thread inside the supplied action.</summary>
    public void SetActivationCallback(Action onActivate) => Volatile.Write(ref _onActivate, onActivate);

    /// <summary>
    /// Signals the primary listening on <paramref name="pipeName"/>: connect (2 s
    /// timeout), write one byte, return whether it succeeded. Never throws — a failure
    /// means no live primary answered, which the caller reads as refuted contention.
    /// </summary>
    public static bool SignalPrimary(string pipeName)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
            client.Connect(SignalTimeoutMs);
            client.WriteByte((byte)'A');
            client.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            // Unblock a pending WaitForConnectionAsync promptly (belt-and-braces with
            // the cancellation token, whose pipe support varies by platform).
            _currentServer?.Dispose();
        }

        _cts.Cancel();
        // Bounded wait so the pipe endpoint is torn down before we return; the loop
        // observes cancellation immediately once the server is disposed.
        try { _listener.Wait(TimeSpan.FromSeconds(1)); } catch { /* loop teardown races are benign */ }
        _cts.Dispose();
        _lockStream.Dispose(); // releases the OS lock
    }

    private async Task ListenLoopAsync(NamedPipeServerStream firstServer, CancellationToken ct)
    {
        var server = firstServer;
        while (!ct.IsCancellationRequested)
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    server.Dispose();
                    return;
                }
                _currentServer = server;
            }

            try
            {
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                var buffer = new byte[1];
                int read = await server.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read == 1)
                    Volatile.Read(ref _onActivate)();
            }
            catch (OperationCanceledException)
            {
                return; // shutdown requested; the finally still disposes this server
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // A dying secondary (or our own Dispose) must not kill the listener.
            }
            finally
            {
                server.Dispose();
            }

            if (ct.IsCancellationRequested)
                return;

            try
            {
                server = CreatePipeServer(_pipeName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return; // cannot re-establish the endpoint: stop listening (fail-open)
            }
        }
    }

    private static NamedPipeServerStream CreatePipeServer(string pipeName) =>
        new(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

    private static void EnsureParentDirectory(string lockPath)
    {
        string? dir = Path.GetDirectoryName(lockPath);
        if (dir is not { Length: > 0 })
            return;
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(dir);
        else
            Directory.CreateDirectory(dir, UserOnlyDir);
    }
}
