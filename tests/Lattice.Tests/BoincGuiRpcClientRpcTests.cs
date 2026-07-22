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
    public async Task GetProjectStatus_returns_typed_projects_with_presence_flags_round_tripped()
    {
        var stream = ScriptedStream.FromReplies(Fixture("get_project_status.xml"));
        await using var client = ClientWith(stream);

        IReadOnlyList<Project> projects = await client.GetProjectStatusAsync();

        Assert.Equal(2, projects.Count);
        Assert.Equal("Einstein@Home", projects[0].ProjectName);
        Assert.Equal(4321.098765, projects[0].UserExpavgCredit);
        Assert.False(projects[0].SuspendedViaGui);
        Assert.False(projects[0].DontRequestMoreWork);
        Assert.True(projects[1].SuspendedViaGui);
        Assert.True(projects[1].DontRequestMoreWork);
        string sent = Encoding.ASCII.GetString(stream.Written.ToArray());
        Assert.Contains("<get_project_status/>", sent);
    }

    [Fact]
    public async Task GetStatistics_sends_request_and_returns_typed_history()
    {
        var stream = ScriptedStream.FromReplies(Fixture("get_statistics.xml"));
        await using var client = ClientWith(stream);

        IReadOnlyList<ProjectStatistics> stats = await client.GetStatisticsAsync();

        Assert.Equal(2, stats.Count);
        Assert.Equal("https://einsteinathome.org/", stats[0].MasterUrl);
        Assert.Equal(3, stats[0].Daily.Count);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1750982400), stats[0].Daily[0].Day);
        Assert.Equal(1200.25, stats[0].Daily[0].HostExpavgCredit);
        string sent = Encoding.ASCII.GetString(stream.Written.ToArray());
        Assert.Contains("<get_statistics/>", sent);
    }

    [Fact]
    public async Task GetStatistics_sanitizes_noncompliant_reply()
    {
        // BOINC's hand-rolled writer does not escape master_url, so a URL with a query
        // string reaches us with a bare '&'. The client's sanitizer must repair it into
        // valid XML rather than throwing on the strict parser.
        const string raw =
            "<boinc_gui_rpc_reply>\n<statistics>\n<project_statistics>\n" +
            "<master_url>https://example.org/boinc/?a=1&b=2</master_url>\n" +
            "<daily_statistics>\n<day>1750982400.000000</day>\n" +
            "<user_total_credit>10.000000</user_total_credit>\n" +
            "</daily_statistics>\n</project_statistics>\n</statistics>\n</boinc_gui_rpc_reply>";
        var stream = ScriptedStream.FromReplies(raw);
        await using var client = ClientWith(stream);

        ProjectStatistics project = Assert.Single(await client.GetStatisticsAsync());

        Assert.Equal("https://example.org/boinc/?a=1&b=2", project.MasterUrl);
        Assert.Equal(10.0, Assert.Single(project.Daily).UserTotalCredit);
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

    [Fact]
    public async Task Client_implements_IGuiRpcClient()
    {
        var stream = ScriptedStream.FromReplies(Fixture("get_cc_status.xml"));
        await using BoincGuiRpcClient client = ClientWith(stream);

        IGuiRpcClient viaInterface = client;
        CcStatus status = await viaInterface.GetCcStatusAsync();

        Assert.Equal(RunMode.Auto, status.TaskMode);
    }
}
