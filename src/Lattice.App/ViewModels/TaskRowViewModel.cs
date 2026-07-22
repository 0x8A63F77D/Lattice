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
    double RemainingSeconds = 0,
    // Task identity for control ops (design 1.5: tasks are addressed by
    // (project_url, result name)). Default "" for hand-built test rows; From()
    // always sets it from the snapshot.
    string ProjectUrl = "")
{
    public TaskRowKey Key => new(HostId, Name);

    // Default ambient status for hand-built test rows that don't pass a cc_status:
    // nothing suspended, so the policy sees no policy-suspend reasons. The single
    // production caller always passes the host's live cc_status.
    private static readonly CcStatus NoAmbientSuspension = new(
        RunMode.Auto, RunMode.Auto, RunMode.Auto,
        SuspendReason.NotSuspended, SuspendReason.NotSuspended, SuspendReason.NotSuspended,
        RunMode.Auto, 0, RunMode.Auto, 0, RunMode.Auto, 0);

    /// <summary>
    /// Project a TaskSnapshot into a row suitable for binding to the DataGrid.
    /// Pure: all values computed from snapshot, host name, and the host's cc_status
    /// (needed for the fine-grained State text — policy-suspend reasons live there),
    /// no I/O, no side effects.
    /// </summary>
    public static TaskRowViewModel From(TaskSnapshot snap, Guid hostId, string host, CcStatus? ccStatus = null)
    {
        var r = snap.Result;
        var cc = ccStatus ?? NoAmbientSuspension;

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
            StateText: TaskStatusPolicy.Text(r, cc),
            IsDeadlineAtRisk: snap.IsDeadlineAtRisk,
            IsSuspended: stateKind == TaskStateKind.Suspended,
            HostId: hostId,
            Host: host,
            ElapsedSeconds: elapsedSeconds,
            RemainingSeconds: remainingSeconds,
            ProjectUrl: r.ProjectUrl);
    }

    private static double? ComputeFraction(Result r) => r.ActiveTask switch
    {
        not null => r.ActiveTask.FractionDone,
        null when r.State is ResultState.FilesUploading or ResultState.FilesUploaded or ResultState.UploadFailed => 1.0,
        _ => null,
    };
}
