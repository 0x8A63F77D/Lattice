using Avalonia.Headless.XUnit;
using Lattice.App.Localization;
using Lattice.App.ViewModels;
using Lattice.Core;
using Lattice.Tests;
using Xunit;

namespace Lattice.App.Tests.Headless.Journeys;

/// <summary>
/// End-to-end: a host whose password the daemon rejects lands AuthFailed on the
/// rail → selecting it (the same SelectedIndex path ShellWindow's real click
/// handler drives) jumps to Settings with that host's expander focused → the
/// user corrects the password and saves → once the daemon (fake) accepts it,
/// the rail reaches Connected.
/// </summary>
public class AuthFailJourney
{
    [AvaloniaFact]
    public async Task Correcting_the_password_after_an_auth_failure_reaches_connected()
    {
        await using var harness = new JourneyHarness();
        const string address = "auth-fail-journey";
        var fake = new FakeGuiRpcClient { OnAuthorize = _ => Task.FromResult(false) };
        HostConfig host = harness.AddHost(address, fake, password: "wrong-pw");
        harness.Start();
        harness.Window.Show();
        harness.Layout();

        await harness.SettleAsync(
            () => harness.Shell.RailEntries.OfType<HostRailItemViewModel>()
                .Single(h => h.HostId == host.Id).State == RailState.AuthFailed,
            "rail should show AuthFailed for the rejected password");
        harness.Layout();

        var railItem = harness.Shell.RailEntries.OfType<HostRailItemViewModel>().Single();
        Assert.Equal(Strings.RailAuthFailed, railItem.StateText);

        // Simulate the rail click path: ShellWindow.OnHostSelectionChanged drives
        // shell.NavigateToSettings(item.HostId) off exactly this selection change
        // (index 0 is the All-hosts sentinel; the sole host lives at index 1).
        harness.Window.HostList.SelectedIndex = 1;
        harness.Layout();

        Assert.Same(harness.Shell.Settings, harness.Shell.CurrentPage);
        Assert.Equal(host.Id, harness.Shell.Scope.HostId);
        var settingsItem = Assert.Single(harness.Shell.Settings.Hosts);
        Assert.True(settingsItem.IsExpanded);
        Assert.True(settingsItem.HasAuthError);

        // Correct the password and let the daemon (fake) accept it, then save —
        // flipping the fake BEFORE saving keeps this deterministic: Save triggers
        // HostMonitor.UpdateConfig, which wakes the parked AuthFailed loop and
        // reconnects immediately with whatever OnAuthorize says right now.
        settingsItem.Password = "correct-pw";
        fake.OnAuthorize = _ => Task.FromResult(true);
        settingsItem.SaveCommand.Execute(null);

        await harness.SettleAsync(
            () => railItem.State == RailState.Connected,
            "rail should reach Connected once the corrected password is accepted");
        harness.Layout();

        Assert.Equal(string.Format(Strings.RailConnectedFmt, 0), railItem.StateText);
        Assert.Equal("correct-pw", harness.Registry.Hosts.Single().Password);
    }
}
