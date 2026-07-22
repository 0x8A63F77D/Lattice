using Lattice.App.Localization;
using Lattice.App.ViewModels;
using Lattice.Boinc.GuiRpc;
using Xunit;

namespace Lattice.App.Tests;

/// <summary>
/// Exhaustive transition table for <see cref="TaskStatusPolicy"/>, the faithful port
/// of BOINC's result_description(). Every branch of Classify has a row; Text's
/// reason composition and the SuspendReasonText mapping are pinned separately. The
/// State-column filter/count bucket (TaskStateKind) is deliberately NOT re-derived
/// here — it is independent (issue #154).
/// </summary>
public class TaskStatusPolicyTests
{
    // No suspensions, Auto everywhere — the ambient default a healthy host reports.
    private static readonly CcStatus NoSuspend = new(
        RunMode.Auto, RunMode.Auto, RunMode.Auto,
        SuspendReason.NotSuspended, SuspendReason.NotSuspended, SuspendReason.NotSuspended,
        RunMode.Auto, 0, RunMode.Auto, 0, RunMode.Auto, 0);

    private static Result Res(
        ResultState state,
        ActiveTask? active = null,
        bool suspendedViaGui = false,
        bool projectSuspendedViaGui = false,
        bool schedulerWait = false,
        string schedulerWaitReason = "",
        bool networkWait = false,
        bool readyToReport = false,
        bool gotServerAck = false,
        int exitStatus = 0,
        string resources = "")
        => new("task", "wu", "https://p/", state, null, readyToReport, suspendedViaGui,
               0, 0, 0, 0, "", exitStatus, active,
               projectSuspendedViaGui, gotServerAck, schedulerWait, schedulerWaitReason,
               networkWait, resources);

    private static ActiveTask Active(
        int taskState = ProcessExecuting,
        SchedulerState scheduler = SchedulerState.Scheduled,
        bool wss = false, bool swap = false, bool shmem = false, bool network = false)
        => new(taskState, 0.5, 10, 90, scheduler, wss, swap, shmem, network);

    private const int ProcessExecuting = 1;
    private const int ProcessSuspended = 9;

    // ---- Classify: the structural transition table ------------------------

    [Fact]
    public void New_state()
        => Assert.Equal(TaskStatusKind.New, TaskStatusPolicy.Classify(Res(ResultState.New), NoSuspend));

    [Fact]
    public void Downloading_when_files_downloading()
        => Assert.Equal(TaskStatusKind.Downloading,
            TaskStatusPolicy.Classify(Res(ResultState.FilesDownloading), NoSuspend));

    [Fact]
    public void Download_failed_when_downloading_and_ready_to_report()
        => Assert.Equal(TaskStatusKind.DownloadFailed,
            TaskStatusPolicy.Classify(Res(ResultState.FilesDownloading, readyToReport: true), NoSuspend));

    [Fact]
    public void Uploading_when_files_uploading()
        => Assert.Equal(TaskStatusKind.Uploading,
            TaskStatusPolicy.Classify(Res(ResultState.FilesUploading), NoSuspend));

    [Fact]
    public void Upload_failed_when_uploading_and_ready_to_report()
        => Assert.Equal(TaskStatusKind.UploadFailed,
            TaskStatusPolicy.Classify(Res(ResultState.FilesUploading, readyToReport: true), NoSuspend));

    [Fact]
    public void Upload_failed_state_is_named_not_left_as_invalid_state()
        // RESULT_UPLOAD_FAILED (7) is distinguishable at the field level, so we name
        // it "Upload failed" instead of leaking the reference's "invalid state '7'"
        // (owner call, PR #159 Codex finding 2).
        => Assert.Equal(TaskStatusKind.UploadFailed,
            TaskStatusPolicy.Classify(Res(ResultState.UploadFailed), NoSuspend));

    [Fact]
    public void Computation_error_state()
        => Assert.Equal(TaskStatusKind.ComputationError,
            TaskStatusPolicy.Classify(Res(ResultState.ComputeError), NoSuspend));

    [Fact]
    public void Acknowledged_when_uploaded_and_server_acked()
        => Assert.Equal(TaskStatusKind.Acknowledged,
            TaskStatusPolicy.Classify(Res(ResultState.FilesUploaded, gotServerAck: true), NoSuspend));

    [Fact]
    public void Ready_to_report_when_uploaded_and_ready()
        => Assert.Equal(TaskStatusKind.ReadyToReport,
            TaskStatusPolicy.Classify(Res(ResultState.FilesUploaded, readyToReport: true), NoSuspend));

    [Fact]
    public void Invalid_state_when_uploaded_neither_acked_nor_ready()
        => Assert.Equal(TaskStatusKind.InvalidState,
            TaskStatusPolicy.Classify(Res(ResultState.FilesUploaded), NoSuspend));

    // FILES_DOWNLOADED sub-tree — the crux. Ordered by result_description precedence.

    [Fact]
    public void Downloaded_network_wait_overrides_everything()
        => Assert.Equal(TaskStatusKind.WaitingForNetworkAccess,
            TaskStatusPolicy.Classify(
                Res(ResultState.FilesDownloaded, suspendedViaGui: true, schedulerWait: true, networkWait: true),
                NoSuspend));

    [Fact]
    public void Downloaded_scheduler_wait_overrides_suspend()
        => Assert.Equal(TaskStatusKind.Postponed,
            TaskStatusPolicy.Classify(
                Res(ResultState.FilesDownloaded, suspendedViaGui: true, schedulerWait: true),
                NoSuspend));

    [Fact]
    public void Downloaded_project_suspended_beats_task_suspended()
        => Assert.Equal(TaskStatusKind.ProjectSuspendedByUser,
            TaskStatusPolicy.Classify(
                Res(ResultState.FilesDownloaded, suspendedViaGui: true, projectSuspendedViaGui: true),
                NoSuspend));

    [Fact]
    public void Downloaded_task_suspended_by_user()
        => Assert.Equal(TaskStatusKind.TaskSuspendedByUser,
            TaskStatusPolicy.Classify(Res(ResultState.FilesDownloaded, suspendedViaGui: true), NoSuspend));

    [Fact]
    public void Downloaded_policy_suspended_when_task_reason_and_not_executing()
        => Assert.Equal(TaskStatusKind.SuspendedByPolicy,
            TaskStatusPolicy.Classify(
                Res(ResultState.FilesDownloaded, Active(taskState: ProcessSuspended)),
                NoSuspend with { TaskSuspendReason = SuspendReason.UserActive }));

    [Fact]
    public void Downloaded_executing_task_is_not_policy_suspended_even_with_task_reason()
    {
        // An NCI process can execute while computation is globally suspended.
        var kind = TaskStatusPolicy.Classify(
            Res(ResultState.FilesDownloaded, Active(taskState: ProcessExecuting, scheduler: SchedulerState.Scheduled)),
            NoSuspend with { TaskSuspendReason = SuspendReason.UserActive });
        Assert.Equal(TaskStatusKind.Running, kind);
    }

    [Fact]
    public void Downloaded_gpu_suspended_only_for_gpu_task()
    {
        var cc = NoSuspend with { GpuSuspendReason = SuspendReason.UserActive };
        Assert.Equal(TaskStatusKind.GpuSuspended, TaskStatusPolicy.Classify(
            Res(ResultState.FilesDownloaded, Active(taskState: ProcessSuspended), resources: "0.5 CPUs + 1 NVIDIA GPU"), cc));
        // A CPU task (no "GPU" in resources) is NOT GPU-suspended: it falls through.
        Assert.NotEqual(TaskStatusKind.GpuSuspended, TaskStatusPolicy.Classify(
            Res(ResultState.FilesDownloaded, Active(taskState: ProcessSuspended), resources: "1 CPU"), cc));
    }

    [Fact]
    public void Downloaded_waiting_for_memory_swap_shmem_network_in_order()
    {
        Assert.Equal(TaskStatusKind.WaitingForMemory, TaskStatusPolicy.Classify(
            Res(ResultState.FilesDownloaded, Active(wss: true, swap: true, shmem: true, network: true)), NoSuspend));
        Assert.Equal(TaskStatusKind.WaitingForSwapSpace, TaskStatusPolicy.Classify(
            Res(ResultState.FilesDownloaded, Active(swap: true, shmem: true, network: true)), NoSuspend));
        Assert.Equal(TaskStatusKind.WaitingForSharedMemory, TaskStatusPolicy.Classify(
            Res(ResultState.FilesDownloaded, Active(shmem: true, network: true)), NoSuspend));
        Assert.Equal(TaskStatusKind.WaitingForNetwork, TaskStatusPolicy.Classify(
            Res(ResultState.FilesDownloaded, Active(network: true)), NoSuspend));
    }

    [Fact]
    public void Downloaded_scheduler_state_maps_running_preempted_uninitialized()
    {
        Assert.Equal(TaskStatusKind.Running, TaskStatusPolicy.Classify(
            Res(ResultState.FilesDownloaded, Active(scheduler: SchedulerState.Scheduled)), NoSuspend));
        Assert.Equal(TaskStatusKind.WaitingToRun, TaskStatusPolicy.Classify(
            Res(ResultState.FilesDownloaded, Active(scheduler: SchedulerState.Preempted)), NoSuspend));
        Assert.Equal(TaskStatusKind.ReadyToStart, TaskStatusPolicy.Classify(
            Res(ResultState.FilesDownloaded, Active(scheduler: SchedulerState.Uninitialized)), NoSuspend));
    }

    [Fact]
    public void Downloaded_no_active_task_is_ready_to_start()
        => Assert.Equal(TaskStatusKind.ReadyToStart,
            TaskStatusPolicy.Classify(Res(ResultState.FilesDownloaded), NoSuspend));

    [Theory]
    [InlineData(203, TaskStatusKind.AbortedByUser)]     // EXIT_ABORTED_VIA_GUI
    [InlineData(202, TaskStatusKind.AbortedByProject)]  // EXIT_ABORTED_BY_PROJECT
    [InlineData(200, TaskStatusKind.AbortedNotStarted)] // EXIT_UNSTARTED_LATE
    [InlineData(196, TaskStatusKind.AbortedDiskLimit)]  // EXIT_DISK_LIMIT_EXCEEDED
    [InlineData(197, TaskStatusKind.AbortedTimeLimit)]  // EXIT_TIME_LIMIT_EXCEEDED
    [InlineData(198, TaskStatusKind.AbortedMemLimit)]   // EXIT_MEM_LIMIT_EXCEEDED
    [InlineData(0, TaskStatusKind.Aborted)]             // any other exit status
    [InlineData(999, TaskStatusKind.Aborted)]
    public void Aborted_variants_by_exit_status(int exitStatus, TaskStatusKind expected)
        => Assert.Equal(expected,
            TaskStatusPolicy.Classify(Res(ResultState.Aborted, exitStatus: exitStatus), NoSuspend));

    // ---- Text: composition and the headline #154 example ------------------

    [Fact]
    public void Policy_suspend_text_names_the_reason()
    {
        var text = TaskStatusPolicy.Text(
            Res(ResultState.FilesDownloaded, Active(taskState: ProcessSuspended)),
            NoSuspend with { TaskSuspendReason = SuspendReason.UserActive });
        // The headline acceptance: a policy-suspended task names "computer is in use".
        Assert.Equal(string.Format(Strings.TaskStatusSuspendedReasonFmt, Strings.TaskSuspendReasonUserActive), text);
        Assert.Equal("Suspended - computer is in use", text);
    }

    [Fact]
    public void Gpu_suspend_text_names_the_reason()
    {
        var text = TaskStatusPolicy.Text(
            Res(ResultState.FilesDownloaded, Active(taskState: ProcessSuspended), resources: "1 CPU + 1 NVIDIA GPU"),
            NoSuspend with { GpuSuspendReason = SuspendReason.TimeOfDay });
        Assert.Equal(string.Format(Strings.TaskStatusGpuSuspendedReasonFmt, Strings.TaskSuspendReasonTimeOfDay), text);
    }

    [Fact]
    public void Downloading_text_annotates_network_suspension()
    {
        var suspended = TaskStatusPolicy.Text(Res(ResultState.FilesDownloading),
            NoSuspend with { NetworkSuspendReason = SuspendReason.NetworkQuotaExceeded });
        Assert.Equal(
            string.Format(Strings.TaskStatusDownloadingSuspendedFmt, Strings.TaskSuspendReasonNetworkQuota),
            suspended);
        // Without a network suspension it is the plain "Downloading".
        Assert.Equal(Strings.TaskStatusDownloading,
            TaskStatusPolicy.Text(Res(ResultState.FilesDownloading), NoSuspend));
    }

    [Fact]
    public void Uploading_text_annotates_network_suspension()
    {
        Assert.Equal(
            string.Format(Strings.TaskStatusUploadingSuspendedFmt, Strings.TaskSuspendReasonUserRequest),
            TaskStatusPolicy.Text(Res(ResultState.FilesUploading),
                NoSuspend with { NetworkSuspendReason = SuspendReason.UserRequest }));
        Assert.Equal(Strings.TaskStateUploading,
            TaskStatusPolicy.Text(Res(ResultState.FilesUploading), NoSuspend));
    }

    [Fact]
    public void Postponed_text_uses_daemon_reason_verbatim_or_bare()
    {
        Assert.Equal(string.Format(Strings.TaskStatusPostponedReasonFmt, "project backoff"),
            TaskStatusPolicy.Text(
                Res(ResultState.FilesDownloaded, schedulerWait: true, schedulerWaitReason: "project backoff"),
                NoSuspend));
        Assert.Equal(Strings.TaskStatusPostponed,
            TaskStatusPolicy.Text(Res(ResultState.FilesDownloaded, schedulerWait: true), NoSuspend));
    }

    [Fact]
    public void Invalid_state_text_includes_the_raw_state_number()
        => Assert.Equal(string.Format(Strings.TaskStatusInvalidStateFmt, (int)ResultState.FilesUploaded),
            TaskStatusPolicy.Text(Res(ResultState.FilesUploaded), NoSuspend));

    [Fact]
    public void Running_reuses_the_coarse_running_text()
        => Assert.Equal(Strings.TaskStateRunning,
            TaskStatusPolicy.Text(Res(ResultState.FilesDownloaded, Active(scheduler: SchedulerState.Scheduled)), NoSuspend));

    // ---- SuspendReasonText: the reason glossary ---------------------------

    public static TheoryData<SuspendReason, string> Reasons => new()
    {
        { SuspendReason.Batteries, Strings.TaskSuspendReasonBatteries },
        { SuspendReason.UserActive, Strings.TaskSuspendReasonUserActive },
        { SuspendReason.UserRequest, Strings.TaskSuspendReasonUserRequest },
        { SuspendReason.TimeOfDay, Strings.TaskSuspendReasonTimeOfDay },
        { SuspendReason.Benchmarks, Strings.TaskSuspendReasonBenchmarks },
        { SuspendReason.DiskSize, Strings.TaskSuspendReasonDiskSize },
        { SuspendReason.NoRecentInput, Strings.TaskSuspendReasonNoRecentInput },
        { SuspendReason.InitialDelay, Strings.TaskSuspendReasonInitialDelay },
        { SuspendReason.ExclusiveAppRunning, Strings.TaskSuspendReasonExclusiveApp },
        { SuspendReason.CpuUsage, Strings.TaskSuspendReasonCpuUsage },
        { SuspendReason.NetworkQuotaExceeded, Strings.TaskSuspendReasonNetworkQuota },
        { SuspendReason.Os, Strings.TaskSuspendReasonOs },
        { SuspendReason.WifiState, Strings.TaskSuspendReasonWifiState },
        { SuspendReason.BatteryCharging, Strings.TaskSuspendReasonBatteryCharging },
        { SuspendReason.BatteryOverheated, Strings.TaskSuspendReasonBatteryOverheated },
        { SuspendReason.NoGuiKeepalive, Strings.TaskSuspendReasonNoGuiKeepalive },
        { SuspendReason.PodmanInit, Strings.TaskSuspendReasonPodmanInit },
        { SuspendReason.BatteryChargeWait, Strings.TaskSuspendReasonBatteryChargeWait },
        { SuspendReason.BatteryHeatWait, Strings.TaskSuspendReasonBatteryHeatWait },
    };

    [Theory]
    [MemberData(nameof(Reasons))]
    public void Suspend_reason_text_maps_each_named_reason(SuspendReason reason, string expected)
        => Assert.Equal(expected, SuspendReasonText.Of(reason));

    [Theory]
    [InlineData(SuspendReason.CpuThrottle)] // 64: BOINC's suspend_reason_string has no case → unknown
    [InlineData((SuspendReason)4104)]       // just past the last named reason (BatteryHeatWait=4103)
    [InlineData((SuspendReason)999)]        // wholly unknown value cast through from the wire
    public void Suspend_reason_text_falls_back_to_unknown(SuspendReason reason)
        => Assert.Equal(Strings.TaskSuspendReasonUnknown, SuspendReasonText.Of(reason));
}
