#if DEBUG
using Lattice.Boinc.GuiRpc;
using Lattice.Core;
// Disambiguate the BOINC app model from Lattice.App.App (the Application class),
// which otherwise wins name resolution inside the Lattice.App.* namespace.
using BoincApp = Lattice.Boinc.GuiRpc.App;

namespace Lattice.App.Infrastructure;

/// <summary>
/// One canned sample host: a <see cref="HostConfig"/> plus the raw RPC replies a
/// real daemon would return. It flows through the SAME
/// registry → HostMonitor → SnapshotBuilder → HostStore → ViewModel path as a
/// live host — nothing here builds a <see cref="HostSnapshot"/> directly, so the
/// UI derives every value (aggregation, tiers, view slices) exactly as it would
/// from a real client.
/// </summary>
internal sealed record SampleHostData(
    HostConfig Config,
    CcState State,
    CcStatus Status,
    IReadOnlyList<Result> Results,
    IReadOnlyList<FileTransfer> Transfers,
    IReadOnlyList<Message> Messages);

/// <summary>
/// DEBUG-only injectable sample fleet (shell-design §12.4). The live test daemon
/// has 0 attached projects, so the data-rich states — 500+ tasks (virtualization
/// check), transfers in every state, multi-project aggregates with
/// <c>Varies</c>/mixed status tiers — can only be exercised from canned data.
///
/// Entirely under <c>#if DEBUG</c>: never compiled into Release (a Release build
/// contains no <c>Sample*</c> type — asserted by SampleHostReleaseExclusionTests).
/// It touches no protocol or Lattice.Core code; the seam is the App composition
/// root (<see cref="Compose"/>), the same shape the headless tests use — a fake
/// <see cref="IGuiRpcClient"/> routed per host address.
///
/// Aggregation note: <c>Varies</c> and mixed status tiers are multi-host
/// properties (a project attached to several hosts with differing share/status),
/// so the fleet is deliberately three hosts sharing projects — a single host
/// could never surface them (see Lattice.App.Aggregation.ProjectRows).
/// </summary>
internal static class SampleHost
{
    /// <summary>Set this env var to any of 1/true/yes/on to inject the fleet in a DEBUG run.</summary>
    public const string EnvVar = "LATTICE_SAMPLE_HOSTS";

    /// <summary>
    /// Opt-in DEBUG "live progress" aid (set to 1/true/yes/on ALONGSIDE <see cref="EnvVar"/>):
    /// makes the canned fleet's ACTIVE transfers and running tasks advance their progress a
    /// step on every poll, looping at the top. The canned data is otherwise static (a snapshot),
    /// so a progress bar never changes value and its width transition never fires — this exists
    /// purely so the owner can eyeball the Wave-3 progress-width motion (200 ms easyEase) on the
    /// live app. Off by default; the fleet's shape (states, tiers, counts) is unchanged.
    /// </summary>
    public const string TickEnvVar = "LATTICE_SAMPLE_TICK";

    public static bool Ticking => IsTruthy(Environment.GetEnvironmentVariable(TickEnvVar));

    /// <summary>Per-poll progress step for the live-progress aid (fraction of the whole).</summary>
    private const double TickStep = 0.08;

    /// <summary>
    /// Advances one transfer for the live-progress aid: an ACTIVE transfer climbs by
    /// <see cref="TickStep"/> of its size and loops back near the start once it fills, so the bar
    /// keeps moving for a walkthrough; retrying/queued transfers (not <c>XferActive</c>) are left
    /// exactly as canned. Pure — same input, same output.
    /// </summary>
    internal static FileTransfer AdvanceTransfer(FileTransfer t)
    {
        if (!t.XferActive)
            return t;
        double next = t.BytesXferred + t.Nbytes * TickStep;
        if (next >= t.Nbytes)
            next = t.Nbytes * TickStep; // loop, don't clamp — a pinned-full bar would stop animating
        return t with { BytesXferred = next, FileOffset = next };
    }

    /// <summary>
    /// Advances one task for the live-progress aid: a running task (one that carries an
    /// <see cref="ActiveTask"/>) climbs its <see cref="ActiveTask.FractionDone"/> and loops;
    /// waiting/suspended/uploading tasks (no active task) are left as canned. Pure.
    /// </summary>
    internal static Result AdvanceResult(Result r)
    {
        if (r.ActiveTask is not { } active)
            return r;
        double next = active.FractionDone + TickStep;
        if (next >= 1.0)
            next = TickStep;
        return r with { ActiveTask = active with { FractionDone = next } };
    }

    // Stable identities so the fleet keeps the same host ids across restarts
    // (rail order, scope pins, and per-host UI state stay put).
    private static readonly Guid AlphaId = new("5a3f0001-0000-4000-8000-000000000001");
    private static readonly Guid BetaId = new("5a3f0002-0000-4000-8000-000000000002");
    private static readonly Guid GammaId = new("5a3f0003-0000-4000-8000-000000000003");

    // Sentinel addresses: the routing client matches on these to serve canned
    // data; any other address falls through to the real BOINC client.
    private const string AlphaAddress = "sample-alpha";
    private const string BetaAddress = "sample-beta";
    private const string GammaAddress = "sample-gamma";

    private const string EinsteinUrl = "https://einstein.phys.uwm.edu/";
    private const string RosettaUrl = "https://boinc.bakerlab.org/rosetta/";
    private const string LhcUrl = "https://lhcathome.cern.ch/lhcathome/";

    public static bool Enabled => IsTruthy(Environment.GetEnvironmentVariable(EnvVar));

    private static bool IsTruthy(string? value) =>
        value is not null
        && (value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("on", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Augments the real registry/factory with the sample fleet without touching
    /// the user's config on disk: the returned registry saves to a throwaway temp
    /// path, so any Settings mutation while sample mode is on cannot overwrite the
    /// real <c>config.json</c>. Real hosts still load and connect for real; the
    /// factory routes sample addresses to canned data and everything else to the
    /// live client.
    /// </summary>
    public static (HostRegistry Registry, Func<IGuiRpcClient> Factory) Compose(
        HostRegistry real, Func<IGuiRpcClient> realFactory, DateTimeOffset now)
    {
        IReadOnlyList<SampleHostData> fleet = BuildHosts(now);
        Dictionary<string, SampleHostData> byAddress = fleet.ToDictionary(h => h.Config.Address);

        var merged = new LatticeConfig(
            real.PollingIntervalSeconds,
            [.. real.Hosts, .. fleet.Select(h => h.Config)]);
        var tempPath = Path.Combine(Path.GetTempPath(), "lattice-sample-hosts.json");
        var registry = new HostRegistry(merged, tempPath);

        Func<IGuiRpcClient> factory = () => new SampleRoutingGuiRpcClient(realFactory, byAddress);
        return (registry, factory);
    }

    /// <summary>
    /// The canned fleet, resolved against <paramref name="now"/> so retry
    /// countdowns land in the future for both the live clock (real app) and the
    /// fixture's frozen clock (headless test). Pure — same input, same output.
    /// </summary>
    public static IReadOnlyList<SampleHostData> BuildHosts(DateTimeOffset now) =>
    [
        Alpha(now),
        Beta(now),
        Gamma(now),
    ];

    // ---- Hosts -------------------------------------------------------------

    // Alpha: the busy host — 520 tasks (virtualization), all three projects
    // active, transfers in every state.
    private static SampleHostData Alpha(DateTimeOffset now)
    {
        Project einstein = ProjectOf(EinsteinUrl, "Einstein@Home", share: 100);
        Project rosetta = ProjectOf(RosettaUrl, "Rosetta@home", share: 150);
        Project lhc = ProjectOf(LhcUrl, "LHC@home", share: 80);

        List<Result> results =
        [
            .. Tasks(EinsteinUrl, "wu_einstein", "alpha", 300, now),
            .. Tasks(RosettaUrl, "wu_rosetta", "alpha", 140, now),
            .. Tasks(LhcUrl, "wu_lhc", "alpha", 80, now),
        ];

        List<FileTransfer> transfers =
        [
            Xfer("gw_O3_0421_1.dat", EinsteinUrl, "Einstein@Home", TransferKind.Active, up: false, now, fraction: 0.62, speed: 480_000),
            Xfer("gw_O3_0421_2.dat", EinsteinUrl, "Einstein@Home", TransferKind.Active, up: false, now, fraction: 0.18, speed: 210_000),
            Xfer("rosetta_9d1_result.zip", RosettaUrl, "Rosetta@home", TransferKind.Active, up: true, now, fraction: 0.94, speed: 96_000),
            Xfer("sixtrack_out_88.bin", LhcUrl, "LHC@home", TransferKind.Retrying, up: true, now, fraction: 0.40, retries: 3),
            Xfer("gw_O3_0421_3.dat", EinsteinUrl, "Einstein@Home", TransferKind.Retrying, up: false, now, fraction: 0.05, retries: 1),
            Xfer("rosetta_9d2_input.dat", RosettaUrl, "Rosetta@home", TransferKind.Queued, up: false, now, fraction: 0.0),
            Xfer("sixtrack_in_89.zip", LhcUrl, "LHC@home", TransferKind.Queued, up: false, now, fraction: 0.0),
        ];

        var state = new CcState(
            new VersionInfo(8, 2, 11),
            HostInfoOf("sample-alpha.local", "Linux Ryzen 9"),
            [einstein, rosetta, lhc],
            [AppOf("einstein_O3AS", "Gravitational Wave search O3"),
             AppOf("rosetta", "Rosetta"),
             AppOf("sixtrack", "SixTrack")],
            [],
            [WorkunitOf("wu_einstein", "einstein_O3AS"),
             WorkunitOf("wu_rosetta", "rosetta"),
             WorkunitOf("wu_lhc", "sixtrack")],
            results);

        List<Message> messages =
        [
            Msg("Einstein@Home", MessagePriority.Info, 1, now.AddMinutes(-42), "Requesting new tasks for CPU"),
            Msg("Rosetta@home", MessagePriority.Info, 2, now.AddMinutes(-30), "Finished upload of rosetta_9d1_result.zip"),
            Msg("LHC@home", MessagePriority.UserAlert, 3, now.AddMinutes(-8), "Temporarily failed upload: transient HTTP error"),
        ];

        return new SampleHostData(
            new HostConfig(AlphaId, "Sample · Alpha", AlphaAddress, 31416, "sample"),
            state, RunningStatus, results, transfers, messages);
    }

    // Beta: shares Einstein (SUSPENDED here) and Rosetta with Alpha; contributes
    // the "suspended" leg of Einstein's mixed status tier.
    private static SampleHostData Beta(DateTimeOffset now)
    {
        Project einstein = ProjectOf(EinsteinUrl, "Einstein@Home", share: 50, suspended: true);
        Project rosetta = ProjectOf(RosettaUrl, "Rosetta@home", share: 150);

        List<Result> results =
        [
            .. Tasks(EinsteinUrl, "wu_einstein", "beta", 30, now),
            .. Tasks(RosettaUrl, "wu_rosetta", "beta", 30, now),
        ];

        List<FileTransfer> transfers =
        [
            Xfer("rosetta_b1_result.zip", RosettaUrl, "Rosetta@home", TransferKind.Active, up: true, now, fraction: 0.71, speed: 88_000),
            Xfer("rosetta_b2_input.dat", RosettaUrl, "Rosetta@home", TransferKind.Queued, up: false, now, fraction: 0.0),
        ];

        var state = new CcState(
            new VersionInfo(8, 0, 4),
            HostInfoOf("sample-beta.local", "macOS Apple M2"),
            [einstein, rosetta],
            [AppOf("einstein_O3AS", "Gravitational Wave search O3"),
             AppOf("rosetta", "Rosetta")],
            [],
            [WorkunitOf("wu_einstein", "einstein_O3AS"),
             WorkunitOf("wu_rosetta", "rosetta")],
            results);

        List<Message> messages =
        [
            Msg("Einstein@Home", MessagePriority.Info, 1, now.AddMinutes(-55), "Project suspended by user"),
            Msg("Rosetta@home", MessagePriority.Info, 2, now.AddMinutes(-12), "Computation for task rosetta finished"),
        ];

        return new SampleHostData(
            new HostConfig(BetaId, "Sample · Beta", BetaAddress, 31416, "sample"),
            state, RunningStatus, results, transfers, messages);
    }

    // Gamma: shares Einstein (NO NEW TASKS here) and LHC; contributes the
    // "no new tasks" leg of Einstein's mixed status tier, and the widest share.
    private static SampleHostData Gamma(DateTimeOffset now)
    {
        Project einstein = ProjectOf(EinsteinUrl, "Einstein@Home", share: 200, noNewTasks: true);
        Project lhc = ProjectOf(LhcUrl, "LHC@home", share: 80);

        List<Result> results =
        [
            .. Tasks(EinsteinUrl, "wu_einstein", "gamma", 20, now),
            .. Tasks(LhcUrl, "wu_lhc", "gamma", 20, now),
        ];

        List<FileTransfer> transfers =
        [
            Xfer("sixtrack_g_out_5.bin", LhcUrl, "LHC@home", TransferKind.Retrying, up: true, now, fraction: 0.33, retries: 2),
        ];

        var state = new CcState(
            new VersionInfo(7, 24, 1),
            HostInfoOf("sample-gamma.local", "Windows 11 Core i7"),
            [einstein, lhc],
            [AppOf("einstein_O3AS", "Gravitational Wave search O3"),
             AppOf("sixtrack", "SixTrack")],
            [],
            [WorkunitOf("wu_einstein", "einstein_O3AS"),
             WorkunitOf("wu_lhc", "sixtrack")],
            results);

        List<Message> messages =
        [
            Msg("Einstein@Home", MessagePriority.Info, 1, now.AddMinutes(-20), "Not requesting tasks: don't need (config: no new tasks)"),
            Msg("LHC@home", MessagePriority.Info, 2, now.AddMinutes(-3), "Sending scheduler request: to report completed tasks"),
        ];

        return new SampleHostData(
            new HostConfig(GammaId, "Sample · Gamma", GammaAddress, 31416, "sample"),
            state, RunningStatus, results, transfers, messages);
    }

    // ---- Builders ----------------------------------------------------------

    private static readonly CcStatus RunningStatus = new(
        RunMode.Auto, RunMode.Auto, RunMode.Auto,
        SuspendReason.NotSuspended, SuspendReason.NotSuspended, SuspendReason.NotSuspended,
        RunMode.Auto, 0, RunMode.Auto, 0, RunMode.Auto, 0);

    private static Project ProjectOf(
        string url, string name, double share, bool suspended = false, bool noNewTasks = false) =>
        new(url, name,
            UserTotalCredit: 4_200_000, UserExpavgCredit: 1_850,
            HostTotalCredit: 512_000, HostExpavgCredit: 640,
            ResourceShare: share, SuspendedViaGui: suspended, DontRequestMoreWork: noNewTasks);

    private static BoincApp AppOf(string name, string friendly) => new(name, friendly);

    private static Workunit WorkunitOf(string name, string appName) => new(name, appName, 8.64e13);

    private static HostInfo HostInfoOf(string domain, string model) =>
        new(domain, "", "", NCpus: 8, PModel: model);

    private static Message Msg(string project, MessagePriority pri, int seqno, DateTimeOffset time, string body) =>
        new(project, pri, seqno, time, body);

    // A realistic five-way cycle of task states, so density/sort/state-filter all
    // have something to bite on: running (two progress bands), waiting, suspended,
    // and uploading-ready. Every 23rd task is deadline-at-risk (tight deadline vs
    // large remaining estimate) to exercise the at-risk styling.
    private static IEnumerable<Result> Tasks(
        string projectUrl, string workunit, string hostTag, int count, DateTimeOffset now) =>
        Enumerable.Range(0, count).Select(i => Task(projectUrl, workunit, hostTag, i, now));

    private static Result Task(string projectUrl, string workunit, string hostTag, int i, DateTimeOffset now)
    {
        string name = $"{workunit}_{hostTag}_{i:0000}";
        bool atRisk = i % 23 == 0;
        DateTimeOffset deadline = atRisk
            ? now.AddHours(1)
            : now.AddHours(24 + (i % 30) * 6);
        double atRiskRemaining = atRisk ? 7_200 : 0;

        return (i % 5) switch
        {
            // Running, early. SchedulerState.Scheduled mirrors what a real daemon
            // reports alongside an EXECUTING task — TaskStatusPolicy keys the fine
            // "Running" text off scheduler_state, so the canned running rows would
            // otherwise read "Ready to start" (Codex R3 P3).
            0 => Base(ResultState.FilesDownloaded, deadline,
                     estRemaining: atRiskRemaining > 0 ? atRiskRemaining : 5_400,
                     active: new ActiveTask(1, ((i % 9) + 1) / 10.0, 1_200, 1_500, SchedulerState.Scheduled)),
            // Running, mid.
            1 => Base(ResultState.FilesDownloaded, deadline,
                     estRemaining: atRiskRemaining > 0 ? atRiskRemaining : 3_600,
                     active: new ActiveTask(1, 0.55, 3_000, 3_600, SchedulerState.Scheduled)),
            // Ready to run (downloaded, not yet scheduled).
            2 => Base(ResultState.FilesDownloaded, deadline, estRemaining: 9_000, active: null),
            // Suspended by the user.
            3 => Base(ResultState.FilesDownloaded, deadline, estRemaining: 6_000, active: null, suspended: true),
            // Finished computing, uploading / ready to report.
            _ => Base(ResultState.FilesUploading, deadline, estRemaining: 0, active: null,
                     readyToReport: true, finalElapsed: 7_500, finalCpu: 7_200),
        };

        Result Base(
            ResultState resultState, DateTimeOffset reportDeadline, double estRemaining, ActiveTask? active,
            bool suspended = false, bool readyToReport = false, double finalElapsed = 0, double finalCpu = 0) =>
            new(name, workunit, projectUrl, resultState, reportDeadline, readyToReport, suspended,
                finalCpu, finalElapsed, estRemaining, VersionNum: 1, PlanClass: "", ExitStatus: 0, active);
    }

    private enum TransferKind { Active, Retrying, Queued }

    private static FileTransfer Xfer(
        string name, string projectUrl, string projectName, TransferKind kind, bool up,
        DateTimeOffset now, double fraction, double speed = 0, int retries = 0)
    {
        const double nbytes = 96.0 * 1024 * 1024;
        double xferred = nbytes * fraction;
        bool active = kind == TransferKind.Active;
        // Retrying is classified by SnapshotBuilder as "NextRequestTime in the
        // future"; 30 min keeps it in the Retrying tier for a whole walkthrough.
        DateTimeOffset? nextRequest = kind == TransferKind.Retrying ? now.AddMinutes(30) : null;
        return new FileTransfer(
            name, projectUrl, projectName,
            Nbytes: nbytes, Status: 0, IsUpload: up, NumRetries: retries,
            FirstRequestTime: now.AddMinutes(-15), NextRequestTime: nextRequest,
            TimeSoFar: 120, BytesXferred: xferred, FileOffset: xferred,
            XferSpeed: active ? speed : 0, ProjectBackoff: 0,
            PersXferActive: kind != TransferKind.Queued, XferActive: active);
    }
}

/// <summary>
/// Serves one <see cref="SampleHostData"/>'s canned RPC replies. State and status stay fixed;
/// results and transfers are held mutably only to support the opt-in live-progress aid
/// (<see cref="SampleHost.Ticking"/>) — with it off, every poll returns the canned lists
/// unchanged (identity), exactly as before.
/// </summary>
internal sealed class SampleGuiRpcClient(SampleHostData data) : IGuiRpcClient
{
    private IReadOnlyList<Result> _results = data.Results;
    private IReadOnlyList<FileTransfer> _transfers = data.Transfers;

    public Task ConnectAsync(string host, int port = 31416, CancellationToken ct = default) => Task.CompletedTask;

    public Task<bool> AuthorizeAsync(string password, CancellationToken ct = default) => Task.FromResult(true);

    public Task<VersionInfo> ExchangeVersionsAsync(CancellationToken ct = default) =>
        Task.FromResult(data.State.CoreClientVersion);

    public Task<CcState> GetStateAsync(CancellationToken ct = default) => Task.FromResult(data.State);

    public Task<CcStatus> GetCcStatusAsync(CancellationToken ct = default) => Task.FromResult(data.Status);

    public Task<IReadOnlyList<Result>> GetResultsAsync(bool activeOnly = false, CancellationToken ct = default)
    {
        if (SampleHost.Ticking)
            _results = [.. _results.Select(SampleHost.AdvanceResult)];
        return Task.FromResult(_results);
    }

    // BOINC's get_messages contract: only seqno-greater messages. First poll
    // (seqno 0) returns the batch; subsequent polls return nothing new.
    public Task<IReadOnlyList<Message>> GetMessagesAsync(int seqno = 0, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Message>>([.. data.Messages.Where(m => m.Seqno > seqno)]);

    public Task<IReadOnlyList<FileTransfer>> GetFileTransfersAsync(CancellationToken ct = default)
    {
        if (SampleHost.Ticking)
            _transfers = [.. _transfers.Select(SampleHost.AdvanceTransfer)];
        return Task.FromResult(_transfers);
    }

    // Control ops on the sample host are accepted and ignored: the canned data
    // never changes, mirroring the read-only nature of the demo fixture.
    public Task PerformTaskOpAsync(TaskOp op, string projectUrl, string taskName, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task PerformProjectOpAsync(ProjectOp op, string projectUrl, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task SetModeAsync(ModeLane lane, RunMode mode, TimeSpan duration, CancellationToken ct = default) =>
        Task.CompletedTask;

    // Attach-flow RPCs are benign no-ops on sample hosts: requests succeed and
    // polls report success without changing the canned data.
    public Task RequestAccountLookupAsync(string projectUrl, string email, string password, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<AccountLookupReply> PollAccountLookupAsync(CancellationToken ct = default) =>
        Task.FromResult(new AccountLookupReply(0, string.Empty, string.Empty));

    public Task RequestProjectAttachAsync(string projectUrl, string authenticator, string projectName, string emailAddr, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<ProjectAttachReply> PollProjectAttachAsync(CancellationToken ct = default) =>
        Task.FromResult(new ProjectAttachReply(0, []));

    public Task<IReadOnlyList<Project>> GetProjectStatusAsync(CancellationToken ct = default) =>
        Task.FromResult(data.State.Projects);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Routes a host-agnostic <see cref="IGuiRpcClient"/> factory by the address
/// carried on <see cref="ConnectAsync"/> — sample addresses get canned data, any
/// real address falls through to the live client. Mirrors the test suite's
/// RoutingGuiRpcClient so the injection shape is identical to what the headless
/// fixtures exercise.
/// </summary>
internal sealed class SampleRoutingGuiRpcClient(
    Func<IGuiRpcClient> realFactory, IReadOnlyDictionary<string, SampleHostData> byAddress) : IGuiRpcClient
{
    private IGuiRpcClient? _target;

    public Task ConnectAsync(string host, int port = 31416, CancellationToken ct = default)
    {
        _target = byAddress.TryGetValue(host, out SampleHostData? data)
            ? new SampleGuiRpcClient(data)
            : realFactory();
        return _target.ConnectAsync(host, port, ct);
    }

    public Task<bool> AuthorizeAsync(string password, CancellationToken ct = default) =>
        _target!.AuthorizeAsync(password, ct);

    public Task<VersionInfo> ExchangeVersionsAsync(CancellationToken ct = default) =>
        _target!.ExchangeVersionsAsync(ct);

    public Task<CcState> GetStateAsync(CancellationToken ct = default) => _target!.GetStateAsync(ct);

    public Task<CcStatus> GetCcStatusAsync(CancellationToken ct = default) => _target!.GetCcStatusAsync(ct);

    public Task<IReadOnlyList<Result>> GetResultsAsync(bool activeOnly = false, CancellationToken ct = default) =>
        _target!.GetResultsAsync(activeOnly, ct);

    public Task<IReadOnlyList<Message>> GetMessagesAsync(int seqno = 0, CancellationToken ct = default) =>
        _target!.GetMessagesAsync(seqno, ct);

    public Task<IReadOnlyList<FileTransfer>> GetFileTransfersAsync(CancellationToken ct = default) =>
        _target!.GetFileTransfersAsync(ct);

    public Task PerformTaskOpAsync(TaskOp op, string projectUrl, string taskName, CancellationToken ct = default) =>
        _target!.PerformTaskOpAsync(op, projectUrl, taskName, ct);

    public Task PerformProjectOpAsync(ProjectOp op, string projectUrl, CancellationToken ct = default) =>
        _target!.PerformProjectOpAsync(op, projectUrl, ct);

    public Task SetModeAsync(ModeLane lane, RunMode mode, TimeSpan duration, CancellationToken ct = default) =>
        _target!.SetModeAsync(lane, mode, duration, ct);

    public Task RequestAccountLookupAsync(string projectUrl, string email, string password, CancellationToken ct = default) =>
        _target!.RequestAccountLookupAsync(projectUrl, email, password, ct);

    public Task<AccountLookupReply> PollAccountLookupAsync(CancellationToken ct = default) =>
        _target!.PollAccountLookupAsync(ct);

    public Task RequestProjectAttachAsync(string projectUrl, string authenticator, string projectName, string emailAddr, CancellationToken ct = default) =>
        _target!.RequestProjectAttachAsync(projectUrl, authenticator, projectName, emailAddr, ct);

    public Task<ProjectAttachReply> PollProjectAttachAsync(CancellationToken ct = default) =>
        _target!.PollProjectAttachAsync(ct);

    public Task<IReadOnlyList<Project>> GetProjectStatusAsync(CancellationToken ct = default) =>
        _target!.GetProjectStatusAsync(ct);

    public ValueTask DisposeAsync() => _target?.DisposeAsync() ?? ValueTask.CompletedTask;
}
#endif
