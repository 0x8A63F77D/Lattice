using Lattice.App.Infrastructure;
using Xunit;

namespace Lattice.App.Tests;

/// <summary>
/// DensityPreference is the single owner of the global density preference
/// (UiState.CompactDensity): it holds the live in-memory value and its
/// persistence, so every density-toggling view-model mirrors ONE instance
/// instead of caching its own copy (Codex round-3 P2, PR #45 — the second
/// same-class finding around this shared preference, after the persistence
/// clobber UiStateStoreTests.Update_applies_the_mutation_to_a_freshly_loaded_state_not_a_stale_argument
/// covers).
/// </summary>
public class DensityPreferenceTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}-ui.json");

    [Fact]
    public void Constructor_initializes_Value_from_the_store()
    {
        var path = TempPath();
        try
        {
            var store = new UiStateStore(path);
            store.Save(UiState.Default with { CompactDensity = true });

            var density = new DensityPreference(store);

            Assert.True(density.Value);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Set_persists_through_the_store_so_a_fresh_load_sees_it()
    {
        var path = TempPath();
        try
        {
            var store = new UiStateStore(path);
            var density = new DensityPreference(store);
            Assert.False(density.Value);

            density.Set(true);

            Assert.True(density.Value);
            // A fresh UiStateStore.Load (not the DensityPreference's own cached
            // Value) is the real assertion: Set must have gone through
            // UiStateStore.Update, the clobber-safe write path, not just
            // updated the in-memory field.
            Assert.True(store.Load().CompactDensity);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Set_to_the_same_value_is_a_no_op_and_raises_no_Changed()
    {
        var path = TempPath();
        try
        {
            var store = new UiStateStore(path);
            var density = new DensityPreference(store);
            var raised = false;
            density.Changed += (_, _) => raised = true;

            density.Set(false); // already false at construction

            Assert.False(raised);
            Assert.False(density.Value);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Set_to_a_new_value_raises_Changed_exactly_once()
    {
        var path = TempPath();
        try
        {
            var store = new UiStateStore(path);
            var density = new DensityPreference(store);
            var raiseCount = 0;
            density.Changed += (_, _) => raiseCount++;

            density.Set(true);

            Assert.Equal(1, raiseCount);
            Assert.True(density.Value);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
