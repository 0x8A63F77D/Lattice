using Avalonia.Threading;

namespace Lattice.App.Infrastructure;

/// <summary>
/// The single 1 s heartbeat driving every countdown / relative-time string
/// (design rule: these tick as plain text swaps, never animate, and there are
/// no per-row timers).
/// </summary>
public interface IUiClock
{
    event EventHandler? Tick;
    DateTimeOffset Now { get; }
}

/// <summary>DispatcherTimer-backed clock; create on the UI thread.</summary>
public sealed class DispatcherUiClock : IUiClock, IDisposable
{
    private readonly DispatcherTimer _timer;

    public DispatcherUiClock()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick?.Invoke(this, EventArgs.Empty);
        _timer.Start();
    }

    public event EventHandler? Tick;
    public DateTimeOffset Now => DateTimeOffset.Now;
    public void Dispose() => _timer.Stop();
}
