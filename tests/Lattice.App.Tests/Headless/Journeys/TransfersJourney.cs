using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Lattice.App.Localization;
using Lattice.Boinc.GuiRpc;
using Lattice.Tests;
using Xunit;

namespace Lattice.App.Tests.Headless.Journeys;

/// <summary>
/// End-to-end: a single host reports one retrying transfer; navigating to the
/// Transfers view renders its live countdown, which ticks down with the manual
/// clock, then clears to the empty state once the next poll reports zero
/// transfers. Mirrors TasksScopeJourney's shell-composition-root idiom
/// (JourneyHarness) and TransfersViewModelTests'
/// Retrying_row_status_text_counts_down_as_the_manual_clock_advances fixture
/// (real-UtcNow-anchored nextRequest, manual-clock synced onto a fixed
/// remaining-seconds checkpoint — Setting Now alone does not trigger a
/// Rebuild, only Advance fires Tick).
/// </summary>
public class TransfersJourney
{
    [AvaloniaFact]
    public async Task Retrying_transfer_counts_down_then_clears_to_empty_state()
    {
        await using var harness = new JourneyHarness();
        var t = TestData.MakeTransfer(
            name: "transfers-journey-file", numRetries: 2, nextRequest: DateTimeOffset.UtcNow.AddMinutes(5));
        var fake = new FakeGuiRpcClient
        {
            OnGetFileTransfers = () => Task.FromResult<IReadOnlyList<FileTransfer>>([t]),
        };
        harness.AddHost("transfers-journey", fake);
        harness.Start();
        harness.Window.Show();
        harness.Layout();

        // Index 2 is Transfers (Views[0]=Tasks, [1]=Projects, [2]=Transfers, [3]=EventLog).
        harness.Shell.SelectViewCommand.Execute("2");
        harness.Layout();

        Assert.Same(harness.Shell.Transfers, harness.Shell.CurrentPage);

        await harness.SettleAsync(() => harness.Shell.Transfers.Rows.Count == 1,
            "the host should snapshot and produce one retrying row");
        harness.Layout();

        var nextRequestTime = harness.Store.Hosts[0].Snapshot!.Transfers.Single().Transfer.NextRequestTime!.Value;

        // Sync the manual clock onto a fixed remaining-seconds checkpoint,
        // independent of real wall-clock timing (the Retrying classification
        // itself was already fixed at poll time by SnapshotBuilder).
        harness.Clock.Now = nextRequestTime - TimeSpan.FromSeconds(11);
        harness.Clock.Advance(TimeSpan.FromSeconds(1));
        await harness.SettleAsync(
            () => harness.Shell.Transfers.Rows[0].Data.StatusText == "Retry in 00:10 (attempt 2)",
            "the countdown should read exactly 10s remaining");
        harness.Layout();

        harness.Clock.Advance(TimeSpan.FromSeconds(1));
        await harness.SettleAsync(
            () => harness.Shell.Transfers.Rows[0].Data.StatusText == "Retry in 00:09 (attempt 2)",
            "the countdown should decrement by one second");
        harness.Layout();

        // Next poll reports zero transfers: the row is removed and the view
        // falls back to the empty state.
        fake.OnGetFileTransfers = () => Task.FromResult<IReadOnlyList<FileTransfer>>([]);
        harness.Store.RequestRefresh(null);
        await harness.SettleAsync(() => harness.Shell.Transfers.IsEmpty,
            "the next poll should report zero transfers");
        harness.Layout();

        Assert.Empty(harness.Shell.Transfers.Rows);
        _ = harness.Window.GetVisualDescendants().OfType<TextBlock>()
            .Single(tb => tb.IsVisible && tb.Text == Strings.TransfersEmpty);
    }
}
