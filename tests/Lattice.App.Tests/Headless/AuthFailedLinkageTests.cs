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
    [AvaloniaFact]
    public async Task Selecting_an_auth_failed_host_opens_the_edit_dialog_with_the_password_error()
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

        // TWO hosts: with one host the rail is SingleHost (no sentinel, host auto-selected/scoped),
        // so re-selecting it raises no SelectionChanged and the dialog never opens. Two hosts → Flat
        // list (sentinel at 0), and the auth-failed host (added first) sits at index 1, unselected.
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

        window.HostList.SelectedIndex = 1;   // sentinel at 0; the auth-failed host is at 1
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
