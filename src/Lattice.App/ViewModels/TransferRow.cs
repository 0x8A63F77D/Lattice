using System.Globalization;
using Lattice.App.Infrastructure;
using Lattice.App.Localization;
using Lattice.Core;

namespace Lattice.App.ViewModels;

/// <summary>
/// Row identity in the Transfers grid: the daemon keys a transfer by
/// (project, file name, direction); the same file name can exist on
/// multiple hosts and as both upload and download.
/// </summary>
public readonly record struct TransferRowKey(Guid HostId, string ProjectUrl, string Name, bool IsUpload);

/// <summary>
/// Closed holder for Transfers-view rows: XAML cannot name generic types, so
/// the DataGrid binds TransferRow (x:DataType) while the reconciler works
/// against the RowHolder base — the same shape as TaskRow. Data swaps in
/// place on value-change polls; the instance — and therefore DataGrid
/// selection — survives.
/// </summary>
public sealed class TransferRow(TransferRowKey key, TransferRowViewModel data)
    : RowHolder<TransferRowKey, TransferRowViewModel>(key, data);

/// <summary>
/// Immutable row projection for a file transfer. Pure over (snapshot, host,
/// now); "now" drives the retry countdown, re-rendered by the VM's per-tick
/// Rebuild — the same mechanism that keeps the Tasks freshness text moving.
/// </summary>
public sealed record TransferRowViewModel(
    TransferRowKey Key,
    string Name,
    string Project,
    string DirectionText,
    string ProgressText,
    double Fraction,
    string SpeedText,
    TransferUiState UiState,
    string StatusText,
    Guid HostId,
    string Host,
    // Raw transfer rate (bytes/sec) backing the Speed column's header sort. The
    // Speed column binds the adaptive SpeedText for display but sorts on THIS —
    // sorting the formatted string orders "10 MB/s" < "1 MB/s" < "900 KB/s"
    // (lexicographic), inverting the true rate. Zero for rows that render "—",
    // so they sort as the slowest (mirrors the Fraction column's non-progressing
    // rows). Optional/defaulted so hand-built test rows need not set it.
    double SpeedBytesPerSec = 0)
{
    /// <summary>Convenience predicate for XAML/bounded lookups that only care about the retry state.</summary>
    public bool IsRetrying => UiState == TransferUiState.Retrying;

    /// <summary>
    /// Project a TransferSnapshot into a row suitable for binding to the DataGrid.
    /// Pure: all values computed from snapshot, host name, and "now" — no I/O, no side effects.
    /// </summary>
    public static TransferRowViewModel From(TransferSnapshot snap, Guid hostId, string host, DateTimeOffset now)
    {
        var t = snap.Transfer;
        var fraction = t.Nbytes > 0 ? Math.Clamp(t.BytesXferred / t.Nbytes, 0, 1) : 0;

        var statusText = snap.UiState switch
        {
            TransferUiState.Active => Strings.TransfersStatusActive,
            TransferUiState.Retrying => RetryText(t.NextRequestTime, t.NumRetries, now),
            TransferUiState.Queued => Strings.TransfersStatusQueued,
            _ => throw new InvalidOperationException("unreachable: closed enum"),
        };

        var hasActiveSpeed = snap.UiState == TransferUiState.Active && t.XferSpeed > 0;

        return new(
            Key: new TransferRowKey(hostId, t.ProjectUrl, t.Name, t.IsUpload),
            Name: t.Name,
            Project: snap.ProjectName,
            DirectionText: t.IsUpload ? Strings.TransfersUpload : Strings.TransfersDownload,
            ProgressText: string.Format(CultureInfo.InvariantCulture, Strings.TransfersProgressFmt, Mb(t.BytesXferred), Mb(t.Nbytes)),
            Fraction: fraction,
            SpeedText: hasActiveSpeed ? ByteRateFormat.Format(t.XferSpeed) : "—",
            UiState: snap.UiState,
            StatusText: statusText,
            HostId: hostId,
            Host: host,
            SpeedBytesPerSec: hasActiveSpeed ? t.XferSpeed : 0);
    }

    private static string RetryText(DateTimeOffset? nextRequest, int attempt, DateTimeOffset now)
    {
        var remaining = nextRequest is { } at && at > now ? at - now : TimeSpan.Zero;
        // mm:ss with minutes allowed past 59 (backoffs can exceed an hour).
        var mmss = string.Create(CultureInfo.InvariantCulture,
            $"{(int)remaining.TotalMinutes:00}:{remaining.Seconds:00}");
        return string.Format(CultureInfo.InvariantCulture, Strings.TransfersRetryFmt, mmss, attempt);
    }

    private static string Mb(double bytes) =>
        (bytes / (1024.0 * 1024.0)).ToString("0.#", CultureInfo.InvariantCulture);
}
