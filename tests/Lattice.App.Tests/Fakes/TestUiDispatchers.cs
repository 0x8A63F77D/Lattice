using Lattice.App.Infrastructure;

namespace Lattice.App.Tests.Fakes;

/// <summary>Runs posted work inline — for tests that drive events synchronously.</summary>
public sealed class ImmediateUiDispatcher : IUiDispatcher
{
    public bool CheckAccess() => true;
    public void Post(Action action) => action();
}

/// <summary>Queues posted work for explicit draining — for ordering-sensitive tests.</summary>
public sealed class QueueUiDispatcher : IUiDispatcher
{
    private readonly Queue<Action> _queue = new();
    private readonly object _gate = new();
    public bool CheckAccess() => true;
    public void Post(Action action) { lock (_gate) _queue.Enqueue(action); }
    public int Pending { get { lock (_gate) return _queue.Count; } }
    public int Drain()
    {
        var n = 0;
        while (true)
        {
            Action? next;
            lock (_gate) { if (!_queue.TryDequeue(out next)) return n; }
            next(); n++;
        }
    }
}

/// <summary>
/// Runs posted work inline (no deferral, so Wait.UntilAsync polling still
/// works) but serialized under a lock. Plain ImmediateUiDispatcher runs each
/// post on whatever thread called it; with two ACTUALLY RUNNING monitors
/// (unlike every other fixture using it, which never starts more than one
/// live monitor at a time) that means two background threads can both be
/// inside HostStore.Changed -> the VM's Rebuild() at once, racing the
/// unsynchronized ObservableCollection Clear()+Add(). Production never hits
/// this: the real IUiDispatcher marshals every post onto one UI thread.
/// Shared by the multi-live-monitor VM fixtures (Tasks/Transfers/Projects).
/// </summary>
public sealed class LockingUiDispatcher : IUiDispatcher
{
    private readonly object _gate = new();
    public bool CheckAccess() => true;
    public void Post(Action action) { lock (_gate) action(); }
}
