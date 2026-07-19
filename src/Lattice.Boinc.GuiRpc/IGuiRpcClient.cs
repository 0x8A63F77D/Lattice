namespace Lattice.Boinc.GuiRpc;

/// <summary>
/// The GUI RPC surface of one BOINC core client connection.
/// Extracted so higher layers can substitute a fake for testing;
/// <see cref="BoincGuiRpcClient"/> is the production implementation.
/// </summary>
public interface IGuiRpcClient : IAsyncDisposable
{
    /// <summary>Connects to a BOINC core client.</summary>
    Task ConnectAsync(string host, int port = 31416, CancellationToken ct = default);

    /// <summary>Authenticates with the daemon using a password. Returns true on success.</summary>
    Task<bool> AuthorizeAsync(string password, CancellationToken ct = default);

    /// <summary>Exchanges version information with the daemon.</summary>
    Task<VersionInfo> ExchangeVersionsAsync(CancellationToken ct = default);

    /// <summary>Full state snapshot. Several MB on busy hosts — call once per connection, then poll deltas.</summary>
    Task<CcState> GetStateAsync(CancellationToken ct = default);

    /// <summary>Returns the core client status: task mode, network status, suspend reasons.</summary>
    Task<CcStatus> GetCcStatusAsync(CancellationToken ct = default);

    /// <summary>Returns the list of results (tasks) on the core client.</summary>
    Task<IReadOnlyList<Result>> GetResultsAsync(bool activeOnly = false, CancellationToken ct = default);

    /// <summary>Returns messages with seqno greater than the given value. Seqno is monotonic.</summary>
    Task<IReadOnlyList<Message>> GetMessagesAsync(int seqno = 0, CancellationToken ct = default);

    /// <summary>Returns in-progress file uploads and downloads.</summary>
    Task<IReadOnlyList<FileTransfer>> GetFileTransfersAsync(CancellationToken ct = default);

    /// <summary>Suspends, resumes, or aborts one task. Requires authorization.</summary>
    Task PerformTaskOpAsync(TaskOp op, string projectUrl, string taskName, CancellationToken ct = default);

    /// <summary>Suspends, resumes, updates, or detaches one project. Requires authorization.</summary>
    Task PerformProjectOpAsync(ProjectOp op, string projectUrl, CancellationToken ct = default);

    /// <summary>
    /// Sets a run-mode lane. Zero duration makes the mode permanent; a positive duration is a
    /// temporary override (snooze = <see cref="RunMode.Never"/> with a duration) that the daemon
    /// reverts on its own. <see cref="RunMode.Restore"/> cancels a temporary override immediately.
    /// Requires authorization.
    /// </summary>
    Task SetModeAsync(ModeLane lane, RunMode mode, TimeSpan duration, CancellationToken ct = default);
}
