using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Lattice.App.Infrastructure;
using Lattice.App.Tests.Fakes;
using Lattice.App.ViewModels;
using Lattice.App.Views;
using Lattice.Core;
using Lattice.Tests;
using Xunit;
using static Lattice.Tests.HeadlessLayout;

namespace Lattice.App.Tests.Headless;

public class AuthFailedLinkageTests
{
    [AvaloniaFact]
    public void Selecting_an_auth_failed_host_lands_in_settings_with_that_host_expanded()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}.json");
        var registry = new HostRegistry(new LatticeConfig(5, []), path);
        // Manager never started: no sockets, no background threads in headless tests.
        var manager = new HostMonitorManager(registry, () => new FakeGuiRpcClient(), TimeProvider.System);
        var store = new HostStore(registry, manager, new ImmediateUiDispatcher());
        var uiState = new UiStateStore(Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}-ui.json"));
        var shell = new ShellViewModel(registry, store, new ManualUiClock(), uiState, () => new FakeGuiRpcClient());
        var window = new ShellWindow { DataContext = shell };
        window.Show();
        Layout(window);

        var host = TestData.MakeHostConfig(name: "office-pc");
        registry.AddHost(host);
        store.Hosts[0].Status = new ConnectionStatus(
            host.Id, HostConnectionState.AuthFailed, 1, null, "unauthorized", null);
        shell.RailEntries.OfType<HostRailItemViewModel>().Single().Refresh();
        Layout(window);

        // Index 0 is the All-hosts sentinel; the sole host lives at index 1.
        window.HostList.SelectedIndex = 1;
        Layout(window);

        Assert.Same(shell.Settings, shell.CurrentPage);
        var item = Assert.Single(shell.Settings.Hosts);
        Assert.True(item.IsExpanded);
        Assert.True(item.HasAuthError);
        Assert.Equal(host.Id, shell.Scope.HostId);
        window.Close();
    }
}
