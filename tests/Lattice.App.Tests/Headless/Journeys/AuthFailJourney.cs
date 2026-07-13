using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Lattice.App.Localization;
using Lattice.App.ViewModels;
using Lattice.App.Views;
using Lattice.Core;
using Lattice.Tests;
using Xunit;

namespace Lattice.App.Tests.Headless.Journeys;

/// <summary>
/// End-to-end: a host whose password the daemon rejects lands AuthFailed on the
/// rail → selecting it (the same SelectedIndex path ShellWindow's real click
/// handler drives) opens the Edit host dialog with the password field flagged →
/// the user corrects the password and saves → once the daemon (fake) accepts it,
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
        // A second, healthy host: with only one host the rail is SingleHost (no
        // sentinel, host auto-selected — a re-select raises no SelectionChanged, so
        // the dialog never opens). Two hosts → Flat list with the sentinel at 0 and
        // the auth-failed host at index 1, unselected.
        var fake2 = new FakeGuiRpcClient { OnAuthorize = _ => Task.FromResult(true) };
        harness.AddHost("auth-fail-journey-healthy", fake2, password: "pw");
        harness.Start();
        harness.Window.Show();
        harness.Layout();

        await harness.SettleAsync(
            () => harness.Shell.RailEntries.OfType<HostRailItemViewModel>()
                .Single(h => h.HostId == host.Id).State == RailState.AuthFailed,
            "rail should show AuthFailed for the rejected password");
        harness.Layout();

        var railItem = harness.Shell.RailEntries.OfType<HostRailItemViewModel>().Single(h => h.HostId == host.Id);
        Assert.Equal(Strings.RailAuthFailed, railItem.StateText);

        // Auth-failed click now opens the Edit host dialog (Task 12), not Settings. The deep
        // link rides the Tapped gesture (OnHostRailTapped), so drive a real click on the row
        // rather than a bare SelectedIndex assignment (which only fires SelectionChanged).
        RailInput.ClickRow(harness.Window, railItem);
        await harness.SettleAsync(
            () => harness.Window.GetVisualDescendants().OfType<AddHostDialog>().Any(),
            "auth-failed click should open the Edit host dialog");
        var dialog = harness.Window.GetVisualDescendants().OfType<AddHostDialog>().Single();
        var vm = Assert.IsType<AddHostViewModel>(dialog.DataContext);
        Assert.Equal(HostDialogMode.Edit, vm.Mode);
        Assert.True(vm.HasPasswordError);
        Assert.Equal(host.Id, harness.Shell.Scope.HostId);

        // Correct the password, let the daemon accept it, then Save. Edit-save persists
        // without a connection test (Task 9), waking the parked AuthFailed loop → reconnect.
        vm.Password = "correct-pw";
        fake.OnAuthorize = _ => Task.FromResult(true);
        var save = harness.Window.GetVisualDescendants().OfType<Button>()
            .Single(b => b.Name == "PrimaryButton");
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        await harness.SettleAsync(
            () => railItem.State == RailState.Connected,
            "rail should reach Connected once the corrected password is saved");
        harness.Layout();

        Assert.Equal(string.Format(Strings.RailConnectedFmt, 0), railItem.StateText);
        Assert.Equal("correct-pw", harness.Registry.Hosts.Single(h => h.Id == host.Id).Password);
    }
}
