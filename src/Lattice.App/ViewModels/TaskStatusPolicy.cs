using Lattice.App.Localization;
using Lattice.Boinc.GuiRpc;

namespace Lattice.App.ViewModels;

/// <summary>
/// The fine-grained "why" behind a task's coarse <see cref="TaskStateKind"/>: the
/// specific status the official BOINC Manager would show for the same daemon state.
///
/// This is a faithful port of <c>result_description()</c>
/// (clientgui/MainDocument.cpp) — the function the Manager's Tasks view calls to
/// fill its Status column. The mapping is source-pinned, not invented; see the PR
/// body for the field→status table with file/line cites. Two functions:
/// <see cref="Classify"/> is the structural decision (a closed <see cref="TaskStatusKind"/>,
/// exhaustively tested), and <see cref="Text"/> renders it, composing the localized
/// suspend reason where a status names one.
///
/// Scope note (documented deviations from result_description): the "GPU missing, "
/// prefix (coproc_missing) and the trailing resource-usage suffix are omitted —
/// neither distinguishes a *waiting* reason, and both belong to a resource column,
/// not the status text (issue #154, owner text-only directive). The
/// "(non-CPU-intensive)" Running suffix is also omitted (needs a project join for a
/// cosmetic tag). Everything else — download/upload states, the full
/// FILES_DOWNLOADED waiting sub-tree, aborted variants, terminal reporting states —
/// is reproduced.
/// </summary>
public static class TaskStatusPolicy
{
    // Process exit codes (lib/error_numbers.h) for the ABORTED sub-variants that
    // result_description() distinguishes. exit_status is an open int, so the switch
    // over it keeps its default arm.
    private const int ExitDiskLimitExceeded = 196;  // EXIT_DISK_LIMIT_EXCEEDED
    private const int ExitTimeLimitExceeded = 197;  // EXIT_TIME_LIMIT_EXCEEDED
    private const int ExitMemLimitExceeded = 198;   // EXIT_MEM_LIMIT_EXCEEDED
    private const int ExitUnstartedLate = 200;      // EXIT_UNSTARTED_LATE
    private const int ExitAbortedByProject = 202;   // EXIT_ABORTED_BY_PROJECT
    private const int ExitAbortedViaGui = 203;      // EXIT_ABORTED_VIA_GUI

    // ACTIVE_TASK::task_state value for a running process (lib/common_defs.h).
    private const int ProcessExecuting = 1;         // PROCESS_EXECUTING

    /// <summary>
    /// The structural status outcome for a task, given its result and the host's
    /// cc_status (run modes + suspend reasons). Pure and total: the outer switch is
    /// over daemon enums (ResultState, SchedulerState) so they keep a default arm,
    /// but the returned <see cref="TaskStatusKind"/> is closed.
    /// </summary>
    public static TaskStatusKind Classify(Result r, CcStatus cc) => r.State switch
    {
        ResultState.New => TaskStatusKind.New,
        ResultState.FilesDownloading =>
            r.ReadyToReport ? TaskStatusKind.DownloadFailed : TaskStatusKind.Downloading,
        ResultState.FilesDownloaded => ClassifyDownloaded(r, cc),
        ResultState.ComputeError => TaskStatusKind.ComputationError,
        ResultState.FilesUploading =>
            r.ReadyToReport ? TaskStatusKind.UploadFailed : TaskStatusKind.Uploading,
        // Deviation from result_description (owner call, PR #159): the reference has
        // no case for RESULT_UPLOAD_FAILED, so it leaks the default branch's
        // "invalid state '7'" for an upload-failed task. We CAN distinguish the state
        // at the field level, so name it accurately instead. (Reference would show
        // "Ready to report"/"Acknowledged"/"invalid state '7'".)
        ResultState.UploadFailed => TaskStatusKind.UploadFailed,
        ResultState.Aborted => ClassifyAborted(r.ExitStatus),
        // FILES_UPLOADED (5) and any newer state: the daemon has computed/uploaded
        // and is waiting to notify the scheduler.
        _ => r.GotServerAck ? TaskStatusKind.Acknowledged
            : r.ReadyToReport ? TaskStatusKind.ReadyToReport
            : TaskStatusKind.InvalidState,
    };

    // The FILES_DOWNLOADED sub-tree — the crux of #154: everything the coarse view
    // lumps as "Waiting" is distinguished here. Precedence follows result_description
    // top to bottom. The scheduler_wait / network_wait overrides sit FIRST because
    // in the reference they unconditionally REPLACE the computed string (strBuffer =
    // ...), so they win over even the suspend branches; network_wait is applied last
    // there, so it beats scheduler_wait here.
    private static TaskStatusKind ClassifyDownloaded(Result r, CcStatus cc)
    {
        if (r.NetworkWait) return TaskStatusKind.WaitingForNetworkAccess;
        if (r.SchedulerWait) return TaskStatusKind.Postponed;
        if (r.ProjectSuspendedViaGui) return TaskStatusKind.ProjectSuspendedByUser;
        if (r.SuspendedViaGui) return TaskStatusKind.TaskSuspendedByUser;

        int activeTaskState = r.ActiveTask?.ActiveTaskState ?? 0;
        // An NCI (non-CPU-intensive) process can keep executing even while
        // computation is globally suspended, so a task that is itself EXECUTING is
        // not "Suspended" (reference comment on <dont_suspend_nci>).
        if (cc.TaskSuspendReason != SuspendReason.NotSuspended && activeTaskState != ProcessExecuting)
            return TaskStatusKind.SuspendedByPolicy;
        if (cc.GpuSuspendReason != SuspendReason.NotSuspended && UsesGpu(r))
            return TaskStatusKind.GpuSuspended;

        if (r.ActiveTask is { } at)
        {
            if (at.WssTooLarge) return TaskStatusKind.WaitingForMemory;
            if (at.SwapTooLarge) return TaskStatusKind.WaitingForSwapSpace;
            if (at.NeedsShmem) return TaskStatusKind.WaitingForSharedMemory;
            if (at.WantNetwork) return TaskStatusKind.WaitingForNetwork;
            return at.SchedulerState switch
            {
                SchedulerState.Scheduled => TaskStatusKind.Running,
                SchedulerState.Preempted => TaskStatusKind.WaitingToRun,
                // Uninitialized, or any value a newer daemon adds.
                _ => TaskStatusKind.ReadyToStart,
            };
        }
        return TaskStatusKind.ReadyToStart;
    }

    private static TaskStatusKind ClassifyAborted(int exitStatus) => exitStatus switch
    {
        ExitAbortedViaGui => TaskStatusKind.AbortedByUser,
        ExitAbortedByProject => TaskStatusKind.AbortedByProject,
        ExitUnstartedLate => TaskStatusKind.AbortedNotStarted,
        ExitDiskLimitExceeded => TaskStatusKind.AbortedDiskLimit,
        ExitTimeLimitExceeded => TaskStatusKind.AbortedTimeLimit,
        ExitMemLimitExceeded => TaskStatusKind.AbortedMemLimit,
        _ => TaskStatusKind.Aborted,
    };

    // uses_gpu(): the daemon's resource string names the coprocessor, so a task is a
    // GPU task iff its <resources> contains "GPU" (clientgui/MainDocument.cpp kludge,
    // case-sensitive to match).
    private static bool UsesGpu(Result r) => r.Resources.Contains("GPU", StringComparison.Ordinal);

#pragma warning disable CS8524 // No `_` arm on purpose: CS8509 must stay live so that
    // adding a NAMED TaskStatusKind is a build error until its text is chosen here.
    // CS8524 (residual unnamed value, unreachable for a well-formed kind) is suppressed.
    // Same RailTierProjection / TaskStateMapping pattern used across the App policies.
    /// <summary>
    /// The localized status string for a task. Renders <see cref="Classify"/>'s
    /// outcome, composing the suspend reason (<see cref="SuspendReasonText"/>) where a
    /// status names one, and the daemon's verbatim postpone reason for Postponed.
    /// </summary>
    public static string Text(Result r, CcStatus cc) => Classify(r, cc) switch
    {
        TaskStatusKind.New => Strings.TaskStatusNew,
        // A downloading task whose network is policy-suspended annotates the reason.
        TaskStatusKind.Downloading => cc.NetworkSuspendReason != SuspendReason.NotSuspended
            ? string.Format(Strings.TaskStatusDownloadingSuspendedFmt, SuspendReasonText.Of(cc.NetworkSuspendReason))
            : Strings.TaskStatusDownloading,
        TaskStatusKind.DownloadFailed => Strings.TaskStatusDownloadFailed,
        TaskStatusKind.ProjectSuspendedByUser => Strings.TaskStatusProjectSuspended,
        TaskStatusKind.TaskSuspendedByUser => Strings.TaskStatusTaskSuspended,
        TaskStatusKind.SuspendedByPolicy =>
            string.Format(Strings.TaskStatusSuspendedReasonFmt, SuspendReasonText.Of(cc.TaskSuspendReason)),
        TaskStatusKind.GpuSuspended =>
            string.Format(Strings.TaskStatusGpuSuspendedReasonFmt, SuspendReasonText.Of(cc.GpuSuspendReason)),
        TaskStatusKind.WaitingForMemory => Strings.TaskStatusWaitingForMemory,
        TaskStatusKind.WaitingForSwapSpace => Strings.TaskStatusWaitingForSwap,
        TaskStatusKind.WaitingForSharedMemory => Strings.TaskStatusWaitingForSharedMemory,
        TaskStatusKind.WaitingForNetwork => Strings.TaskStatusWaitingForNetwork,
        // Running / Uploading / Aborted (generic) reuse the coarse TaskState* text.
        TaskStatusKind.Running => Strings.TaskStateRunning,
        TaskStatusKind.WaitingToRun => Strings.TaskStatusWaitingToRun,
        TaskStatusKind.ReadyToStart => Strings.TaskStatusReadyToStart,
        // scheduler_wait_reason is daemon-supplied free text (project-specific),
        // shown verbatim; blank falls back to the bare "Postponed".
        TaskStatusKind.Postponed => r.SchedulerWaitReason.Length > 0
            ? string.Format(Strings.TaskStatusPostponedReasonFmt, r.SchedulerWaitReason)
            : Strings.TaskStatusPostponed,
        TaskStatusKind.WaitingForNetworkAccess => Strings.TaskStatusWaitingForNetworkAccess,
        TaskStatusKind.ComputationError => Strings.TaskStatusComputationError,
        TaskStatusKind.Uploading => cc.NetworkSuspendReason != SuspendReason.NotSuspended
            ? string.Format(Strings.TaskStatusUploadingSuspendedFmt, SuspendReasonText.Of(cc.NetworkSuspendReason))
            : Strings.TaskStateUploading,
        TaskStatusKind.UploadFailed => Strings.TaskStatusUploadFailed,
        TaskStatusKind.AbortedByUser => Strings.TaskStatusAbortedByUser,
        TaskStatusKind.AbortedByProject => Strings.TaskStatusAbortedByProject,
        TaskStatusKind.AbortedNotStarted => Strings.TaskStatusAbortedNotStarted,
        TaskStatusKind.AbortedDiskLimit => Strings.TaskStatusAbortedDiskLimit,
        TaskStatusKind.AbortedTimeLimit => Strings.TaskStatusAbortedTimeLimit,
        TaskStatusKind.AbortedMemLimit => Strings.TaskStatusAbortedMemLimit,
        TaskStatusKind.Aborted => Strings.TaskStateAborted,
        TaskStatusKind.Acknowledged => Strings.TaskStatusAcknowledged,
        TaskStatusKind.ReadyToReport => Strings.TaskStatusReadyToReport,
        TaskStatusKind.InvalidState => string.Format(Strings.TaskStatusInvalidStateFmt, (int)r.State),
    };
#pragma warning restore CS8524
}

/// <summary>
/// The closed set of fine-grained task statuses (BOINC result_description outcomes).
/// A domain DU: switches over it are exhaustive (no wildcard) so a new status forces
/// every consumer to choose its text.
/// </summary>
public enum TaskStatusKind
{
    New,
    Downloading,
    DownloadFailed,
    ProjectSuspendedByUser,
    TaskSuspendedByUser,
    SuspendedByPolicy,
    GpuSuspended,
    WaitingForMemory,
    WaitingForSwapSpace,
    WaitingForSharedMemory,
    WaitingForNetwork,
    Running,
    WaitingToRun,
    ReadyToStart,
    Postponed,
    WaitingForNetworkAccess,
    ComputationError,
    Uploading,
    UploadFailed,
    AbortedByUser,
    AbortedByProject,
    AbortedNotStarted,
    AbortedDiskLimit,
    AbortedTimeLimit,
    AbortedMemLimit,
    Aborted,
    Acknowledged,
    ReadyToReport,
    InvalidState,
}

/// <summary>
/// Localized text for a <see cref="SuspendReason"/>, mirroring BOINC's
/// <c>suspend_reason_string()</c> (lib/str_util.cpp). Composed into the
/// "Suspended - {reason}" family by <see cref="TaskStatusPolicy"/>.
/// </summary>
public static class SuspendReasonText
{
    // SuspendReason is a daemon enum: values are cast through from the wire and newer
    // daemons add cases (Podman, battery waits) our enum doesn't name, so this switch
    // KEEPS a default arm — mapping every unmapped/unknown reason to "unknown reason",
    // exactly as suspend_reason_string()'s default does. (This is the sanctioned
    // predicate/daemon-enum exception to the no-wildcard rule, not a domain DU.)
    public static string Of(SuspendReason reason) => reason switch
    {
        SuspendReason.Batteries => Strings.TaskSuspendReasonBatteries,
        SuspendReason.UserActive => Strings.TaskSuspendReasonUserActive,
        SuspendReason.UserRequest => Strings.TaskSuspendReasonUserRequest,
        SuspendReason.TimeOfDay => Strings.TaskSuspendReasonTimeOfDay,
        SuspendReason.Benchmarks => Strings.TaskSuspendReasonBenchmarks,
        SuspendReason.DiskSize => Strings.TaskSuspendReasonDiskSize,
        SuspendReason.NoRecentInput => Strings.TaskSuspendReasonNoRecentInput,
        SuspendReason.InitialDelay => Strings.TaskSuspendReasonInitialDelay,
        SuspendReason.ExclusiveAppRunning => Strings.TaskSuspendReasonExclusiveApp,
        SuspendReason.CpuUsage => Strings.TaskSuspendReasonCpuUsage,
        SuspendReason.NetworkQuotaExceeded => Strings.TaskSuspendReasonNetworkQuota,
        SuspendReason.Os => Strings.TaskSuspendReasonOs,
        SuspendReason.WifiState => Strings.TaskSuspendReasonWifiState,
        SuspendReason.BatteryCharging => Strings.TaskSuspendReasonBatteryCharging,
        SuspendReason.BatteryOverheated => Strings.TaskSuspendReasonBatteryOverheated,
        SuspendReason.NoGuiKeepalive => Strings.TaskSuspendReasonNoGuiKeepalive,
        SuspendReason.PodmanInit => Strings.TaskSuspendReasonPodmanInit,
        SuspendReason.BatteryChargeWait => Strings.TaskSuspendReasonBatteryChargeWait,
        SuspendReason.BatteryHeatWait => Strings.TaskSuspendReasonBatteryHeatWait,
        // NotSuspended (guarded upstream), CpuThrottle (never in a status per BOINC),
        // and any newer/unknown reason.
        _ => Strings.TaskSuspendReasonUnknown,
    };
}
