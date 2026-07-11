using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Lattice.App.Localization;
using Lattice.App.ViewModels;
using Lattice.Boinc.GuiRpc;
using Lattice.Core;
using Lattice.Tests;
using Xunit;

namespace Lattice.App.Tests.Headless.Journeys;

/// <summary>
/// End-to-end: two connected hosts merge into the All-hosts Tasks grid; scoping to
/// one host filters the grid and hides the Host column; switching to another view
/// and back leaves the rail scope untouched (design rule: scope is a rail concern,
/// not a per-view one — mirrors ShellRailTests.Selecting_a_host_then_switching_views).
/// </summary>
public class TasksScopeJourney
{
    private static IReadOnlyList<Result> MakeResults(string prefix, int count) =>
        [.. Enumerable.Range(1, count).Select(i => TestData.MakeResult(name: $"{prefix}_{i}"))];

    private static List<string?> VisibleColumnHeaders(Window window) =>
        [.. window.GetVisualDescendants().OfType<DataGridColumnHeader>()
            .Where(h => h.IsVisible)
            .Select(h => h.Content as string)
            .Where(text => text is not null)];

    [AvaloniaFact]
    public async Task Scoping_to_a_host_filters_the_grid_and_survives_a_view_switch()
    {
        await using var harness = new JourneyHarness();
        harness.AddHost("scope-journey-a",
            new FakeGuiRpcClient { OnGetResults = _ => Task.FromResult(MakeResults("a", 3)) });
        HostConfig hostB = harness.AddHost("scope-journey-b",
            new FakeGuiRpcClient { OnGetResults = _ => Task.FromResult(MakeResults("b", 2)) });
        harness.Start();
        harness.Window.Show();
        harness.Layout();

        await harness.SettleAsync(() => harness.Shell.Tasks.Rows.Count == 5,
            "both hosts should snapshot and merge into 5 rows under All hosts");
        harness.Layout();

        Assert.Equal(5, harness.Shell.TasksCount);
        // ShellWindow's content area is narrower than a standalone 1280px TasksView
        // window (the NavigationView pane eats width), so this checks column
        // presence/absence rather than pinning an exact header count to a
        // breakpoint that isn't this journey's concern.
        Assert.Contains(Strings.ColHost, VisibleColumnHeaders(harness.Window));

        // Index 0 is the All-hosts sentinel; hosts follow in registration order.
        harness.Window.HostList.SelectedIndex = 2;
        harness.Layout();

        Assert.Equal(hostB.Id, harness.Shell.Scope.HostId);
        Assert.Equal(2, harness.Shell.Tasks.Rows.Count);
        Assert.DoesNotContain(Strings.ColHost, VisibleColumnHeaders(harness.Window));

        // Switch to another view and back to Tasks: the design rule is "scope is a
        // rail concern" — a page switch must never reset it. All four nav views are
        // real pages now (Tasks 0 · Projects 1 · Transfers 2 · Event log 3), so this
        // hops to Transfers (index "2") and confirms the scope survives the round-trip.
        harness.Shell.SelectViewCommand.Execute("2");
        harness.Layout();
        Assert.Same(harness.Shell.Transfers, harness.Shell.CurrentPage);

        harness.Shell.SelectViewCommand.Execute("0");
        harness.Layout();

        Assert.Same(harness.Shell.Tasks, harness.Shell.CurrentPage);
        Assert.Equal(hostB.Id, harness.Shell.Scope.HostId);
        Assert.Equal(2, harness.Shell.Tasks.Rows.Count);
    }
}
