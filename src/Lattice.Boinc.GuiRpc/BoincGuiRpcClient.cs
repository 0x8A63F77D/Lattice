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
/// </summary>
public sealed class BoincGuiRpcClient : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private RpcConnection? connection;

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
        this.connection = connection;
        State = ConnectionState.Connected;
    }

    /// <summary>
    /// Connects to a BOINC core client.
    /// </summary>
    public async Task ConnectAsync(string host, int port = 31416, CancellationToken ct = default)
    {
        if (connection is not null)
            throw new InvalidOperationException("Already connected. Create a new client to reconnect.");
        connection = await RpcConnection.ConnectAsync(host, port, ct).ConfigureAwait(false);
        State = ConnectionState.Connected;
    }

    /// <summary>
    /// Authenticates with the daemon using a password. Returns true on success.
    /// </summary>
    public async Task<bool> AuthorizeAsync(string password, CancellationToken ct = default)
    {
        XElement reply1 = await PerformRpcAsync("<auth1/>", throwOnUnauthorized: true, ct).ConfigureAwait(false);
        string nonce = ParseHelpers.GetString(reply1, "nonce");

        string hash = Convert.ToHexStringLower(MD5.HashData(Encoding.ASCII.GetBytes(nonce + password)));
        XElement reply2 = await PerformRpcAsync(
            $"<auth2>\n<nonce_hash>{hash}</nonce_hash>\n</auth2>", throwOnUnauthorized: false, ct).ConfigureAwait(false);

        bool authorized = reply2.Element("authorized") is not null;
        if (authorized)
            State = ConnectionState.Authorized;
        return authorized;
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

    private async Task<XElement> PerformRpcAsync(string body, bool throwOnUnauthorized, CancellationToken ct)
    {
        if (connection is null)
            throw new InvalidOperationException("Not connected. Call ConnectAsync first.");

        await gate.WaitAsync(ct).ConfigureAwait(false);
        string raw;
        try
        {
            raw = await connection.PerformRpcAsync(body, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
        return RpcReplyParser.Parse(raw, throwOnUnauthorized);
    }

    /// <summary>
    /// Closes the connection and releases resources.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (connection is not null)
            await connection.DisposeAsync().ConfigureAwait(false);
        State = ConnectionState.Disconnected;
        gate.Dispose();
    }
}
