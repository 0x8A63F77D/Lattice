using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Lattice.Boinc.GuiRpc;

/// <summary>
/// Client for one BOINC core client over one GUI RPC connection.
/// All RPCs are serialized: the protocol is strictly request-reply, so
/// concurrent callers queue on an internal semaphore.
/// No reconnect/retry policy — when the connection dies, callers see
/// BoincConnectionException and must create a new client.
/// Disposing while an RPC is in flight is a caller error; the owner is
/// expected to stop issuing RPCs before disposing.
/// </summary>
public sealed class BoincGuiRpcClient : IGuiRpcClient
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private RpcConnection? _connection;

    /// <summary>
    /// The current connection state.
    /// </summary>
    public ConnectionState State { get; private set; }

    /// <summary>
    /// The daemon version, set after ExchangeVersionsAsync succeeds.
    /// </summary>
    public VersionInfo? DaemonVersion { get; private set; }

    /// <summary>
    /// Creates a new unconnected client.
    /// </summary>
    public BoincGuiRpcClient() { }

    internal BoincGuiRpcClient(RpcConnection connection)
    {
        _connection = connection;
        State = ConnectionState.Connected;
    }

    /// <summary>
    /// Connects to a BOINC core client.
    /// </summary>
    public async Task ConnectAsync(string host, int port = 31416, CancellationToken ct = default)
    {
        if (_connection is not null)
            throw new InvalidOperationException("Already connected. Create a new client to reconnect.");
        _connection = await RpcConnection.ConnectAsync(host, port, ct).ConfigureAwait(false);
        State = ConnectionState.Connected;
    }

    /// <summary>
    /// Authenticates with the daemon using a password. Returns true on success.
    /// </summary>
    public async Task<bool> AuthorizeAsync(string password, CancellationToken ct = default)
    {
        // The daemon keeps one pending nonce per connection, so the gate must span
        // both RPCs: an interleaved auth1 from another caller would invalidate this nonce.
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            XElement reply1 = await PerformRpcLockedAsync("<auth1/>", throwOnUnauthorized: true, ct).ConfigureAwait(false);
            string nonce = ParseHelpers.GetString(reply1, "nonce");

            // UTF-8 matches the raw bytes the C++ client hashes; ASCII would corrupt non-ASCII passwords.
            string hash = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(nonce + password)));
            XElement reply2 = await PerformRpcLockedAsync(
                $"<auth2>\n<nonce_hash>{hash}</nonce_hash>\n</auth2>", throwOnUnauthorized: false, ct).ConfigureAwait(false);

            bool authorized = reply2.Element("authorized") is not null;
            if (authorized)
                State = ConnectionState.Authorized;
            return authorized;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Exchanges version information with the daemon and stores it.
    /// </summary>
    public async Task<VersionInfo> ExchangeVersionsAsync(CancellationToken ct = default)
    {
        XElement reply = await PerformRpcAsync("<exchange_versions/>", throwOnUnauthorized: true, ct).ConfigureAwait(false);
        XElement versionElement = reply.Element("server_version") ?? reply;
        VersionInfo version = VersionInfo.Parse(versionElement);
        DaemonVersion = version;
        return version;
    }

    /// <summary>Full state snapshot. Several MB on busy hosts — call once per connection, then poll deltas.</summary>
    public async Task<CcState> GetStateAsync(CancellationToken ct = default)
    {
        XElement reply = await PerformRpcAsync("<get_state/>", throwOnUnauthorized: true, ct).ConfigureAwait(false);
        if (reply.Element("client_state") is not { } clientState)
            throw new BoincProtocolException("get_state reply is missing <client_state>.", reply.ToString());
        return CcState.Parse(clientState);
    }

    /// <summary>Returns the core client status: task mode, network status, suspend reasons.</summary>
    public async Task<CcStatus> GetCcStatusAsync(CancellationToken ct = default)
    {
        XElement reply = await PerformRpcAsync("<get_cc_status/>", throwOnUnauthorized: true, ct).ConfigureAwait(false);
        return CcStatus.Parse(reply.Element("cc_status") ?? reply);
    }

    /// <summary>Returns the list of results (tasks) on the core client.</summary>
    public async Task<IReadOnlyList<Result>> GetResultsAsync(bool activeOnly = false, CancellationToken ct = default)
    {
        string body = $"<get_results>\n<active_only>{(activeOnly ? 1 : 0)}</active_only>\n</get_results>";
        XElement reply = await PerformRpcAsync(body, throwOnUnauthorized: true, ct).ConfigureAwait(false);
        XElement container = reply.Element("results") ?? reply;
        return [.. container.Elements("result").Select(Result.Parse)];
    }

    /// <summary>Returns messages with seqno greater than the given value. Seqno is monotonic.</summary>
    public async Task<IReadOnlyList<Message>> GetMessagesAsync(int seqno = 0, CancellationToken ct = default)
    {
        string body = $"<get_messages>\n<seqno>{seqno}</seqno>\n</get_messages>";
        XElement reply = await PerformRpcAsync(body, throwOnUnauthorized: true, ct).ConfigureAwait(false);
        XElement container = reply.Element("msgs") ?? reply;
        return [.. container.Elements("msg").Select(Message.Parse)];
    }

    /// <summary>Returns in-progress file uploads and downloads.</summary>
    public async Task<IReadOnlyList<FileTransfer>> GetFileTransfersAsync(CancellationToken ct = default)
    {
        XElement reply = await PerformRpcAsync("<get_file_transfers/>", throwOnUnauthorized: true, ct).ConfigureAwait(false);
        XElement container = reply.Element("file_transfers") ?? reply;
        return [.. container.Elements("file_transfer").Select(FileTransfer.Parse)];
    }

    private async Task<XElement> PerformRpcAsync(string body, bool throwOnUnauthorized, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await PerformRpcLockedAsync(body, throwOnUnauthorized, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Callers must hold _gate.
    private async Task<XElement> PerformRpcLockedAsync(string body, bool throwOnUnauthorized, CancellationToken ct)
    {
        if (_connection is null)
            throw new InvalidOperationException("Not connected. Call ConnectAsync first.");

        string raw = await _connection.PerformRpcAsync(body, ct).ConfigureAwait(false);
        return RpcReplyParser.Parse(raw, throwOnUnauthorized);
    }

    /// <summary>
    /// Closes the connection and releases resources.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
            await _connection.DisposeAsync().ConfigureAwait(false);
        State = ConnectionState.Disconnected;
        _gate.Dispose();
    }
}
