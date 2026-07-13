using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Lattice.App.ViewModels;
using Lattice.App.Views;
using Lattice.Boinc.GuiRpc;
using Lattice.Core;
using Lattice.Tests;
using Xunit;

namespace Lattice.App.Tests.Headless.Journeys;

/// <summary>
/// End-to-end for the Projects view wired into the shell: two connected hosts
/// sharing one project URL collapse to a single parent row under All hosts (host
/// count "2", chevron present); scoping to one host drops the chevron and shows
/// that host's values only (no children even on toggle — design 2a); returning to
/// All hosts and expanding materializes the child rows. Mirrors TasksScopeJourney's
/// harness idiom exactly (settle on expected end states — row counts / rendered
/// text — never transient booleans, no wall-clock sleeps).
/// </summary>
public class ProjectsScopeJourney
{
    private const string Url = "http://shared/";

    private static FakeGuiRpcClient FakeWithProject(double rac) =>
        new()
        {
            OnGetState = () => Task.FromResult(
                TestData.MakeState(projects: [TestData.MakeProject(Url, "Shared") with { HostExpavgCredit = rac }])),
        };

    private static ProjectsView ProjectsViewIn(Window window) =>
        window.GetVisualDescendants().OfType<ProjectsView>().Single();

    private static IEnumerable<string?> TextsIn(Visual row) =>
        row.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text);

    [AvaloniaFact]
    public async Task Shared_project_collapses_across_hosts_and_scopes_to_a_single_host()
    {
        await using var harness = new JourneyHarness();
        HostConfig hostA = harness.AddHost("proj-journey-a", FakeWithProject(rac: 10));
        harness.AddHost("proj-journey-b", FakeWithProject(rac: 5));
        harness.Start();
        harness.Window.Show();
        harness.Layout();

        // Navigate to the Projects view (rail index 1).
        harness.Shell.SelectViewCommand.Execute("1");
        harness.Layout();
        Assert.Same(harness.Shell.Projects, harness.Shell.CurrentPage);

        await harness.SettleAsync(() => harness.Shell.Projects.Rows.Count == 1,
            "both hosts on one URL collapse to a single parent under All hosts");
        harness.Layout();

        // One parent row, host-count "2", chevron available (children exist).
        ProjectRowViewModel parentData = harness.Shell.Projects.Rows[0].Data;
        Assert.True(parentData.IsParent);
        Assert.Equal("2", parentData.HostsText);
        Assert.True(parentData.ShowChevron);
        Assert.Equal("15", parentData.AvgCreditText); // 10 + 5 aggregated across both hosts

        var view = ProjectsViewIn(harness.Window);
        DataGridRow parentRow = VisualTree.FindRow(view.Grid, 0);
        Assert.Contains("2", TextsIn(parentRow)); // the Hosts cell renders the count

        // Scope to host-a with a real click on its row — the scope trigger is the click
        // gesture (OnHostRailTapped), not SelectionChanged.
        var hostARow = harness.Shell.RailEntries.OfType<HostRailItemViewModel>().Single(r => r.HostId == hostA.Id);
        RailInput.ClickRow(harness.Window, hostARow);
        harness.Layout();
        Assert.Equal(hostA.Id, harness.Shell.Scope.HostId);

        await harness.SettleAsync(
            () => harness.Shell.Projects.Rows.Count == 1
                  && harness.Shell.Projects.Rows[0].Data.AvgCreditText == "10",
            "single-host scope shows host-a's project with host-a's values only");
        harness.Layout();

        ProjectRowViewModel scopedData = harness.Shell.Projects.Rows[0].Data;
        Assert.False(scopedData.ShowChevron); // no chevron: children are impossible in single-host scope

        // Toggling a single-host scope never materializes children (design 2a) —
        // expansion is a session-local flag the single-host render simply ignores.
        harness.Shell.Projects.ToggleExpandCommand.Execute(Url);
        harness.Layout();
        Assert.Single(harness.Shell.Projects.Rows);
        Assert.False(harness.Shell.Projects.Rows[0].Data.ShowChevron);
        // Reset that flag so the All-hosts leg starts from a known collapsed state
        // (the toggle above still flipped the session-local expansion set).
        harness.Shell.Projects.ToggleExpandCommand.Execute(Url);
        harness.Layout();
        Assert.Single(harness.Shell.Projects.Rows);

        // Back to All hosts with a real click on the sentinel, then expand: the two child rows become visible.
        var sentinel = harness.Shell.RailEntries.OfType<AllHostsRailItemViewModel>().Single();
        RailInput.ClickRow(harness.Window, sentinel);
        harness.Layout();
        await harness.SettleAsync(() => harness.Shell.Projects.Rows.Count == 1,
            "returning to All hosts collapses back to the single parent");

        harness.Shell.Projects.ToggleExpandCommand.Execute(Url);
        await harness.SettleAsync(() => harness.Shell.Projects.Rows.Count == 3,
            "expanding the parent inserts both hosts' child rows");
        harness.Layout();

        Assert.False(harness.Shell.Projects.Rows[1].Data.IsParent);
        Assert.False(harness.Shell.Projects.Rows[2].Data.IsParent);
        Assert.Equal("proj-journey-a", harness.Shell.Projects.Rows[1].Data.Name);
        Assert.Equal("proj-journey-b", harness.Shell.Projects.Rows[2].Data.Name);

        // Child rows are realized in the grid (row 1 and row 2 exist post-expand).
        view = ProjectsViewIn(harness.Window);
        Assert.Contains("proj-journey-a", TextsIn(VisualTree.FindRow(view.Grid, 1)));
        Assert.Contains("proj-journey-b", TextsIn(VisualTree.FindRow(view.Grid, 2)));
    }
}
