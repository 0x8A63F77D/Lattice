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

    /// <summary>
    /// Read from the OS RECORD, not from a persisted copy (Codex P2, PR #188). Storing this
    /// alongside the record gave one fact two owners, and every way they could drift was a bug:
    /// a ui-state save that failed after the record was written left the toggle contradicting
    /// the OS, and a user who switched the login item off in their desktop's own settings still
    /// read back "on". With the record as the sole source of truth those states cannot be
    /// constructed.
    ///
    /// <para>It is the truth we can SEE, which is not always the whole truth: where the OS
    /// records its own opt-out somewhere we cannot read (macOS's root-only Background Task
    /// Management database, Windows' <c>StartupApproved</c> key) this reads "registered" for
    /// an item the OS will skip. Only Linux writes that state into the record itself, so only
    /// there is it visible. The limit is measured, documented in README and on the #116
    /// checklist, and NOT papered over with a guess.</para>
    /// </summary>
    public bool StartAtLogin => _registration.IsRegistered;

    /// <summary>Read LIVE from the store at call time, never cached — the read-modify-write
    /// doctrine <see cref="UiStateStore.Update"/> exists for. This one IS persisted: it has to
    /// survive an off/on cycle, during which no record exists to carry it.</summary>
    public bool StartMinimized => _store.Load().StartMinimized;

    /// <summary>
    /// Registers or removes the OS record. Nothing to persist — the record itself is what
    /// <see cref="StartAtLogin"/> reads back.
    ///
    /// <para>Success means the OS now REFLECTS the request, not merely that the write did not
    /// throw (Codex P2, PR #188). Anything that leaves the requested state unreached — a
    /// disable we could not clear, a record something else removed underneath us — surfaces
    /// as a failure the user can see, rather than a toggle that silently springs back.</para>
    /// </summary>
    public bool SetStartAtLogin(bool value) =>
        _registration.Apply(value, StartMinimized) && _registration.IsRegistered == value;

    /// <summary>
    /// Persists the minimized-start choice, re-writing the login record when one is live.
    /// The flag lives in the REGISTERED command line, not in a startup-time preference read,
    /// so it applies to login launches only — starting Lattice by hand always shows the
    /// window. That is why this must rewrite the record: nothing else carries the change.
    /// </summary>
    public bool SetStartMinimized(bool value)
    {
        // Heal, not Apply: changing this flag rewrites a live record's CONTENT and must never
        // be read as a request to register or to undo an OS-level disable. When nothing is
        // registered it is a no-op and only the preference moves.
        bool registered = _registration.IsRegistered;
        if (!_registration.Heal(value))
            return false;
        if (_store.TryUpdate(s => s with { StartMinimized = value }))
            return true;

        // The record now carries a flag the preference does not (Codex P2, PR #188). Put the
        // record back so the two never disagree — otherwise the login item would keep starting
        // minimized while the toggle, restored from the un-saved preference, reads off.
        if (registered)
            _registration.Heal(!value);
        return false;
    }

    /// <summary>
    /// Path self-heal (#187 requirement 3), called once from the composition root. A moved,
    /// reinstalled or updated app leaves a record pointing at a path that no longer exists;
    /// rewriting it on launch points it back at the binary that is actually running.
    ///
    /// <para>It REPAIRS an existing registration and never creates one. That is the exact
    /// scope of the requirement — a stale path — and it is what keeps the self-heal from
    /// undoing an OS-level opt-out (Codex P2, PR #188): a user who deleted the entry in their
    /// desktop's startup settings does not get it silently recreated, and one who merely
    /// disabled it reads as unregistered here (see <see cref="StartAtLogin"/>), so it is left
    /// alone rather than rewritten back to enabled.</para>
    ///
    /// <para>Best-effort by design — a failure here has no user-visible surface to report
    /// into, and the next launch tries again.</para>
    /// </summary>
    public void SyncOnLaunch() => _registration.Heal(_store.Load().StartMinimized);
}
