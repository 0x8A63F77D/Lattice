namespace Lattice.App.Infrastructure;

/// <summary>
/// Single owner of the start-at-login pair (issue #187): the persisted preferences
/// (<see cref="UiState.StartAtLogin"/> / <see cref="UiState.StartMinimized"/>) and the OS
/// record that implements them. Same shape as <see cref="ThemePreference"/> /
/// <see cref="LanguagePreference"/>, with one extra rule that earns this type its own class:
///
/// <para>The preference is persisted ONLY when the OS write succeeds. A toggle that reads
/// back "on" while no registration exists is a lie the user cannot see through — so a failed
/// write leaves the stored value alone and the bound toggle snaps back.</para>
/// </summary>
public sealed class StartupPreference
{
    private readonly UiStateStore _store;
    private readonly IStartupRegistration _registration;

    public StartupPreference(UiStateStore store, IStartupRegistration registration)
    {
        _store = store;
        _registration = registration;
    }

    /// <summary>Whether this launch can be registered at all (see
    /// <see cref="IStartupRegistration.IsSupported"/>).</summary>
    public bool IsSupported => _registration.IsSupported;

    /// <summary>Read LIVE from the store at call time, never cached — the read-modify-write
    /// doctrine <see cref="UiStateStore.Update"/> exists for.</summary>
    public bool StartAtLogin => _store.Load().StartAtLogin;

    public bool StartMinimized => _store.Load().StartMinimized;

    /// <summary>Registers or removes the OS record, then persists on success.</summary>
    public bool SetStartAtLogin(bool value)
    {
        if (!_registration.Apply(value, StartMinimized))
            return false;
        _store.Update(s => s with { StartAtLogin = value });
        return true;
    }

    /// <summary>
    /// Persists the minimized-start choice, re-writing the login record when one is live.
    /// The flag lives in the REGISTERED command line, not in a startup-time preference read,
    /// so it applies to login launches only — starting Lattice by hand always shows the
    /// window. That is why this must rewrite the record: nothing else carries the change.
    /// </summary>
    public bool SetStartMinimized(bool value)
    {
        if (StartAtLogin && !_registration.Apply(true, value))
            return false;
        _store.Update(s => s with { StartMinimized = value });
        return true;
    }

    /// <summary>
    /// Path self-heal (#187 requirement 3), called once from the composition root. A moved,
    /// reinstalled or updated app leaves a record pointing at a path that no longer exists;
    /// re-applying on every launch while the toggle is on rewrites it to the binary that is
    /// actually running. Best-effort by design — a failure here has no user-visible surface
    /// to report into, and the next launch tries again. Does nothing while the toggle is off,
    /// so a user who turned the login item off in the OS's own settings is never fought with.
    /// </summary>
    public void SyncOnLaunch()
    {
        UiState state = _store.Load();
        if (state.StartAtLogin)
            _registration.Apply(true, state.StartMinimized);
    }
}
