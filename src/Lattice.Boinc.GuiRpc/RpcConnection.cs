using System.Net.Sockets;
using System.Text;

namespace Lattice.Boinc.GuiRpc;

/// <summary>Owns the socket and the \x03 framing. Knows no RPC semantics.</summary>
internal sealed class RpcConnection : IAsyncDisposable
{
    private const byte Terminator = 0x03;

    private static readonly Encoding LenientUtf8 = Encoding.GetEncoding(
        "utf-8", EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);

    private readonly Stream _stream;
    private readonly TcpClient? _tcpClient;

    internal RpcConnection(Stream stream) => _stream = stream;

    private RpcConnection(TcpClient tcpClient) : this(tcpClient.GetStream()) => _tcpClient = tcpClient;

    internal static async Task<RpcConnection> ConnectAsync(string host, int port, CancellationToken ct)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            client.Dispose();
            throw new BoincConnectionException($"Failed to connect to {host}:{port}.", ex);
        }
        return new RpcConnection(client);
    }

    internal async Task<string> PerformRpcAsync(string requestBody, CancellationToken ct)
    {
        string request = "<boinc_gui_rpc_request>\n" + requestBody + "\n</boinc_gui_rpc_request>\n\x03";
        byte[] sendBuffer = Encoding.ASCII.GetBytes(request);
        try
        {
            await _stream.WriteAsync(sendBuffer, ct).ConfigureAwait(false);

            using var reply = new MemoryStream();
            byte[] buffer = new byte[8192];
            while (true)
            {
                int read = await _stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read == 0)
                    throw new BoincConnectionException("Connection closed before the reply terminator arrived.");

                int terminator = Array.IndexOf(buffer, Terminator, 0, read);
                if (terminator >= 0)
                {
                    // Bytes past the terminator are discarded: the protocol is strictly
                    // request-reply with no pipelining, so nothing valid can follow it.
                    reply.Write(buffer, 0, terminator);
                    break;
                }
                reply.Write(buffer, 0, read);
            }
            return LenientUtf8.GetString(reply.ToArray());
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            throw new BoincConnectionException("Connection failed during RPC.", ex);
        }
    }

    public ValueTask DisposeAsync()
    {
        _tcpClient?.Dispose();
        return _stream.DisposeAsync();
    }
}
