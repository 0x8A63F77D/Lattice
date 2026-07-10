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
    string Host)
{
    /// <summary>
    /// Project a TaskSnapshot into a row suitable for binding to the DataGrid.
    /// Pure: all values computed from snapshot and host name, no I/O, no side effects.
    /// </summary>
    public static TaskRowViewModel From(TaskSnapshot snap, string host)
    {
        var r = snap.Result;

        var stateKind = TaskStateMapping.From(r);
        var fraction = ComputeFraction(r);
        var percentText = fraction switch
        {
            null => "—",
            _ => $"{Math.Round(fraction.Value * 100)}%",
        };

        var elapsedText = TimeText.Duration(snap.ElapsedSeconds);
        var remainingText = r.EstimatedCpuTimeRemaining > 0
            ? TimeText.Duration(r.EstimatedCpuTimeRemaining)
            : "—";

        var deadlineText = r.ReportDeadline?.ToLocalTime().ToString("MM-dd HH:mm") ?? "—";

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
            Host: host);
    }

    private static double? ComputeFraction(Result r) => r.ActiveTask switch
    {
        not null => r.ActiveTask.FractionDone,
        null when r.State is ResultState.FilesUploading or ResultState.FilesUploaded or ResultState.UploadFailed => 1.0,
        _ => null,
    };
}
