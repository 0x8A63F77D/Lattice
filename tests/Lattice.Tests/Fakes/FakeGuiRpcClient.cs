using Lattice.Boinc.GuiRpc;

namespace Lattice.Tests;

/// <summary>
/// Scriptable IGuiRpcClient: each RPC delegates to a settable func; every call is
/// recorded in a thread-safe log. Passwords are deliberately NOT recorded.
/// </summary>
public sealed class FakeGuiRpcClient : IGuiRpcClient
{
    private readonly object _gate = new();
    private readonly List<string> _calls = [];

    public IReadOnlyList<string> Calls { get { lock (_gate) return [.. _calls]; } }
    public bool Disposed { get; private set; }

    public static CcState EmptyState { get; } = new(new VersionInfo(8, 2, 0), null, [], [], [], [], []);
    public static CcStatus DefaultStatus { get; } = new(
        RunMode.Auto, RunMode.Auto, RunMode.Auto,
        SuspendReason.NotSuspended, SuspendReason.NotSuspended, SuspendReason.NotSuspended);

    public Func<string, int, Task> OnConnect { get; set; } = (_, _) => Task.CompletedTask;
    public Func<string, Task<bool>> OnAuthorize { get; set; } = _ => Task.FromResult(true);
    public Func<Task<VersionInfo>> OnExchangeVersions { get; set; } = () => Task.FromResult(new VersionInfo(8, 2, 0));
    public Func<Task<CcState>> OnGetState { get; set; } = () => Task.FromResult(EmptyState);
    public Func<Task<CcStatus>> OnGetCcStatus { get; set; } = () => Task.FromResult(DefaultStatus);
    public Func<bool, Task<IReadOnlyList<Result>>> OnGetResults { get; set; } = _ => Task.FromResult<IReadOnlyList<Result>>([]);
    public Func<int, Task<IReadOnlyList<Message>>> OnGetMessages { get; set; } = _ => Task.FromResult<IReadOnlyList<Message>>([]);
    public Func<Task<IReadOnlyList<FileTransfer>>> OnGetFileTransfers { get; set; } = () => Task.FromResult<IReadOnlyList<FileTransfer>>([]);

    private void Record(string call) { lock (_gate) _calls.Add(call); }

    public Task ConnectAsync(string host, int port = 31416, CancellationToken ct = default)
    { Record($"connect:{host}:{port}"); return OnConnect(host, port); }

    public Task<bool> AuthorizeAsync(string password, CancellationToken ct = default)
    { Record("authorize"); return OnAuthorize(password); }

    public Task<VersionInfo> ExchangeVersionsAsync(CancellationToken ct = default)
    { Record("exchange_versions"); return OnExchangeVersions(); }

    public Task<CcState> GetStateAsync(CancellationToken ct = default)
    { Record("get_state"); return OnGetState(); }

    public Task<CcStatus> GetCcStatusAsync(CancellationToken ct = default)
    { Record("get_cc_status"); return OnGetCcStatus(); }

    public Task<IReadOnlyList<Result>> GetResultsAsync(bool activeOnly = false, CancellationToken ct = default)
    { Record("get_results"); return OnGetResults(activeOnly); }

    public Task<IReadOnlyList<Message>> GetMessagesAsync(int seqno = 0, CancellationToken ct = default)
    { Record($"get_messages:{seqno}"); return OnGetMessages(seqno); }

    public Task<IReadOnlyList<FileTransfer>> GetFileTransfersAsync(CancellationToken ct = default)
    { Record("get_file_transfers"); return OnGetFileTransfers(); }

    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
}
