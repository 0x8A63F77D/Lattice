using System.Text;
using Lattice.Boinc.GuiRpc;
using Xunit;

namespace Lattice.Tests;

public class BoincGuiRpcClientRpcTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    private static BoincGuiRpcClient ClientWith(ScriptedStream stream) =>
        new(new RpcConnection(stream));

    [Fact]
    public async Task GetState_returns_typed_state()
    {
        var stream = ScriptedStream.FromReplies(Fixture("get_state.xml"));
        await using var client = ClientWith(stream);

        CcState state = await client.GetStateAsync();

        Assert.Equal(new VersionInfo(8, 0, 4), state.CoreClientVersion);
        Assert.Equal(2, state.Projects.Count);
        string sent = Encoding.ASCII.GetString(stream.Written.ToArray());
        Assert.Contains("<get_state/>", sent);
    }

    [Fact]
    public async Task GetCcStatus_returns_typed_status()
    {
        var stream = ScriptedStream.FromReplies(Fixture("get_cc_status.xml"));
        await using var client = ClientWith(stream);

        CcStatus status = await client.GetCcStatusAsync();

        Assert.Equal(RunMode.Auto, status.TaskMode);
    }

    [Fact]
    public async Task GetResults_sends_active_only_flag()
    {
        var stream = ScriptedStream.FromReplies(Fixture("get_results.xml"));
        await using var client = ClientWith(stream);

        IReadOnlyList<Result> results = await client.GetResultsAsync(activeOnly: true);

        Assert.Equal(2, results.Count);
        string sent = Encoding.ASCII.GetString(stream.Written.ToArray());
        Assert.Contains("<get_results>\n<active_only>1</active_only>\n</get_results>", sent);
    }

    [Fact]
    public async Task GetMessages_sends_seqno()
    {
        var stream = ScriptedStream.FromReplies(Fixture("get_messages.xml"));
        await using var client = ClientWith(stream);

        IReadOnlyList<Message> messages = await client.GetMessagesAsync(seqno: 5);

        Assert.Equal(2, messages.Count);
        string sent = Encoding.ASCII.GetString(stream.Written.ToArray());
        Assert.Contains("<get_messages>\n<seqno>5</seqno>\n</get_messages>", sent);
    }

    [Fact]
    public async Task GetFileTransfers_returns_typed_transfers()
    {
        var stream = ScriptedStream.FromReplies(Fixture("get_file_transfers.xml"));
        await using var client = ClientWith(stream);

        IReadOnlyList<FileTransfer> transfers = await client.GetFileTransfersAsync();

        Assert.Equal(3, transfers.Count);
        Assert.True(transfers[0].XferActive);
        string sent = Encoding.ASCII.GetString(stream.Written.ToArray());
        Assert.Contains("<get_file_transfers/>", sent);
    }

    [Fact]
    public async Task Unauthorized_reply_on_any_rpc_throws()
    {
        var stream = ScriptedStream.FromReplies("<boinc_gui_rpc_reply>\n<unauthorized/>\n</boinc_gui_rpc_reply>");
        await using var client = ClientWith(stream);

        await Assert.ThrowsAsync<BoincUnauthorizedException>(() => client.GetCcStatusAsync());
    }

    [Fact]
    public async Task GetState_missing_client_state_element_throws_protocol_exception()
    {
        var stream = ScriptedStream.FromReplies("<boinc_gui_rpc_reply>\n<something_else/>\n</boinc_gui_rpc_reply>");
        await using var client = ClientWith(stream);

        await Assert.ThrowsAsync<BoincProtocolException>(() => client.GetStateAsync());
    }
}
