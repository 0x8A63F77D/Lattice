using System.Text;
using Lattice.Boinc.GuiRpc;
using Xunit;

namespace Lattice.Tests;

public class BoincGuiRpcClientAuthTests
{
    private static BoincGuiRpcClient ClientWith(ScriptedStream stream) =>
        new(new RpcConnection(stream));

    [Fact]
    public async Task Authorize_sends_md5_of_nonce_plus_password()
    {
        var stream = ScriptedStream.FromReplies(
            "<boinc_gui_rpc_reply>\n<nonce>1751600000.114370</nonce>\n</boinc_gui_rpc_reply>",
            "<boinc_gui_rpc_reply>\n<authorized/>\n</boinc_gui_rpc_reply>");
        await using var client = ClientWith(stream);

        bool ok = await client.AuthorizeAsync("s3cret");

        Assert.True(ok);
        Assert.Equal(ConnectionState.Authorized, client.State);
        string sent = Encoding.ASCII.GetString(stream.Written.ToArray());
        // MD5("1751600000.114370s3cret") lowercase hex
        string expectedHash = Convert.ToHexStringLower(
            System.Security.Cryptography.MD5.HashData(Encoding.ASCII.GetBytes("1751600000.114370s3cret")));
        Assert.Contains("<auth1/>", sent);
        Assert.Contains($"<nonce_hash>{expectedHash}</nonce_hash>", sent);
        Assert.DoesNotContain("<auth1 />", sent); // no space before slash, ever
    }

    [Fact]
    public async Task Authorize_wrong_password_returns_false_without_throwing()
    {
        var stream = ScriptedStream.FromReplies(
            "<boinc_gui_rpc_reply>\n<nonce>42</nonce>\n</boinc_gui_rpc_reply>",
            "<boinc_gui_rpc_reply>\n<unauthorized/>\n</boinc_gui_rpc_reply>");
        await using var client = ClientWith(stream);

        bool ok = await client.AuthorizeAsync("wrong");

        Assert.False(ok);
        Assert.Equal(ConnectionState.Connected, client.State);
    }

    [Fact]
    public async Task ExchangeVersions_stores_daemon_version()
    {
        var stream = ScriptedStream.FromReplies(
            "<boinc_gui_rpc_reply>\n<server_version><major>8</major><minor>0</minor><release>4</release></server_version>\n</boinc_gui_rpc_reply>");
        await using var client = ClientWith(stream);

        VersionInfo v = await client.ExchangeVersionsAsync();

        Assert.Equal(new VersionInfo(8, 0, 4), v);
        Assert.Equal(v, client.DaemonVersion);
    }

    [Fact]
    public async Task Rpc_before_connect_throws_invalid_operation()
    {
        await using var client = new BoincGuiRpcClient();
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ExchangeVersionsAsync());
    }
}
