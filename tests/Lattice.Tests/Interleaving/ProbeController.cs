using Lattice.Core;

namespace Lattice.Tests.Interleaving;

/// <summary>
/// Deterministic freeze/release controller for HostMonitor's probe seam. Arm a
/// point with FreezeAt; the loop blocks there; the test observes via WaitForAsync,
/// performs an environment action, then Release()s. Unarmed points pass through.
/// </summary>
internal sealed class ProbeController
{
    private readonly object _sync = new();
    private string? _armed;
    private TaskCompletionSource _reached = NewTcs();
    private TaskCompletionSource _release = NewTcs();

    private static TaskCompletionSource NewTcs() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Probe(string point)
    {
        lock (_sync)
        {
            if (_armed != point)
                return Task.CompletedTask;
            _armed = null;                 // one-shot
            _reached.TrySetResult();
            return _release.Task;
        }
    }

    public void FreezeAt(string point)
    {
        lock (_sync)
        {
            _armed = point;
            _reached = NewTcs();
            _release = NewTcs();
        }
    }

    public Task WaitForAsync(string point) => _reached.Task.WaitAsync(TimeSpan.FromSeconds(10));

    public void Release()
    {
        lock (_sync)
            _release.TrySetResult();
    }

    public void Disarm()
    {
        lock (_sync)
        {
            _armed = null;
            _release.TrySetResult();
        }
    }
}
