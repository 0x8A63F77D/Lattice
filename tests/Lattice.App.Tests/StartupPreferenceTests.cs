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

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void Both_preferences_default_to_off()
    {
        // Opt-in is the whole point: an app that adds itself to login items uninvited is malware.
        Assert.False(_startup.StartAtLogin);
        Assert.False(_startup.StartMinimized);
        Assert.Empty(_registration.Calls);
    }

    [Fact]
    public void Enabling_registers_first_then_persists()
    {
        Assert.True(_startup.SetStartAtLogin(true));

        Assert.Equal([(true, false)], _registration.Calls);
        Assert.True(_store.Load().StartAtLogin);
    }

    [Fact]
    public void A_failed_registration_leaves_the_preference_untouched()
    {
        _registration.Fails = true;

        Assert.False(_startup.SetStartAtLogin(true));

        Assert.False(_startup.StartAtLogin);
        Assert.False(_store.Load().StartAtLogin);
    }

    [Fact]
    public void Disabling_removes_the_registration_and_persists()
    {
        _startup.SetStartAtLogin(true);
        _registration.Calls.Clear();

        Assert.True(_startup.SetStartAtLogin(false));

        Assert.Equal([(false, false)], _registration.Calls);
        Assert.False(_store.Load().StartAtLogin);
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
    public void SyncOnLaunch_heals_the_record_while_the_toggle_is_on()
    {
        _startup.SetStartAtLogin(true);
        _startup.SetStartMinimized(true);
        _registration.Calls.Clear();

        _startup.SyncOnLaunch();

        Assert.Equal([(true, true)], _registration.Calls);
    }

    [Fact]
    public void SyncOnLaunch_never_touches_a_record_the_user_turned_off()
    {
        // A user who disabled the login item in the OS's own settings must not be fought with.
        _startup.SyncOnLaunch();

        Assert.Empty(_registration.Calls);
    }

    [Fact]
    public void Unsupported_launches_report_it_and_cannot_enable()
    {
        var unsupported = new StartupPreference(_store, new UnsupportedStartupRegistration());

        Assert.False(unsupported.IsSupported);
        Assert.False(unsupported.SetStartAtLogin(true));
        Assert.False(_store.Load().StartAtLogin);
        // Turning it OFF is always achievable — there is nothing to remove.
        Assert.True(unsupported.SetStartAtLogin(false));
    }
}
