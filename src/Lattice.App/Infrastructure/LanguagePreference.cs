namespace Lattice.App.Infrastructure;

/// <summary>Single owner of the app UI language (UiState.Language): live value +
/// persistence (#147). Mirrors <see cref="ThemePreference"/>'s shape, minus the runtime
/// Apply: language is read once at load via x:Static resource lookups, so a change takes
/// effect on the next launch. The composition root applies the persisted language's
/// culture at startup via <see cref="LanguageCulture.ApplyAtStartup"/>, before any UI is
/// built. Construction is pure (loads the persisted value; touches no global state).</summary>
public sealed class LanguagePreference
{
    private readonly UiStateStore _store;

    public LanguagePreference(UiStateStore store)
    {
        _store = store;
        Value = store.Load().Language;
    }

    public AppLanguage Value { get; private set; }

    public void Set(AppLanguage value)
    {
        if (Value == value) return;
        Value = value;
        // Read-modify-write through the store so a language write never clobbers a
        // sibling preference (theme, density, scope) persisted since load (PR #45 doctrine).
        _store.Update(s => s with { Language = value });
    }
}
