using System.Text;
using Lattice.Boinc.GuiRpc;
using Xunit;

namespace Lattice.Tests;

public class RpcConnectionTests
{
    [Fact]
    public async Task Wraps_request_in_envelope_with_terminator()
    {
        var stream = ScriptedStream.FromReplies("<boinc_gui_rpc_reply>\n<a/>\n</boinc_gui_rpc_reply>");
        await using var conn = new RpcConnection(stream);

        await conn.PerformRpcAsync("<auth1/>", CancellationToken.None);

        string sent = Encoding.ASCII.GetString(stream.Written.ToArray());
        Assert.Equal("<boinc_gui_rpc_request>\n<auth1/>\n</boinc_gui_rpc_request>\n\x03", sent);
    }

    [Fact]
    public async Task Returns_reply_without_terminator()
    {
        var stream = ScriptedStream.FromReplies("<boinc_gui_rpc_reply>\n<a/>\n</boinc_gui_rpc_reply>");
        await using var conn = new RpcConnection(stream);

        string reply = await conn.PerformRpcAsync("<auth1/>", CancellationToken.None);

        Assert.Equal("<boinc_gui_rpc_reply>\n<a/>\n</boinc_gui_rpc_reply>", reply);
        Assert.DoesNotContain('\x03', reply);
    }

    [Fact]
    public async Task Accumulates_fragmented_reply_until_terminator()
    {
        byte[] whole = Encoding.UTF8.GetBytes("<boinc_gui_rpc_reply><big>payload</big></boinc_gui_rpc_reply>\x03");
        var stream = new ScriptedStream(whole[..10], whole[10..25], whole[25..]);
        await using var conn = new RpcConnection(stream);

        string reply = await conn.PerformRpcAsync("<get_state/>", CancellationToken.None);

        Assert.Equal("<boinc_gui_rpc_reply><big>payload</big></boinc_gui_rpc_reply>", reply);
    }

    [Fact]
    public async Task Stream_ending_before_terminator_throws_connection_exception()
    {
        byte[] truncated = Encoding.UTF8.GetBytes("<boinc_gui_rpc_reply><never_terminated>");
        var stream = new ScriptedStream(truncated);
        await using var conn = new RpcConnection(stream);

        await Assert.ThrowsAsync<BoincConnectionException>(
            () => conn.PerformRpcAsync("<get_state/>", CancellationToken.None));
    }

    [Fact]
    public async Task Invalid_utf8_bytes_do_not_throw()
    {
        byte[] reply = [.. Encoding.UTF8.GetBytes("<a>"), 0xFF, 0xFE, .. Encoding.UTF8.GetBytes("</a>"), 0x03];
        var stream = new ScriptedStream(reply);
        await using var conn = new RpcConnection(stream);

        string text = await conn.PerformRpcAsync("<x/>", CancellationToken.None);

        Assert.StartsWith("<a>", text);
        Assert.EndsWith("</a>", text);
    }
}
