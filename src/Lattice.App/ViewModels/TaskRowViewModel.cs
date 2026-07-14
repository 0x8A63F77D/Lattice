using System.Globalization;
using Lattice.App.Infrastructure;
using Lattice.Boinc.GuiRpc;
using Lattice.Core;

namespace Lattice.App.ViewModels;

/// <summary>
/// Immutable row projection for a task in the Tasks view DataGrid.
/// Computed from TaskSnapshot (core + domain knowledge) and host name.
/// </summary>
public sealed record TaskRowViewModel(
    string Project,
    string Application,
    string Name,
    double? Fraction,
    string PercentText,
    string ElapsedText,
    string RemainingText,
    string DeadlineText,
    DateTimeOffset? Deadline,
    TaskStateKind StateKind,
    string StateText,
    bool IsDeadlineAtRisk,
    bool IsSuspended,
    Guid HostId,
    string Host,
    // Raw durations behind ElapsedText / RemainingText. Kept so the Elapsed / Remaining columns
    // sort by actual time (SortMemberPath -> these) rather than the formatted string, where
    // "10m" would otherwise order before "9m". Default 0 for hand-built test rows that don't
    // exercise duration sorting; From() always sets them from the snapshot.
    double ElapsedSeconds = 0,
    double RemainingSeconds = 0)
{
    public TaskRowKey Key => new(HostId, Name);

    /// <summary>
    /// Project a TaskSnapshot into a row suitable for binding to the DataGrid.
    /// Pure: all values computed from snapshot and host name, no I/O, no side effects.
    /// </summary>
    public static TaskRowViewModel From(TaskSnapshot snap, Guid hostId, string host)
    {
        var r = snap.Result;

        var stateKind = TaskStateMapping.From(r);
        var fraction = ComputeFraction(r);
        var percentText = fraction switch
        {
            null => "—",
            _ => $"{Math.Round(fraction.Value * 100)}%",
        };

        var elapsedSeconds = snap.ElapsedSeconds;
        var remainingSeconds = r.EstimatedCpuTimeRemaining;
        var elapsedText = TimeText.Duration(elapsedSeconds);
        var remainingText = remainingSeconds > 0
            ? TimeText.Duration(remainingSeconds)
            : "—";

        // Invariant culture: CurrentCulture's CALENDAR (e.g. Hijri under ar-SA)
        // would otherwise shift the rendered month/day off the Gregorian deadline.
        var deadlineText = r.ReportDeadline?.ToLocalTime()
            .ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "—";

        return new(
            Project: snap.ProjectName,
            Application: snap.ApplicationName,
            Name: r.Name,
            Fraction: fraction,
            PercentText: percentText,
            ElapsedText: elapsedText,
            RemainingText: remainingText,
            DeadlineText: deadlineText,
            Deadline: r.ReportDeadline,
            StateKind: stateKind,
            StateText: TaskStateMapping.Text(stateKind, r),
            IsDeadlineAtRisk: snap.IsDeadlineAtRisk,
            IsSuspended: stateKind == TaskStateKind.Suspended,
            HostId: hostId,
            Host: host,
            ElapsedSeconds: elapsedSeconds,
            RemainingSeconds: remainingSeconds);
    }

    private static double? ComputeFraction(Result r) => r.ActiveTask switch
    {
        not null => r.ActiveTask.FractionDone,
        null when r.State is ResultState.FilesUploading or ResultState.FilesUploaded or ResultState.UploadFailed => 1.0,
        _ => null,
    };
}
