using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using Lattice.App.Localization;
using Lattice.App.Tests.Fakes;
using Lattice.App.ViewModels;
using Lattice.App.Views;
using Lattice.Boinc.GuiRpc;
using Lattice.Core;
using Lattice.Tests;
using Xunit;

namespace Lattice.App.Tests.Headless.Journeys;

/// <summary>
/// End-to-end: first run → drive the real Add-host dialog VM path (reuse
/// AddHostDialogTests' driving idiom) with a fake that connects and reports 2
/// tasks → the rail, the Tasks nav badge, the TasksView grid, and config.json on
/// disk all agree the host exists.
/// </summary>
public class AddHostJourney
{
    [AvaloniaFact]
    public async Task Adding_a_host_through_the_real_dialog_lands_it_everywhere()
    {
        await using var harness = new JourneyHarness();
        harness.Start();
        harness.Window.Show();
        harness.Layout();

        // First run: no hosts yet.
        Assert.False(harness.Shell.HasHosts);
        Assert.True(harness.Window.FirstRun.IsVisible);
        Assert.False(harness.Window.Nav.IsVisible);

        const string address = "add-host-journey";
        harness.RegisterFake(address, new FakeGuiRpcClient
        {
            OnGetResults = _ => Task.FromResult<IReadOnlyList<Result>>(
                [TestData.MakeResult(name: "task_1"), TestData.MakeResult(name: "task_2")]),
        });

        // Real dialog path: the CTA/rail + button raises AddHostRequested, exactly
        // as AddHostDialogTests drives it.
        harness.Shell.RequestAddHostCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var dialog = Assert.IsType<AddHostDialog>(
            Assert.Single(harness.Window.GetVisualDescendants().OfType<FAContentDialog>()));
        var vm = Assert.IsType<AddHostViewModel>(dialog.DataContext);
        vm.Name = "journey-host";
        vm.Address = address;
        Dispatcher.UIThread.RunJobs();

        // Drive the REAL primary-button path (same idiom as AddHostDialogTests).
        var primary = harness.Window.GetVisualDescendants().OfType<Button>()
            .Single(b => b.Name == "PrimaryButton");
        primary.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        // FAContentDialog's close path awaits a real Task.Delay(200) (see
        // HeadlessSync's doc); wait for the dialog to actually leave the visual
        // tree, not just for the VM's Succeeded flag.
        await harness.SettleAsync(
            () => vm.Succeeded && !harness.Window.GetVisualDescendants().OfType<FAContentDialog>().Any(),
            "add-host dialog should succeed and close");
        harness.Layout();

        Assert.False(harness.Window.GetVisualDescendants().OfType<FAContentDialog>().Any());
        HostConfig host = Assert.Single(harness.Registry.Hosts);
        Assert.Equal(address, host.Address);

        await harness.SettleAsync(
            () => harness.Shell.RailEntries.OfType<HostRailItemViewModel>()
                .SingleOrDefault(h => h.HostId == host.Id) is { StateText: not null } item
                && item.StateText == string.Format(Strings.RailConnectedFmt, 2),
            "rail should show 'Connected · 2 tasks'");
        harness.Layout();

        var railItem = harness.Shell.RailEntries.OfType<HostRailItemViewModel>().Single();
        Assert.Equal(string.Format(Strings.RailConnectedFmt, 2), railItem.StateText);
        Assert.Equal(2, harness.Shell.TasksCount);
        Assert.Equal(2, harness.Shell.Tasks.Rows.Count);
        // TasksView is the default page: the DataGrid should have materialized 2 rows.
        Assert.Equal(2, harness.Window.GetVisualDescendants().OfType<DataGridRow>().Count());

        // config.json on disk now contains the host.
        HostRegistry reloaded = HostRegistry.Load(harness.ConfigPath);
        Assert.Contains(reloaded.Hosts, h => h.Id == host.Id && h.Address == address);
    }
}
