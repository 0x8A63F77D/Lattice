using Lattice.Boinc.GuiRpc;
using Xunit;

namespace Lattice.Tests;

public class RpcReplyParserTests
{
    [Fact]
    public void Returns_reply_element_for_valid_reply()
    {
        var reply = RpcReplyParser.Parse("<boinc_gui_rpc_reply>\n<nonce>123</nonce>\n</boinc_gui_rpc_reply>");
        Assert.Equal("123", ParseHelpers.GetString(reply, "nonce"));
    }

    [Fact]
    public void Malformed_reply_throws_protocol_exception_with_payload()
    {
        var ex = Assert.Throws<BoincProtocolException>(() => RpcReplyParser.Parse("<boinc_gui_rpc_reply><broken"));
        Assert.Contains("<broken", ex.RawPayload);
    }

    [Fact]
    public void Unauthorized_throws_by_default()
    {
        Assert.Throws<BoincUnauthorizedException>(
            () => RpcReplyParser.Parse("<boinc_gui_rpc_reply>\n<unauthorized/>\n</boinc_gui_rpc_reply>"));
    }

    [Fact]
    public void Unauthorized_suppressed_for_auth_flow()
    {
        var reply = RpcReplyParser.Parse(
            "<boinc_gui_rpc_reply>\n<unauthorized/>\n</boinc_gui_rpc_reply>", throwOnUnauthorized: false);
        Assert.NotNull(reply.Element("unauthorized"));
    }

    [Fact]
    public void Error_tag_throws_rpc_exception_with_text()
    {
        var ex = Assert.Throws<BoincRpcException>(
            () => RpcReplyParser.Parse("<boinc_gui_rpc_reply>\n<error>unrecognized op</error>\n</boinc_gui_rpc_reply>"));
        Assert.Equal("unrecognized op", ex.ErrorText);
    }

    [Fact]
    public void Error_kept_as_data_when_error_throw_suppressed()
    {
        var reply = RpcReplyParser.Parse(
            "<boinc_gui_rpc_reply>\n<error>lookup failed</error>\n</boinc_gui_rpc_reply>", throwOnError: false);
        Assert.Equal("lookup failed", ParseHelpers.GetString(reply, "error"));
    }

    [Fact]
    public void Unauthorized_still_throws_when_error_throw_suppressed()
    {
        Assert.Throws<BoincUnauthorizedException>(() => RpcReplyParser.Parse(
            "<boinc_gui_rpc_reply>\n<unauthorized/>\n</boinc_gui_rpc_reply>", throwOnError: false));
    }

    [Fact]
    public void Reply_with_unescaped_ampersand_still_parses()
    {
        var reply = RpcReplyParser.Parse("<boinc_gui_rpc_reply>\n<name>Miles & More</name>\n</boinc_gui_rpc_reply>");
        Assert.Equal("Miles & More", ParseHelpers.GetString(reply, "name"));
    }
}
