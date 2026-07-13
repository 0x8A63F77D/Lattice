using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using Lattice.App.Infrastructure;
using Lattice.App.Tests.Fakes;
using Lattice.App.ViewModels;
using Lattice.App.Views;
using Lattice.Core;
using Lattice.Tests;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using static Lattice.Tests.HeadlessLayout;

namespace Lattice.App.Tests.Headless;

public class AuthFailedLinkageTests
{
    private static (ShellViewModel shell, ShellWindow window, HostRegistry registry, HostStore store)
        BuildShell()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}.json");
        var registry = new HostRegistry(new LatticeConfig(5, []), path);
        var manager = new HostMonitorManager(registry, () => new FakeGuiRpcClient(), new FakeTimeProvider());
        var store = new HostStore(registry, manager, new ImmediateUiDispatcher());
        var uiState = new UiStateStore(Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}-ui.json"));
        var shell = new ShellViewModel(registry, store, new ManualUiClock(), uiState, () => new FakeGuiRpcClient());
        var window = new ShellWindow { DataContext = shell };
        window.Show();
        Layout(window);
        return (shell, window, registry, store);
    }

    [AvaloniaFact]
    public async Task Clicking_an_auth_failed_host_opens_the_edit_dialog_with_the_password_error()
    {
        var (shell, window, registry, store) = BuildShell();

        // TWO hosts → Flat list (sentinel at 0), the auth-failed host (added first) at index 1,
        // unselected. Clicking it both changes selection AND fires Tapped; the deep link now
        // rides the Tapped gesture (OnHostRailTapped), so verify via a real click, not SelectedIndex.
        var host = TestData.MakeHostConfig(name: "office-pc");
        registry.AddHost(host);
        registry.AddHost(TestData.MakeHostConfig(name: "other"));
        store.Hosts[0].Status = new ConnectionStatus(
            host.Id, HostConnectionState.AuthFailed, 1, null, "unauthorized", null);
        // Layout BEFORE the refresh loop: with two hosts and no viewport height measured
        // yet, RebuildRail resolves Grouped+collapsed (fits() sees AvailableHeight=0), so
        // the host rows aren't materialized in RailEntries and a refresh loop here would
        // be a no-op. Laying out first settles the real window height, RebuildRail flips
        // to Flat, and only THEN does the refresh loop find the materialized host rows and
        // pick up the AuthFailed status set above.
        Layout(window);
        foreach (var row in shell.RailEntries.OfType<HostRailItemViewModel>()) row.Refresh();

        var authRow = shell.RailEntries.OfType<HostRailItemViewModel>().Single(r => r.HostId == host.Id);
        Assert.Equal(RailState.AuthFailed, authRow.State);
        RailInput.ClickRow(window, authRow);
        await HeadlessSync.WaitUntilAsync(() => window.GetVisualDescendants().OfType<AddHostDialog>().Any());

        var dialog = Assert.Single(window.GetVisualDescendants().OfType<AddHostDialog>());
        var vm = Assert.IsType<AddHostViewModel>(dialog.DataContext);
        Assert.Equal(HostDialogMode.Edit, vm.Mode);
        Assert.True(vm.HasPasswordError);
        Assert.IsNotType<SettingsViewModel>(shell.CurrentPage);   // did NOT navigate to Settings
        dialog.Hide();
        window.Close();
    }

    // Regression pin for the single-host deep link (Codex P2). With ONE host the rail is
    // SingleHost: no sentinel, and RebuildRail PRE-SELECTS the sole row. Re-clicking that
    // already-selected row raises no SelectionChanged, so the old selection-only handler never
    // opened the Edit dialog — the common one-host password-error setup was broken. The click
    // gesture (OnHostRailTapped) fixes it. Falsification: revert to the SelectionChanged-only
    // trigger and this test fails (no AddHostDialog ever appears).
    [AvaloniaFact]
    public async Task Clicking_the_sole_auth_failed_host_in_single_host_mode_opens_the_edit_dialog()
    {
        var (shell, window, registry, store) = BuildShell();

        var host = TestData.MakeHostConfig(name: "only-pc");
        registry.AddHost(host);   // single host ⇒ SingleHost rail, row pre-selected, no sentinel
        store.Hosts[0].Status = new ConnectionStatus(
            host.Id, HostConnectionState.AuthFailed, 1, null, "unauthorized", null);
        Layout(window);
        foreach (var row in shell.RailEntries.OfType<HostRailItemViewModel>()) row.Refresh();

        var soleRow = Assert.Single(shell.RailEntries.OfType<HostRailItemViewModel>());
        Assert.Equal(RailState.AuthFailed, soleRow.State);
        // Pre-selected by RebuildRail: clicking changes nothing about selection, so only the
        // Tapped gesture can open the dialog here.
        Assert.Same(soleRow, window.HostList.SelectedItem);

        RailInput.ClickRow(window, soleRow);
        await HeadlessSync.WaitUntilAsync(() => window.GetVisualDescendants().OfType<AddHostDialog>().Any());

        var dialog = Assert.Single(window.GetVisualDescendants().OfType<AddHostDialog>());
        var vm = Assert.IsType<AddHostViewModel>(dialog.DataContext);
        Assert.Equal(HostDialogMode.Edit, vm.Mode);
        Assert.True(vm.HasPasswordError);
        Assert.IsNotType<SettingsViewModel>(shell.CurrentPage);   // did NOT navigate to Settings
        dialog.Hide();
        window.Close();
    }
}
