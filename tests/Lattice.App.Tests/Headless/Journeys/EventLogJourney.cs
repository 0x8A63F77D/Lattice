using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Lattice.App.Tests.Fakes;
using Lattice.App.ViewModels;
using Lattice.Boinc.GuiRpc;
using Lattice.Core;
using Lattice.Tests;
using Xunit;

namespace Lattice.App.Tests.Headless.Journeys;

/// <summary>
/// The Event-log acceptance centerpiece — the full path a daemon message travels:
/// HostMonitor event → HostStore marshal → MessageLog identity-keyed dedup → grid.
///
/// Script: two hosts connect → host A emits an Info + a UserAlert while the Tasks
/// page is showing, so the unread InfoBadge reads 1 (warnings/errors only) →
/// navigate to the Event log, which activates the page (badge clears) and renders
/// the rows, the alert carrying its "warning" grid class → REPLAY host A's exact
/// batch (reconnect re-fetches the same seqnos): the identity-keyed log dedups, so
/// the row count is unchanged → host B's message, timestamped between A's two,
/// interleaves by time in the default All-hosts scope.
///
/// Messages are injected straight on the manager (ManagerTestAccess), the only way
/// to exercise the message-only path deterministically — real polling couples a
/// message batch with a snapshot on every tick. Fakes return no polling messages,
/// so the injected batches are the whole message population.
/// </summary>
public class EventLogJourney
{
    // Distinct whole seconds ⇒ distinct MessageKey timestamp ticks, so All-hosts
    // ordering is purely by time (matches EventLogViewModelTests' convention).
    private static DateTimeOffset T(int sec) => new(2026, 7, 11, 12, 0, sec, TimeSpan.Zero);

    private static Message Msg(int seqno, int sec, string body, MessagePriority pri) =>
        new("Proj", pri, seqno, T(sec), body);

    private static void Raise(JourneyHarness harness, Guid hostId, params Message[] batch) =>
        ManagerTestAccess.RaiseMessagesAdded(harness.Manager, new MessagesAddedEventArgs(hostId, batch));

    private static bool AllConnected(JourneyHarness harness) =>
        harness.Shell.RailEntries.OfType<HostRailItemViewModel>() is { } items
        && items.Count() == 2
        && items.All(h => h.State == RailState.Connected);

    [AvaloniaFact]
    public async Task A_daemon_alert_badges_then_the_log_dedups_a_replay_and_interleaves_hosts()
    {
        await using var harness = new JourneyHarness();
        HostConfig hostA = harness.AddHost("eventlog-journey-a", new FakeGuiRpcClient());
        HostConfig hostB = harness.AddHost("eventlog-journey-b", new FakeGuiRpcClient());
        harness.Start();
        harness.Window.Show();
        harness.Layout();

        await harness.SettleAsync(() => AllConnected(harness),
            "both hosts should reach Connected");
        harness.Layout();

        // --- Host A emits while the Tasks page is showing: the alert accrues into
        //     the unread badge, the info line does not. -------------------------
        Raise(harness, hostA.Id,
            Msg(1, 1, "a-info", MessagePriority.Info),
            Msg(2, 3, "a-alert", MessagePriority.UserAlert));
        await harness.SettleAsync(
            () => harness.Shell.EventLog.Rows.Count == 2 && harness.Shell.EventLogUnread == 1,
            "host A's batch should land two rows and one unread (the UserAlert)");
        harness.Layout();

        Assert.False(harness.Shell.EventLog.IsViewActive); // still on Tasks
        Assert.True(harness.Shell.HasEventLogUnread);
        Assert.Equal(1, harness.Shell.EventLogUnread);

        // --- Navigate to the Event log: activation clears the badge and the grid
        //     realizes, the alert row carrying its "warning" severity class. -----
        harness.Shell.SelectViewCommand.Execute("4");
        harness.Layout();

        Assert.Same(harness.Shell.EventLog, harness.Shell.CurrentPage);
        Assert.True(harness.Shell.EventLog.IsViewActive);
        Assert.Equal(0, harness.Shell.EventLogUnread);
        Assert.False(harness.Shell.HasEventLogUnread);

        Assert.Equal(EventLogPriority.Warning,
            harness.Shell.EventLog.Rows.Single(r => r.Data.Body == "a-alert").Data.Priority);
        var warningRow = harness.Window.GetVisualDescendants().OfType<DataGridRow>()
            .Single(r => ((EventLogRow)r.DataContext!).Data.Body == "a-alert");
        Assert.Contains("warning", warningRow.Classes);

        // --- Reconnect replay: the exact same seqnos re-fetched must dedup in the
        //     identity-keyed log — no new rows. ---------------------------------
        Raise(harness, hostA.Id,
            Msg(1, 1, "a-info", MessagePriority.Info),
            Msg(2, 3, "a-alert", MessagePriority.UserAlert));
        harness.Layout();
        Assert.Equal(2, harness.Shell.EventLog.Rows.Count);
        Assert.Equal(["a-info", "a-alert"], harness.Shell.EventLog.Rows.Select(r => r.Data.Body));

        // --- Host B's message, timestamped between A's two, interleaves by time in
        //     the default All-hosts scope. ---------------------------------------
        Assert.True(harness.Shell.EventLog.IsAllHostsScope);
        Raise(harness, hostB.Id, Msg(1, 2, "b-info", MessagePriority.Info));
        await harness.SettleAsync(() => harness.Shell.EventLog.Rows.Count == 3,
            "host B's message should merge in as a third row");
        harness.Layout();

        Assert.Equal(["a-info", "b-info", "a-alert"],
            harness.Shell.EventLog.Rows.Select(r => r.Data.Body));
    }
}
