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
