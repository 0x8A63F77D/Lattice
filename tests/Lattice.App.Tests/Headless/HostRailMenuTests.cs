using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using Lattice.App.Infrastructure;
using Lattice.App.Localization;
using Lattice.App.Tests.Fakes;
using Lattice.App.ViewModels;
using Lattice.App.Views;
using Lattice.Boinc.GuiRpc;
using Lattice.Core;
using Lattice.Tests;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using static Lattice.Tests.HeadlessLayout;

namespace Lattice.App.Tests.Headless;

public class HostRailMenuTests
{
    private static (ShellWindow Window, ShellViewModel Shell, HostRegistry Registry) MakeShell()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}.json");
        var registry = new HostRegistry(new LatticeConfig(5, []), path);
        var manager = new HostMonitorManager(registry, () => new FakeGuiRpcClient(), new FakeTimeProvider());
        var store = new HostStore(registry, manager, new ImmediateUiDispatcher());
        var uiState = new UiStateStore(Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}-ui.json"));
        var shell = new ShellViewModel(registry, store, new ManualUiClock(), uiState, () => new FakeGuiRpcClient());
        var window = new ShellWindow { DataContext = shell, Width = 1280, Height = 800 };
        return (window, shell, registry);
    }

    [AvaloniaFact]
    public void Edit_host_command_opens_a_prefilled_edit_dialog()
    {
        var (window, shell, registry) = MakeShell();
        window.Show();
        var cfg = TestData.MakeHostConfig(name: "mini-01");
        registry.AddHost(cfg);
        Layout(window);

        shell.EditHostCommand.Execute(cfg.Id);
        Dispatcher.UIThread.RunJobs();

        var dialog = Assert.Single(window.GetVisualDescendants().OfType<AddHostDialog>());
        var vm = Assert.IsType<AddHostViewModel>(dialog.DataContext);
        Assert.Equal(HostDialogMode.Edit, vm.Mode);
        Assert.Equal("mini-01", vm.Name);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Remove_host_command_confirms_then_removes()
    {
        var (window, shell, registry) = MakeShell();
        window.Show();
        var cfg = TestData.MakeHostConfig(name: "mini-01");
        registry.AddHost(cfg);
        Layout(window);

        shell.RemoveHostCommand.Execute(cfg.Id);
        Dispatcher.UIThread.RunJobs();
        var dialog = Assert.Single(window.GetVisualDescendants().OfType<FAContentDialog>());
        dialog.Hide(FAContentDialogResult.Primary);
        await HeadlessSync.WaitUntilAsync(() => registry.Hosts.Count == 0);

        Assert.Empty(registry.Hosts);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Test_host_command_writes_the_result_into_the_row_subtext()
    {
        var (window, shell, registry) = MakeShell();
        window.Show();
        var cfg = TestData.MakeHostConfig(name: "mini-01");
        registry.AddHost(cfg);
        Layout(window);
        var row = shell.RailEntries.OfType<HostRailItemViewModel>().Single();

        shell.TestHostCommand.Execute(cfg.Id);
        // FakeGuiRpcClient connects + exchanges versions successfully by default.
        await HeadlessSync.WaitUntilAsync(() => row.TestResultText is not null
            && !row.TestResultText.Equals(Strings.SettingsTestConnectionBusy));

        Assert.Contains("8", row.TestResultText!); // "Connected · BOINC 8.x.x"
        window.Close();
    }

    [AvaloniaFact]
    public async Task Test_host_command_ignores_a_second_invoke_on_the_same_row_while_one_is_in_flight()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}.json");
        var registry = new HostRegistry(new LatticeConfig(5, []), path);
        var manager = new HostMonitorManager(registry, () => new FakeGuiRpcClient(), new FakeTimeProvider());
        var store = new HostStore(registry, manager, new ImmediateUiDispatcher());
        var uiState = new UiStateStore(Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}-ui.json"));
        var gate = new TaskCompletionSource();
        var clientsCreated = 0;
        // A distinct factory from the store's monitors: gating it only blocks the
        // Test-connection path (OnTestHostRequested), never the background monitors.
        var shell = new ShellViewModel(registry, store, new ManualUiClock(), uiState, () =>
        {
            Interlocked.Increment(ref clientsCreated);
            var client = new FakeGuiRpcClient();
            client.OnExchangeVersions = async () => { await gate.Task; return new VersionInfo(8, 2, 0); };
            return client;
        });
        var window = new ShellWindow { DataContext = shell, Width = 1280, Height = 800 };
        window.Show();
        var cfg = TestData.MakeHostConfig(name: "mini-01");
        registry.AddHost(cfg);
        Layout(window);
        var row = shell.RailEntries.OfType<HostRailItemViewModel>().Single();

        shell.TestHostCommand.Execute(cfg.Id);
        await HeadlessSync.WaitUntilAsync(() => clientsCreated == 1);
        Assert.Equal(Strings.SettingsTestConnectionBusy, row.TestResultText);

        // Same-row reentrancy: the guard must no-op this second invoke while the
        // first is still in flight — no second client, no second RPC round-trip.
        shell.TestHostCommand.Execute(cfg.Id);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, clientsCreated);

        gate.SetResult();
        await HeadlessSync.WaitUntilAsync(() => row.TestResultText is not null
            && !row.TestResultText.Equals(Strings.SettingsTestConnectionBusy));

        Assert.Contains("8", row.TestResultText!); // "Connected · BOINC 8.x.x"
        Assert.Equal(1, clientsCreated); // guard held for the whole in-flight window
        window.Close();
    }

    [AvaloniaFact]
    public async Task Remove_write_failure_surfaces_on_the_row_and_keeps_the_host()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}.json");
        var registry = new HostRegistry(new LatticeConfig(5, []), path);
        var manager = new HostMonitorManager(registry, () => new FakeGuiRpcClient(), new FakeTimeProvider());
        var store = new HostStore(registry, manager, new ImmediateUiDispatcher());
        var uiState = new UiStateStore(Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}-ui.json"));
        var shell = new ShellViewModel(registry, store, new ManualUiClock(), uiState, () => new FakeGuiRpcClient());
        var window = new ShellWindow { DataContext = shell, Width = 1280, Height = 800 };
        window.Show();
        var cfg = TestData.MakeHostConfig(name: "mini-01");
        registry.AddHost(cfg);
        Layout(window);

        // Turn the config path into a directory so RemoveHost's save-rename throws
        // (same failure-injection as SettingsViewModelTests.MakeConfigPathUnwritable).
        File.Delete(path);
        Directory.CreateDirectory(path);
        try
        {
            shell.RemoveHostCommand.Execute(cfg.Id);
            Dispatcher.UIThread.RunJobs();
            var dialog = Assert.Single(window.GetVisualDescendants().OfType<FAContentDialog>());
            dialog.Hide(FAContentDialogResult.Primary);

            var row = shell.RailEntries.OfType<HostRailItemViewModel>().Single();
            await HeadlessSync.WaitUntilAsync(() =>
                row.TestResultText is not null
                && row.TestResultText.StartsWith(string.Format(Strings.HostRemoveFailedFmt, "")));
            Assert.Single(registry.Hosts);   // write failed → host still registered, not silently lost
        }
        finally { Directory.Delete(path); window.Close(); }
    }

    [AvaloniaFact]
    public void Host_rows_carry_the_menu_flyout_but_the_all_hosts_sentinel_does_not()
    {
        var (window, shell, registry) = MakeShell();
        window.Show();
        // Two hosts + a tall viewport → Flat, so the "All hosts" sentinel leads the rail.
        registry.AddHost(TestData.MakeHostConfig(name: "a"));
        registry.AddHost(TestData.MakeHostConfig(name: "b"));
        shell.SetRailViewportHeight(1000.0);
        Layout(window);

        foreach (ListBoxItem li in window.HostList.GetVisualDescendants().OfType<ListBoxItem>())
        {
            DockPanel root = li.GetVisualDescendants().OfType<DockPanel>().First();
            if (li.DataContext is HostRailItemViewModel)
                Assert.IsType<MenuFlyout>(root.ContextFlyout);   // Edit / Test / Remove
            else
                Assert.Null(root.ContextFlyout);                 // sentinel: no menu (design 3b)
        }
        window.Close();
    }

    [AvaloniaFact]
    public void Opening_a_host_rows_context_flyout_wires_the_shell_edit_command()
    {
        // The other tests invoke ShellViewModel.EditHostCommand directly; this one
        // exercises the REAL XAML path — the MenuItem's `$parent[ListBox]...EditHostCommand`
        // binding across the flyout popup boundary must resolve, or the menu is dead in the app.
        var (window, shell, registry) = MakeShell();
        window.Show();
        var cfg = TestData.MakeHostConfig(name: "mini-01");
        registry.AddHost(cfg);
        Layout(window);

        var hostRow = window.HostList.GetVisualDescendants().OfType<ListBoxItem>()
            .Single(li => li.DataContext is HostRailItemViewModel);
        var root = hostRow.GetVisualDescendants().OfType<DockPanel>().First();
        var flyout = Assert.IsType<MenuFlyout>(root.ContextFlyout);
        flyout.ShowAt(root);
        Dispatcher.UIThread.RunJobs();

        var editItem = window.GetVisualDescendants().OfType<MenuItem>()
            .Single(mi => Equals(mi.Header, Strings.HostMenuEdit));
        Assert.NotNull(editItem.Command);                 // binding resolved across the popup
        Assert.Equal(cfg.Id, editItem.CommandParameter);  // row's HostId flowed through
        editItem.Command!.Execute(editItem.CommandParameter);
        Dispatcher.UIThread.RunJobs();

        var dialog = Assert.Single(window.GetVisualDescendants().OfType<AddHostDialog>());
        Assert.Equal("mini-01", Assert.IsType<AddHostViewModel>(dialog.DataContext).Name);
        flyout.Hide();
        window.Close();
    }

    [AvaloniaFact]
    public void Host_context_flyout_stays_open_when_a_store_refresh_rebuilds_the_rail()
    {
        // Regression for #153: a background poll refresh raises HostStore.Changed →
        // ShellViewModel.ReconcileHosts → RebuildRail. If the rebuild tears down the
        // row container hosting an OPEN context flyout, the flyout light-dismisses
        // mid-interaction. Drive the exact rebuild path deterministically (a registry
        // update funnels into the SAME RebuildRail as a poll snapshot) and assert the
        // open flyout survives it.
        var (window, shell, registry) = MakeShell();
        window.Show();
        var cfg = TestData.MakeHostConfig(name: "mini-01");
        registry.AddHost(cfg);
        Layout(window);

        var hostRow = window.HostList.GetVisualDescendants().OfType<ListBoxItem>()
            .Single(li => li.DataContext is HostRailItemViewModel);
        var rowVm = Assert.IsType<HostRailItemViewModel>(hostRow.DataContext);
        var root = hostRow.GetVisualDescendants().OfType<DockPanel>().First();
        var flyout = Assert.IsType<MenuFlyout>(root.ContextFlyout);
        flyout.ShowAt(root);
        Dispatcher.UIThread.RunJobs();
        Assert.True(flyout.IsOpen);   // sanity: it opened

        // A background refresh lands and rebuilds the rail while the menu is open.
        registry.UpdateHost(cfg);     // → HostStore.Changed → ReconcileHosts → RebuildRail
        Dispatcher.UIThread.RunJobs();
        Layout(window);

        Assert.True(flyout.IsOpen);   // must NOT auto-dismiss on the rebuild
        // The rebuild reused the SAME row VM (identity preserved), so the menu keeps
        // binding to the LIVE host state — the fix does not freeze the menu's data
        // (issue #153, verification-bar item: state changes still reflect on next open).
        Assert.Same(rowVm, shell.RailEntries.OfType<HostRailItemViewModel>().Single());
        flyout.Hide();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Host_context_flyout_survives_a_real_poll_and_the_menu_reflects_the_new_perm_mode()
    {
        // The faithful #153 trigger: a STARTED monitor polls on its background thread and
        // marshals each snapshot through the store, which rebuilds the rail. A
        // QueueUiDispatcher delivers those posts on THIS thread at an explicit drain
        // (HostGraphFixture discipline), so "a poll lands while the menu is open" is
        // deterministic rather than racy. A frozen FakeTimeProvider means the rail never
        // rebuilds except on the immediate first poll and our explicit RequestRefresh.
        var path = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}.json");
        var registry = new HostRegistry(new LatticeConfig(5, []), path);
        var fake = new FakeGuiRpcClient();
        var manager = new HostMonitorManager(registry, () => fake, new FakeTimeProvider());
        var dispatcher = new QueueUiDispatcher();
        var store = new HostStore(registry, manager, dispatcher);
        var uiPath = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}-ui.json");
        var shell = new ShellViewModel(registry, store, new ManualUiClock(), new UiStateStore(uiPath), () => new FakeGuiRpcClient());
        var window = new ShellWindow { DataContext = shell, Width = 1280, Height = 800 };
        window.Show();
        registry.AddHost(TestData.MakeHostConfig(name: "mini-01"));
        manager.Start();

        // Drain-to-end-state settle: deliver queued monitor posts here, pump the UI, then
        // check the EXPECTED state. No wall-clock settle — the ceiling is a hang diagnostic.
        async Task Settle(Func<bool> done, string reason)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                dispatcher.Drain();
                Dispatcher.UIThread.RunJobs();
                dispatcher.Drain();
                if (done()) return;
                if (sw.Elapsed > TimeSpan.FromSeconds(30))
                    throw new TimeoutException($"end state unreachable: {reason}");
                await Task.Delay(10);
            }
        }

        var row = shell.RailEntries.OfType<HostRailItemViewModel>().Single();
        await Settle(() => row.IsConnected, "host never connected");
        Layout(window);

        var hostRow = window.HostList.GetVisualDescendants().OfType<ListBoxItem>()
            .Single(li => li.DataContext is HostRailItemViewModel);
        var root = hostRow.GetVisualDescendants().OfType<DockPanel>().First();
        var flyout = Assert.IsType<MenuFlyout>(root.ContextFlyout);
        flyout.ShowAt(root);
        Dispatcher.UIThread.RunJobs();
        Assert.True(flyout.IsOpen);
        Assert.Equal(RunMode.Auto, row.CpuMode);   // baseline permanent CPU mode

        // The daemon's PERMANENT CPU mode changes from another source; the next poll
        // carries it. Nudge the monitor to poll now and deliver the snapshot.
        fake.OnGetCcStatus = () => Task.FromResult(FakeGuiRpcClient.DefaultStatus with { TaskModePerm = RunMode.Never });
        store.RequestRefresh();
        await Settle(() => row.CpuMode == RunMode.Never, "poll never delivered the new perm mode");

        Assert.True(flyout.IsOpen);                 // #153: the poll rebuild must not dismiss it
        Assert.Equal(RunMode.Never, row.CpuMode);   // point 4: the menu's data is live, not frozen

        flyout.Hide();
        window.Close();
        store.Dispose();
        await manager.DisposeAsync();
        File.Delete(path);
        File.Delete(uiPath);
    }

    [AvaloniaFact]
    public void Group_header_rows_have_no_menu_flyout()
    {
        var (window, shell, registry) = MakeShell();
        window.Show();
        // Many hosts overflow the pane → Grouped, so group-header rows appear.
        for (var i = 0; i < 30; i++) registry.AddHost(TestData.MakeHostConfig(name: $"h{i}"));
        Layout(window);
        Assert.Contains(shell.RailEntries, e => e is GroupHeaderRailItemViewModel);

        foreach (ListBoxItem li in window.HostList.GetVisualDescendants().OfType<ListBoxItem>()
                     .Where(li => li.DataContext is GroupHeaderRailItemViewModel))
        {
            DockPanel root = li.GetVisualDescendants().OfType<DockPanel>().First();
            Assert.Null(root.ContextFlyout);   // group headers are not hosts → no menu (design 3b)
        }
        window.Close();
    }
}
