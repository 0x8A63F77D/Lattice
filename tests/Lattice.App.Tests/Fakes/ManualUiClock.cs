using Lattice.App.Infrastructure;

namespace Lattice.App.Tests.Fakes;

public sealed class ManualUiClock : IUiClock
{
    public event EventHandler? Tick;
    public DateTimeOffset Now { get; set; } = new(2026, 7, 9, 12, 0, 0, TimeSpan.Zero);
    public void Advance(TimeSpan by) { Now += by; Tick?.Invoke(this, EventArgs.Empty); }
}
