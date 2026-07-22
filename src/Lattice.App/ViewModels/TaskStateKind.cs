using Lattice.Boinc.GuiRpc;

namespace Lattice.App.ViewModels;

/// <summary>
/// The COARSE task bucket that drives the State filter dropdown, the row-count
/// summary, and suspended-row dimming. Deliberately kept to four buckets: the
/// filter operates on these, while the State column shows the fine-grained
/// <see cref="TaskStatusPolicy"/> "why" (issue #154 — filter on coarse, display fine).
/// </summary>
public enum TaskStateKind { Running, Waiting, Suspended, Uploading }

public static class TaskStateMapping
{
    // Precedence invariant: Suspended > Uploading > Running > Waiting (first match wins).
    public static TaskStateKind From(Result r) => r switch
    {
        { SuspendedViaGui: true } => TaskStateKind.Suspended,
        { State: ResultState.FilesUploading or ResultState.FilesUploaded or ResultState.UploadFailed }
            => TaskStateKind.Uploading,
        { ActiveTask.ActiveTaskState: 1 } => TaskStateKind.Running, // 1 = EXECUTING, per BOINC ACTIVE_TASK_STATE
        _ => TaskStateKind.Waiting,
    };
}
