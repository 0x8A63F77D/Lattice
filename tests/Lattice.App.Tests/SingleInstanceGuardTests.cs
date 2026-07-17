using Lattice.App.Infrastructure;
using Xunit;

namespace Lattice.App.Tests;

/// <summary>
/// Pins invariant I-GUARD (#92 PR A): the guard may end a launch ONLY when a live
/// primary answers the activation ping. A held lock signals *probable* contention;
/// an unusable lock (bad path / permissions) degrades to fail-open (Unavailable),
/// never to "another instance exists". The listener is live from TryAcquire itself
/// (R5 atomicity), so SignalPrimary works before any callback is set.
/// </summary>
public class SingleInstanceGuardTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lattice-guard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    // Pipe endpoints live in a shared system temp dir, not our lock dir; keep them
    // unique per test so parallel/repeat runs never collide. Kept short: macOS maps
    // pipe names to Unix-domain-socket paths capped at 104 chars, and the system temp
    // prefix already eats most of that (the production default name is well under it).
    private static string UniquePipe() => "lat-" + Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public void Second_acquire_on_the_same_lock_path_is_contended()
    {
        var dir = TempDir();
        var lockPath = Path.Combine(dir, "instance.lock");
        try
        {
            var first = Assert.IsType<AcquireResult.Acquired>(
                SingleInstanceGuard.TryAcquire(lockPath, UniquePipe()));
            try
            {
                var second = SingleInstanceGuard.TryAcquire(lockPath, UniquePipe());
                Assert.IsType<AcquireResult.Contended>(second);
            }
            finally
            {
                first.Guard.Dispose();
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Re_acquire_after_dispose_succeeds()
    {
        var dir = TempDir();
        var lockPath = Path.Combine(dir, "instance.lock");
        try
        {
            var first = Assert.IsType<AcquireResult.Acquired>(
                SingleInstanceGuard.TryAcquire(lockPath, UniquePipe()));
            first.Guard.Dispose();

            var second = Assert.IsType<AcquireResult.Acquired>(
                SingleInstanceGuard.TryAcquire(lockPath, UniquePipe()));
            second.Guard.Dispose();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Lock_path_that_is_a_directory_is_unavailable()
    {
        var dir = TempDir();
        // The lock path itself is an existing directory: opening it as a file fails
        // with a path-shape error, which classifies as Unavailable (fail-open),
        // NOT Contended (I-GUARD: this is not another instance holding the lock).
        var lockPath = Path.Combine(dir, "instance.lock");
        Directory.CreateDirectory(lockPath);
        try
        {
            var result = SingleInstanceGuard.TryAcquire(lockPath, UniquePipe());
            Assert.IsType<AcquireResult.Unavailable>(result);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SignalPrimary_with_no_primary_returns_false()
    {
        // Nobody ever acquired this pipe name: the connect times out and the signal
        // fails without throwing.
        Assert.False(SingleInstanceGuard.SignalPrimary(UniquePipe()));
    }

    [Fact]
    public void SignalPrimary_succeeds_immediately_after_acquire_without_a_callback()
    {
        // R5 atomicity: the pipe listener is live from TryAcquire itself, so a signal
        // is answered even before any SetActivationCallback call.
        var dir = TempDir();
        var lockPath = Path.Combine(dir, "instance.lock");
        var pipe = UniquePipe();
        try
        {
            var acquired = Assert.IsType<AcquireResult.Acquired>(
                SingleInstanceGuard.TryAcquire(lockPath, pipe));
            try
            {
                Assert.True(SingleInstanceGuard.SignalPrimary(pipe));
            }
            finally
            {
                acquired.Guard.Dispose();
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Activation_callback_runs_on_signal_round_trip()
    {
        var dir = TempDir();
        var lockPath = Path.Combine(dir, "instance.lock");
        var pipe = UniquePipe();
        try
        {
            var acquired = Assert.IsType<AcquireResult.Acquired>(
                SingleInstanceGuard.TryAcquire(lockPath, pipe));
            try
            {
                var activated = new TaskCompletionSource();
                acquired.Guard.SetActivationCallback(() => activated.TrySetResult());

                Assert.True(SingleInstanceGuard.SignalPrimary(pipe));

                // Settle on the observed callback; the timeout is only a safety net
                // so a wiring bug fails the test instead of hanging it.
                await activated.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                acquired.Guard.Dispose();
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
