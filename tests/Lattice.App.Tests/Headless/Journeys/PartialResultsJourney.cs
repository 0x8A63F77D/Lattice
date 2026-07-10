using Avalonia.Headless.XUnit;
using Lattice.App.Localization;
using Lattice.App.ViewModels;
using Lattice.Boinc.GuiRpc;
using Lattice.Core;
using Lattice.Tests;
using Xunit;

namespace Lattice.App.Tests.Headless.Journeys;

/// <summary>
/// End-to-end: one host refuses the connection outright (AuthFailed — the
/// fastest, most deterministic way to land a host outside RailState.Connected;
/// see the class-level note below on why this stands in for "refuses
/// connections" instead of waiting out Unreachable's backoff tiers) while the
/// other is healthy → the All-hosts partial-results bar reports the outage →
/// dismissing hides it → killing the healthy host's next poll makes the
/// covered set shrink, which re-reports the bar even though the unreachable
/// tier didn't grow → the All-hosts rail subtext drops to "0 of 2 connected".
/// </summary>
public class PartialResultsJourney
{
    [AvaloniaFact]
    public async Task A_dead_host_reports_dismisses_and_reappears_on_further_failure()
    {
        await using var harness = new JourneyHarness();
        // "Refuses connections": OnAuthorize failing lands AuthFailed on the FIRST
        // attempt, with no backoff wait — RailState.Unreachable (the other outage
        // tier TasksViewModel counts) requires attempt >= 4, i.e. ~15s of real
        // backoff, which would make this journey slow and is not what's under
        // test here. Both tiers feed the same unreachableIds set in TasksViewModel.
        HostConfig hostA = harness.AddHost("partial-journey-a",
            new FakeGuiRpcClient { OnAuthorize = _ => Task.FromResult(false) });
        HostConfig hostB = harness.AddHost("partial-journey-b",
            new FakeGuiRpcClient { OnGetResults = _ => Task.FromResult<IReadOnlyList<Result>>([TestData.MakeResult()]) });
        harness.Start();
        harness.Window.Show();
        harness.Layout();

        await harness.SettleAsync(() => harness.Shell.Tasks.ShowPartialBar,
            "the partial bar should appear once host A is AuthFailed and host B has snapshotted");
        harness.Layout();

        Assert.Equal(string.Format(Strings.PartialFmt, 1, 2, 1), harness.Shell.Tasks.PartialBarText);

        harness.Shell.Tasks.DismissPartialCommand.Execute(null);
        Assert.False(harness.Shell.Tasks.ShowPartialBar);

        // Kill host B's next poll: the covered set (Connected hosts with a
        // snapshot) shrinks from {B} to {}, which changes the fingerprint and
        // re-reports the bar even though the unreachable tier (still just A)
        // never grew — PartialBarPolicy's "either half changing re-reports" rule.
        FakeGuiRpcClient fakeB = harness.ClientFor("partial-journey-b");
        fakeB.OnGetCcStatus = () => throw new IOException("host b died");
        harness.Store.RequestRefresh(hostB.Id);

        await harness.SettleAsync(() => harness.Shell.Tasks.ShowPartialBar,
            "the bar should reappear once host B's poll fails, even though it isn't dismissed-again");
        harness.Layout();

        Assert.Equal(string.Format(Strings.PartialFmt, 1, 2, 0), harness.Shell.Tasks.PartialBarText);

        var allHosts = Assert.IsType<AllHostsRailItemViewModel>(harness.Shell.RailEntries[0]);
        Assert.Equal(string.Format(Strings.AllHostsPartialFmt, 0, 2), allHosts.Subtext);

        // Sanity: host A never left AuthFailed throughout.
        Assert.Equal(RailState.AuthFailed, harness.Shell.RailEntries.OfType<HostRailItemViewModel>()
            .Single(h => h.HostId == hostA.Id).State);
    }
}
