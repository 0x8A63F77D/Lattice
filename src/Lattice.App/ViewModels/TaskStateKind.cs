using Lattice.App.Localization;
using Lattice.Boinc.GuiRpc;

namespace Lattice.App.ViewModels;

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

#pragma warning disable CS8524 // No `_` arm on purpose: the previous `_` folded the NAMED Waiting
    // case, defeating CS8509 (a new NAMED TaskStateKind would silently take the Waiting text). Name
    // Waiting explicitly so CS8509 forces a choice for any new kind; CS8524 (residual unnamed value,
    // unreachable for a well-formed kind) is suppressed. RailTierProjection pattern. The inner switch
    // is over ResultState — a daemon-defined BOINC enum whose values are open, so it keeps its `_`.
    public static string Text(TaskStateKind kind, Result r) => kind switch
    {
        TaskStateKind.Running => Strings.TaskStateRunning,
        TaskStateKind.Suspended => Strings.TaskStateSuspended,
        TaskStateKind.Uploading => Strings.TaskStateUploading,
        TaskStateKind.Waiting => r.State switch
        {
            ResultState.ComputeError => Strings.TaskStateError,
            ResultState.Aborted => Strings.TaskStateAborted,
            _ => Strings.TaskStateWaiting,
        },
    };
#pragma warning restore CS8524
}
