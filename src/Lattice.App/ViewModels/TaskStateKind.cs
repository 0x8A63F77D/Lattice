using Lattice.App.Localization;
using Lattice.Boinc.GuiRpc;

namespace Lattice.App.ViewModels;

public enum TaskStateKind { Running, Waiting, Suspended, Uploading }

public static class TaskStateMapping
{
    public static TaskStateKind From(Result r) => r switch
    {
        { SuspendedViaGui: true } => TaskStateKind.Suspended,
        { State: ResultState.FilesUploading or ResultState.FilesUploaded or ResultState.UploadFailed }
            => TaskStateKind.Uploading,
        { ActiveTask.ActiveTaskState: 1 } => TaskStateKind.Running,
        _ => TaskStateKind.Waiting,
    };

    public static string Text(TaskStateKind kind, Result r) => kind switch
    {
        TaskStateKind.Running => Strings.TaskStateRunning,
        TaskStateKind.Suspended => Strings.TaskStateSuspended,
        TaskStateKind.Uploading => Strings.TaskStateUploading,
        _ => r.State switch
        {
            ResultState.ComputeError => Strings.TaskStateError,
            ResultState.Aborted => Strings.TaskStateAborted,
            _ => Strings.TaskStateWaiting,
        },
    };
}
