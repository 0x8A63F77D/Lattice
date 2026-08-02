using System.Text;
using Lattice.Boinc.GuiRpc;
using Xunit;

namespace Lattice.Tests;

/// <summary>
/// Adversarial fixtures for the accumulate-until-0x03 framing: what arrives when the
/// network, not the daemon, is the hostile party. The terminator contract is
/// client/gui_rpc_server_ops.cpp, which appends "\003" to every non-HTTP reply.
/// </summary>
public class GuiRpcFramingAdversarialTests
{
    [Fact]
    public async Task Requests_are_encoded_as_utf8()
    {
        // The wire is byte-transparent and BOINC labels its own GUI RPC payloads
        // charset=utf-8. Encoding the request as ASCII folded every non-ASCII byte of a
        // caller-supplied project name to '?', silently attaching under a mangled name.
        var stream = ScriptedStream.FromReplies("<boinc_gui_rpc_reply>\n<success/>\n</boinc_gui_rpc_reply>");
        await using var client = new BoincGuiRpcClient(new RpcConnection(stream));

        await client.RequestProjectAttachAsync(
            "https://example.org/", "3f8a9b", "Cosmología@Home", "user@example.org");

        string sent = Encoding.UTF8.GetString(stream.Written.ToArray());
        Assert.Contains("<project_name>Cosmología@Home</project_name>", sent);
    }

    [Fact]
    public async Task Bytes_after_the_terminator_are_discarded()
    {
        // Strictly request-reply with no pipelining, so nothing valid can follow the
        // terminator; junk in the same TCP segment must not leak into the reply.
        byte[] chunk = [
            .. Encoding.UTF8.GetBytes("<boinc_gui_rpc_reply>\n<a/>\n</boinc_gui_rpc_reply>"),
            0x03,
            .. Encoding.UTF8.GetBytes("<garbage>trailing</garbage>\x03"),
        ];
        await using var conn = new RpcConnection(new ScriptedStream(chunk));

        string reply = await conn.PerformRpcAsync("<x/>", CancellationToken.None);

        Assert.Equal("<boinc_gui_rpc_reply>\n<a/>\n</boinc_gui_rpc_reply>", reply);
    }

    [Fact]
    public async Task Zero_length_message_yields_an_empty_frame_then_a_protocol_error()
    {
        // A lone terminator: framing succeeds (it is a complete, empty frame) and the
        // failure surfaces one layer up as an unparseable reply, carrying the payload.
        await using var conn = new RpcConnection(new ScriptedStream([0x03]));

        string raw = await conn.PerformRpcAsync("<x/>", CancellationToken.None);

        Assert.Equal(string.Empty, raw);
        var ex = Assert.Throws<BoincProtocolException>(() => RpcReplyParser.Parse(raw));
        Assert.Equal(string.Empty, ex.RawPayload);
    }

    [Fact]
    public async Task Terminator_as_the_first_byte_of_a_later_chunk_closes_the_frame()
    {
        var stream = new ScriptedStream(
            Encoding.UTF8.GetBytes("<boinc_gui_rpc_reply><a/></boinc_gui_rpc_reply>"),
            [0x03]);
        await using var conn = new RpcConnection(stream);

        string reply = await conn.PerformRpcAsync("<x/>", CancellationToken.None);

        Assert.Equal("<boinc_gui_rpc_reply><a/></boinc_gui_rpc_reply>", reply);
    }

    [Fact]
    public async Task Multibyte_character_split_across_chunks_decodes_intact()
    {
        // Decoding must happen once over the assembled frame, never per chunk: a UTF-8
        // sequence straddling a TCP segment boundary would otherwise become two
        // replacement characters. Project names carry non-ASCII in practice.
        byte[] whole = Encoding.UTF8.GetBytes(
            "<boinc_gui_rpc_reply><projects><project><project_name>Cosmología</project_name>" +
            "</project></projects></boinc_gui_rpc_reply>\x03");
        int split = Array.IndexOf(whole, (byte)0xC3) + 1; // mid "í"
        var stream = new ScriptedStream(whole[..split], whole[split..]);
        await using var client = new BoincGuiRpcClient(new RpcConnection(stream));

        Project project = Assert.Single(await client.GetProjectStatusAsync());

        Assert.Equal("Cosmología", project.ProjectName);
    }

    [Fact]
    public async Task Multi_megabyte_reply_is_accumulated_intact()
    {
        // get_state is several MB on a busy host and arrives in many reads.
        const int resultCount = 20_000;
        var sb = new StringBuilder("<boinc_gui_rpc_reply>\n<results>\n");
        for (int i = 0; i < resultCount; i++)
            sb.Append($"<result>\n<name>task-{i}</name>\n<state>2</state>\n</result>\n");
        sb.Append("</results>\n</boinc_gui_rpc_reply>");
        byte[] whole = [.. Encoding.UTF8.GetBytes(sb.ToString()), 0x03];
        Assert.True(whole.Length > 1_000_000, "fixture must exceed one megabyte");

        var chunks = new List<byte[]>();
        for (int i = 0; i < whole.Length; i += 4096)
            chunks.Add(whole[i..Math.Min(i + 4096, whole.Length)]);
        await using var client = new BoincGuiRpcClient(new RpcConnection(new ScriptedStream([.. chunks])));

        IReadOnlyList<Result> results = await client.GetResultsAsync();

        Assert.Equal(resultCount, results.Count);
        Assert.Equal("task-0", results[0].Name);
        Assert.Equal($"task-{resultCount - 1}", results[^1].Name);
    }

    [Fact]
    public async Task Connection_closed_before_any_byte_arrives_throws_connection_exception()
    {
        await using var conn = new RpcConnection(new ScriptedStream());

        await Assert.ThrowsAsync<BoincConnectionException>(
            () => conn.PerformRpcAsync("<get_state/>", CancellationToken.None));
    }

    [Fact]
    public async Task Connection_closed_midway_through_a_frame_throws_connection_exception()
    {
        // A dropped connection must never be mistaken for a short reply: half a
        // get_state would otherwise parse as "this host has no tasks".
        var stream = new ScriptedStream(
            Encoding.UTF8.GetBytes("<boinc_gui_rpc_reply>\n<results>\n<result><name>t1</name></result>"));
        await using var client = new BoincGuiRpcClient(new RpcConnection(stream));

        await Assert.ThrowsAsync<BoincConnectionException>(() => client.GetResultsAsync());
    }

    [Fact]
    public async Task Socket_failure_mid_read_surfaces_as_connection_exception()
    {
        await using var conn = new RpcConnection(new FailingStream());

        var ex = await Assert.ThrowsAsync<BoincConnectionException>(
            () => conn.PerformRpcAsync("<get_state/>", CancellationToken.None));

        Assert.IsType<IOException>(ex.InnerException);
    }

    [Fact]
    public async Task Cancellation_mid_read_propagates_rather_than_being_wrapped()
    {
        // A cancelled NetworkStream read throws OperationCanceledException, which the
        // "wrap IO failures" filter must let past: callers cancel polls on shutdown, and
        // that has to stay distinguishable from a genuine connection failure or the host
        // gets marked unreachable on every clean shutdown.
        await using var conn = new RpcConnection(new FailingStream(new OperationCanceledException()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => conn.PerformRpcAsync("<get_state/>", CancellationToken.None));
    }

    private sealed class FailingStream(Exception failure) : Stream
    {
        public FailingStream() : this(new IOException("socket reset")) { }

        public override int Read(byte[] buffer, int offset, int count) => throw failure;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            throw failure;
        public override void Write(byte[] buffer, int offset, int count) { }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
            ValueTask.CompletedTask;
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
