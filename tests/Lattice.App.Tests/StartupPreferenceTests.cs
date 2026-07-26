using Lattice.App.Infrastructure;
using Lattice.App.Tests.Fakes;
using Xunit;

namespace Lattice.App.Tests;

/// <summary>
/// The rule that earns StartupPreference its own type (issue #187): the preference is persisted
/// ONLY after the OS record was actually written, so a toggle can never read back "on" with no
/// registration behind it.
/// </summary>
public class StartupPreferenceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}-ui.json");
    private readonly FakeStartupRegistration _registration = new();
    private readonly UiStateStore _store;
    private readonly StartupPreference _startup;

    public StartupPreferenceTests()
    {
        _store = new UiStateStore(_path);
        _startup = new StartupPreference(_store, _registration);
    }

    public void Dispose()
    {
        if (Directory.Exists(_path))
            Directory.Delete(_path, recursive: true);
        File.Delete(_path);
        File.Delete(_path + ".tmp"); // left behind when a Save races an unwritable target
    }

    [Fact]
    public void Both_preferences_default_to_off()
    {
        // Opt-in is the whole point: an app that adds itself to login items uninvited is malware.
        Assert.False(_startup.StartAtLogin);
        Assert.False(_startup.StartMinimized);
        Assert.Empty(_registration.Calls);
    }

    [Fact]
    public void StartAtLogin_reads_the_OS_record_not_a_persisted_copy()
    {
        // The record is the single source of truth, so a change made outside Lattice — the
        // desktop's own startup settings, an uninstall, a sync tool — is reflected immediately
        // and cannot be contradicted by a stored bool (Codex P2, PR #188).
        _registration.IsRegistered = true;
        Assert.True(_startup.StartAtLogin);

        _registration.IsRegistered = false;
        Assert.False(_startup.StartAtLogin);
    }

    [Fact]
    public void Enabling_registers_and_reads_back_from_the_record()
    {
        Assert.True(_startup.SetStartAtLogin(true));

        Assert.Equal([(true, false)], _registration.Calls);
        Assert.True(_startup.StartAtLogin);
    }

    [Fact]
    public void A_failed_registration_leaves_the_state_off()
    {
        _registration.Fails = true;

        Assert.False(_startup.SetStartAtLogin(true));

        Assert.False(_startup.StartAtLogin);
    }

    [Fact]
    public void Disabling_removes_the_registration()
    {
        _startup.SetStartAtLogin(true);
        _registration.Calls.Clear();

        Assert.True(_startup.SetStartAtLogin(false));

        Assert.Equal([(false, false)], _registration.Calls);
        Assert.False(_startup.StartAtLogin);
    }

    [Fact]
    public void Enabling_carries_the_stored_minimized_flag_into_the_record()
    {
        _startup.SetStartMinimized(true);
        _registration.Calls.Clear();

        _startup.SetStartAtLogin(true);

        Assert.Equal([(true, true)], _registration.Calls);
    }

    [Fact]
    public void Minimized_toggled_while_login_start_is_off_touches_no_record()
    {
        Assert.True(_startup.SetStartMinimized(true));

        Assert.Empty(_registration.Calls);
        Assert.True(_store.Load().StartMinimized);
    }

    [Fact]
    public void Minimized_toggled_while_registered_rewrites_the_record()
    {
        // The flag lives ONLY in the registered command line, so nothing else would carry it.
        _startup.SetStartAtLogin(true);
        _registration.Calls.Clear();

        Assert.True(_startup.SetStartMinimized(true));

        Assert.Equal([(true, true)], _registration.Calls);
        Assert.True(_store.Load().StartMinimized);
    }

    [Fact]
    public void A_failed_rewrite_leaves_the_minimized_preference_untouched()
    {
        _startup.SetStartAtLogin(true);
        _registration.Fails = true;

        Assert.False(_startup.SetStartMinimized(true));

        Assert.False(_store.Load().StartMinimized);
    }

    [Fact]
    public void SyncOnLaunch_heals_an_existing_registration()
    {
        _startup.SetStartAtLogin(true);
        _startup.SetStartMinimized(true);
        _registration.Calls.Clear();

        _startup.SyncOnLaunch();

        Assert.Equal([(true, true)], _registration.Calls);
    }

    [Fact]
    public void SyncOnLaunch_never_recreates_a_record_that_is_gone()
    {
        // The self-heal repairs a stale PATH; it is not a "put it back" loop. A user who
        // removed the entry through their desktop's startup settings — or an uninstall that
        // swept it — must not have it silently restored on the next manual launch
        // (Codex P2, PR #188). Stored StartMinimized is deliberately set to prove the
        // decision keys off the RECORD, not off any persisted state.
        _startup.SetStartMinimized(true);
        _registration.IsRegistered = false;
        _registration.Calls.Clear();

        _startup.SyncOnLaunch();

        Assert.Empty(_registration.Calls);
    }

    [Fact]
    public void SyncOnLaunch_leaves_an_OS_disabled_record_alone()
    {
        // On Linux the desktop's opt-out lives INSIDE our file, so the registration reports
        // itself unregistered; rewriting it would flip X-GNOME-Autostart-enabled back to true
        // and silently undo the user's explicit choice.
        _startup.SetStartAtLogin(true);
        _registration.IsRegistered = false;   // the desktop switched it off behind us
        _registration.Calls.Clear();

        _startup.SyncOnLaunch();

        Assert.Empty(_registration.Calls);
    }

    [Fact]
    public void Unsupported_launches_report_it_and_cannot_enable()
    {
        var unsupported = new StartupPreference(_store, new UnsupportedStartupRegistration());

        Assert.False(unsupported.IsSupported);
        Assert.False(unsupported.SetStartAtLogin(true));
        Assert.False(unsupported.StartAtLogin);
        // Turning it OFF is always achievable — there is nothing to remove.
        Assert.True(unsupported.SetStartAtLogin(false));
    }

    [Fact]
    public void A_minimized_change_that_cannot_be_persisted_puts_the_record_back()
    {
        // The OS record is writable but ui-state.json is not (Codex P2, PR #188). Leaving the
        // record carrying --minimized while the preference stays off would make the login item
        // and the toggle disagree, so the record is restored and the call reports failure.
        _startup.SetStartAtLogin(true);
        MakeStorePathUnwritable();
        try
        {
            _registration.Calls.Clear();

            Assert.False(_startup.SetStartMinimized(true));

            Assert.Equal([(true, true), (true, false)], _registration.Calls);
            Assert.False(_startup.StartMinimized);
        }
        finally
        {
            Directory.Delete(_path);
        }
    }

    [Fact]
    public void An_unpersistable_minimized_change_with_no_record_touches_nothing()
    {
        MakeStorePathUnwritable();
        try
        {
            Assert.False(_startup.SetStartMinimized(true));

            Assert.Empty(_registration.Calls);
        }
        finally
        {
            Directory.Delete(_path);
        }
    }

    /// <summary>Turns the state file's path into a directory, so the store's rename onto it
    /// throws and Save reports failure — the same trick SettingsViewModelTests uses.</summary>
    private void MakeStorePathUnwritable()
    {
        File.Delete(_path);
        Directory.CreateDirectory(_path);
    }
}
