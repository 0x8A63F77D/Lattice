namespace Lattice.App.Infrastructure;

/// <summary>
/// Single owner of the global density preference (UiState.CompactDensity):
/// holds the LIVE in-memory value and its persistence. View-models mirror
/// this into their own IsCompact for XAML binding and subscribe to
/// <see cref="Changed"/> for cross-view sync — before this class existed,
/// TasksViewModel and TransfersViewModel each cached IsCompact at
/// construction and never observed later changes from the other, sibling,
/// long-lived view-model, so flipping density on one view left the other
/// showing the OLD density until app restart (Codex round-3 P2, PR #45).
/// </summary>
public sealed class DensityPreference
{
    private readonly UiStateStore _store;

    public DensityPreference(UiStateStore store)
    {
        _store = store;
        Value = store.Load().CompactDensity;
    }

    public bool Value { get; private set; }

    /// <summary>Raised after <see cref="Value"/> changes and the new value is
    /// persisted. Never raised for a no-op <see cref="Set"/>.</summary>
    public event EventHandler? Changed;

    /// <summary>No-op if <paramref name="value"/> equals the current
    /// <see cref="Value"/>. Otherwise updates <see cref="Value"/>, persists
    /// through the clobber-safe <see cref="UiStateStore.Update"/> (fresh load
    /// before mutate, so a concurrent write to another UiState field is never
    /// dropped), then raises <see cref="Changed"/>.</summary>
    public void Set(bool value)
    {
        if (Value == value) return;
        Value = value;
        _store.Update(s => s with { CompactDensity = value });
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
