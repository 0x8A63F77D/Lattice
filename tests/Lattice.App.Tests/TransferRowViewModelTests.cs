using Lattice.App.ViewModels;
using Lattice.Core;
using Lattice.Tests;
using Xunit;

namespace Lattice.App.Tests;

public class TransferRowViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);

    private static TransferSnapshot Snap(
        string name = "wu_1_0", string project = "P1", bool upload = true,
        double nbytes = 51.7 * 1024 * 1024, double xferred = 34.2 * 1024 * 1024,
        double speed = 0, int retries = 0, DateTimeOffset? nextRequest = null,
        TransferUiState state = TransferUiState.Queued)
    {
        var t = TestData.MakeTransfer(name, "http://p1/", project,
            xferActive: state == TransferUiState.Active,
            nextRequest: nextRequest,
            isUpload: upload,
            nbytes: nbytes,
            bytesXferred: xferred,
            numRetries: retries,
            xferSpeed: speed,
            persXferActive: state == TransferUiState.Retrying);
        return new TransferSnapshot(t, project, state);
    }

    [Fact]
    public void Progress_renders_transferred_over_total_megabytes()
    {
        var row = TransferRowViewModel.From(Snap(), Guid.NewGuid(), "host-a", Now);
        Assert.Equal("34.2 / 51.7 MB", row.ProgressText);
        Assert.True(row.Fraction is > 0.66 and < 0.67);
    }

    [Fact]
    public void Active_shows_live_speed_and_upload_direction()
    {
        var row = TransferRowViewModel.From(
            Snap(speed: 1.5 * 1024 * 1024, state: TransferUiState.Active), Guid.NewGuid(), "host-a", Now);
        Assert.Equal(TransferUiState.Active, row.UiState);
        Assert.Equal("1.5 MB/s", row.SpeedText);
        Assert.Equal("Upload", row.DirectionText);
    }

    [Fact]
    public void Slow_active_transfer_shows_adaptive_kilobyte_speed()
    {
        // The reported bug: a sub-MB rate rendered as "0.1 MB/s" under the old
        // fixed-MB formatter. Adaptive units render it in KB/s instead.
        var row = TransferRowViewModel.From(
            Snap(speed: 200 * 1024, state: TransferUiState.Active), Guid.NewGuid(), "host-a", Now);
        Assert.Equal("200 KB/s", row.SpeedText);
    }

    [Fact]
    public void Speed_sort_key_carries_raw_rate_so_adaptive_units_do_not_invert_order()
    {
        // Rates in ascending order; their adaptive strings sort differently.
        var slow = TransferRowViewModel.From(Snap(speed: 900 * 1024, state: TransferUiState.Active), Guid.NewGuid(), "h", Now);
        var mid = TransferRowViewModel.From(Snap(speed: 1024 * 1024, state: TransferUiState.Active), Guid.NewGuid(), "h", Now);
        var fast = TransferRowViewModel.From(Snap(speed: 10 * 1024 * 1024, state: TransferUiState.Active), Guid.NewGuid(), "h", Now);

        Assert.Equal("900 KB/s", slow.SpeedText);
        Assert.Equal("1 MB/s", mid.SpeedText);
        Assert.Equal("10 MB/s", fast.SpeedText);

        // The numeric key is monotonic in the true rate order …
        Assert.True(slow.SpeedBytesPerSec < mid.SpeedBytesPerSec);
        Assert.True(mid.SpeedBytesPerSec < fast.SpeedBytesPerSec);
        // … whereas an ordinal sort of the display strings puts the SLOWEST row last
        // ("900 KB/s" > "1 MB/s" and > "10 MB/s"), the exact inversion the key avoids.
        Assert.True(string.CompareOrdinal(slow.SpeedText, mid.SpeedText) > 0);
        Assert.True(string.CompareOrdinal(slow.SpeedText, fast.SpeedText) > 0);
    }

    [Fact]
    public void Inactive_row_speed_sort_key_is_zero()
    {
        // Rows that render "—" (no active transfer) sort as the slowest — key 0,
        // consistent with how the Fraction column keys non-progressing rows.
        var queued = TransferRowViewModel.From(Snap(state: TransferUiState.Queued), Guid.NewGuid(), "h", Now);
        Assert.Equal("—", queued.SpeedText);
        Assert.Equal(0, queued.SpeedBytesPerSec);
    }

    [Fact]
    public void Retrying_counts_down_from_next_request_time()
    {
        var row = TransferRowViewModel.From(
            Snap(retries: 3, nextRequest: Now.AddSeconds(161), state: TransferUiState.Retrying),
            Guid.NewGuid(), "host-a", Now);
        Assert.Equal("Retry in 02:41 (attempt 3)", row.StatusText);
        Assert.Equal("—", row.SpeedText);
        Assert.True(row.IsRetrying);
    }

    [Fact]
    public void Retry_moment_passed_clamps_to_zero()
    {
        var row = TransferRowViewModel.From(
            Snap(retries: 1, nextRequest: Now.AddSeconds(-5), state: TransferUiState.Retrying),
            Guid.NewGuid(), "host-a", Now);
        Assert.Equal("Retry in 00:00 (attempt 1)", row.StatusText);
    }

    [Fact]
    public void Queued_renders_dash_speed_and_queued_status()
    {
        var row = TransferRowViewModel.From(Snap(), Guid.NewGuid(), "host-a", Now);
        Assert.Equal("—", row.SpeedText);
        Assert.Equal("Queued", row.StatusText);
    }

    [Fact]
    public void IsRetrying_is_false_for_non_retrying_states()
    {
        var active = TransferRowViewModel.From(Snap(state: TransferUiState.Active), Guid.NewGuid(), "host-a", Now);
        var queued = TransferRowViewModel.From(Snap(state: TransferUiState.Queued), Guid.NewGuid(), "host-a", Now);
        Assert.False(active.IsRetrying);
        Assert.False(queued.IsRetrying);
    }

    [Fact]
    public void Key_is_host_project_name_direction()
    {
        var hostId = Guid.NewGuid();
        var row = TransferRowViewModel.From(Snap(), hostId, "host-a", Now);
        Assert.Equal(new TransferRowKey(hostId, "http://p1/", "wu_1_0", IsUpload: true), row.Key);
    }
}
