using Lattice.Boinc.GuiRpc;
using Xunit;

namespace Lattice.Tests;

/// <summary>
/// Audits the two structural signals against EVERY op, not a representative sample:
/// any RPC may answer &lt;unauthorized/&gt; at any time (client/gui_rpc_server_ops.cpp
/// answers it for every op once auth is needed, before the handler runs), and failure
/// is signalled by an &lt;error&gt; tag whose text is never branched on.
/// <see cref="Audit_covers_every_op_on_the_interface"/> keeps this exhaustive: a new
/// op fails that test until it is listed here.
/// </summary>
public class GuiRpcStructuralErrorPathTests
{
    private static readonly Dictionary<string, Func<IGuiRpcClient, Task>> Ops = new()
    {
        [nameof(IGuiRpcClient.ExchangeVersionsAsync)] = c => c.ExchangeVersionsAsync(),
        [nameof(IGuiRpcClient.GetStateAsync)] = c => c.GetStateAsync(),
        [nameof(IGuiRpcClient.GetCcStatusAsync)] = c => c.GetCcStatusAsync(),
        [nameof(IGuiRpcClient.GetResultsAsync)] = c => c.GetResultsAsync(),
        [nameof(IGuiRpcClient.GetMessagesAsync)] = c => c.GetMessagesAsync(),
        [nameof(IGuiRpcClient.GetFileTransfersAsync)] = c => c.GetFileTransfersAsync(),
        [nameof(IGuiRpcClient.GetProjectStatusAsync)] = c => c.GetProjectStatusAsync(),
        [nameof(IGuiRpcClient.GetStatisticsAsync)] = c => c.GetStatisticsAsync(),
        [nameof(IGuiRpcClient.PerformTaskOpAsync)] = c => c.PerformTaskOpAsync(TaskOp.Suspend, "https://p/", "t1"),
        [nameof(IGuiRpcClient.PerformProjectOpAsync)] = c => c.PerformProjectOpAsync(ProjectOp.Update, "https://p/"),
        [nameof(IGuiRpcClient.SetModeAsync)] = c => c.SetModeAsync(ModeLane.Cpu, RunMode.Auto, TimeSpan.Zero),
        [nameof(IGuiRpcClient.RequestAccountLookupAsync)] = c => c.RequestAccountLookupAsync("https://p/", "u@e", "pw"),
        [nameof(IGuiRpcClient.PollAccountLookupAsync)] = c => c.PollAccountLookupAsync(),
        [nameof(IGuiRpcClient.RequestProjectAttachAsync)] = c => c.RequestProjectAttachAsync("https://p/", "auth", "P", "u@e"),
        [nameof(IGuiRpcClient.PollProjectAttachAsync)] = c => c.PollProjectAttachAsync(),
    };

    // lookup_account_poll is the one documented exception on the <error> leg: the
    // daemon passes the PROJECT SERVER's <error> through, meaning the lookup failed
    // while the RPC succeeded, so it is returned as data (see PollAccountLookupAsync).
    private const string ErrorAsDataOp = nameof(IGuiRpcClient.PollAccountLookupAsync);

    public static TheoryData<string> AllOps => [.. Ops.Keys];

    public static TheoryData<string> ThrowingErrorOps => [.. Ops.Keys.Where(k => k != ErrorAsDataOp)];

    private static IGuiRpcClient ClientWith(string reply) =>
        new BoincGuiRpcClient(new RpcConnection(ScriptedStream.FromReplies(reply)));

    [Theory]
    [MemberData(nameof(AllOps))]
    public async Task Unauthorized_reply_throws_on_every_op(string op)
    {
        await using IGuiRpcClient client =
            ClientWith("<boinc_gui_rpc_reply>\n<unauthorized/>\n</boinc_gui_rpc_reply>");

        await Assert.ThrowsAsync<BoincUnauthorizedException>(() => Ops[op](client));
    }

    [Theory]
    [MemberData(nameof(ThrowingErrorOps))]
    public async Task Error_reply_throws_with_verbatim_text_on_every_op(string op)
    {
        await using IGuiRpcClient client =
            ClientWith("<boinc_gui_rpc_reply>\n<error>Missing authenticator</error>\n</boinc_gui_rpc_reply>");

        var ex = await Assert.ThrowsAsync<BoincRpcException>(() => Ops[op](client));

        Assert.Equal("Missing authenticator", ex.ErrorText);
    }

    [Fact]
    public async Task Error_reply_on_lookup_account_poll_is_returned_as_data()
    {
        await using IGuiRpcClient client =
            ClientWith("<boinc_gui_rpc_reply>\n<error>Account not found</error>\n</boinc_gui_rpc_reply>");

        AccountLookupReply reply = await client.PollAccountLookupAsync();

        Assert.Equal(-1, reply.ErrorNum);
        Assert.Equal("Account not found", reply.ErrorMessage);
        Assert.Equal(string.Empty, reply.Authenticator);
    }

    [Fact]
    public void Audit_covers_every_op_on_the_interface()
    {
        // Connect/Authorize are the connection handshake, not RPCs subject to the two
        // signals above: AuthorizeAsync deliberately reads <unauthorized/> as data
        // ("wrong password"), and is covered by its own tests below.
        string[] exempt = [nameof(IGuiRpcClient.ConnectAsync), nameof(IGuiRpcClient.AuthorizeAsync)];
        string[] uncovered = [.. typeof(IGuiRpcClient).GetMethods()
            .Select(m => m.Name)
            .Except(exempt)
            .Except(Ops.Keys)];

        Assert.Empty(uncovered);
    }

    // --- The handshake's own structural paths ---

    [Fact]
    public async Task Unauthorized_on_auth1_throws_rather_than_reading_as_a_bad_password()
    {
        // auth1 needs no credentials, so <unauthorized/> here means the daemon refused
        // the connection outright (remote_hosts.cfg) — not "try another password".
        await using var client = new BoincGuiRpcClient(new RpcConnection(
            ScriptedStream.FromReplies("<boinc_gui_rpc_reply>\n<unauthorized/>\n</boinc_gui_rpc_reply>")));

        await Assert.ThrowsAsync<BoincUnauthorizedException>(() => client.AuthorizeAsync("pw"));
    }

    [Fact]
    public async Task Error_on_auth1_throws_rpc_exception()
    {
        await using var client = new BoincGuiRpcClient(new RpcConnection(
            ScriptedStream.FromReplies("<boinc_gui_rpc_reply>\n<error>bad request</error>\n</boinc_gui_rpc_reply>")));

        var ex = await Assert.ThrowsAsync<BoincRpcException>(() => client.AuthorizeAsync("pw"));

        Assert.Equal("bad request", ex.ErrorText);
    }

    [Fact]
    public async Task Missing_authorized_tag_reads_as_a_rejected_password()
    {
        // handle_auth2 answers exactly <authorized/> or <unauthorized/>; anything else
        // must fail closed rather than be treated as success.
        await using var client = new BoincGuiRpcClient(new RpcConnection(ScriptedStream.FromReplies(
            "<boinc_gui_rpc_reply>\n<nonce>42</nonce>\n</boinc_gui_rpc_reply>",
            "<boinc_gui_rpc_reply>\n</boinc_gui_rpc_reply>")));

        Assert.False(await client.AuthorizeAsync("pw"));
        Assert.Equal(ConnectionState.Connected, client.State);
    }

    // --- Fabricated-record guard: a missing container must never read as valid data ---

    [Fact]
    public async Task Missing_attach_reply_container_throws_instead_of_reading_as_success()
    {
        // ErrorNum 0 means "the daemon accepted the attach". Parsing a containerless
        // reply as a record yields exactly that value out of thin air, so the caller
        // would report a successful attach that never happened.
        await using var client = new BoincGuiRpcClient(new RpcConnection(
            ScriptedStream.FromReplies("<boinc_gui_rpc_reply>\n<something_else/>\n</boinc_gui_rpc_reply>")));

        var ex = await Assert.ThrowsAsync<BoincProtocolException>(() => client.PollProjectAttachAsync());

        Assert.Contains("<project_attach_reply>", ex.Message);
    }

    [Fact]
    public async Task Missing_cc_status_container_throws_instead_of_reading_as_all_zero()
    {
        // An all-zero CcStatus is not a neutral default: it reports every lane as an
        // unknown run mode with no suspend reason.
        await using var client = new BoincGuiRpcClient(new RpcConnection(
            ScriptedStream.FromReplies("<boinc_gui_rpc_reply>\n</boinc_gui_rpc_reply>")));

        var ex = await Assert.ThrowsAsync<BoincProtocolException>(() => client.GetCcStatusAsync());

        Assert.Contains("<cc_status>", ex.Message);
    }

    [Fact]
    public async Task Missing_account_out_container_throws()
    {
        await using var client = new BoincGuiRpcClient(new RpcConnection(
            ScriptedStream.FromReplies("<boinc_gui_rpc_reply>\n<unrelated/>\n</boinc_gui_rpc_reply>")));

        var ex = await Assert.ThrowsAsync<BoincProtocolException>(() => client.PollAccountLookupAsync());

        Assert.Contains("<account_out>", ex.Message);
    }
}
