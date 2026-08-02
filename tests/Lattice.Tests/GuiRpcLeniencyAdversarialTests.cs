using Lattice.Boinc.GuiRpc;
using Xunit;

namespace Lattice.Tests;

/// <summary>
/// Adversarial fixtures for the quirk classes BOINC's hand-rolled XML writer can
/// actually emit. Every input here is traced to a printf in the reference source
/// (client/*.cpp) rather than invented; the cite is on each test.
/// </summary>
public class GuiRpcLeniencyAdversarialTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    private static BoincGuiRpcClient ClientWith(string reply) =>
        new(new RpcConnection(ScriptedStream.FromReplies(reply)));

    private static string MessagesReply(string project, string body) =>
        "<boinc_gui_rpc_reply>\n<msgs>\n<msg>\n" +
        $" <project>{project}</project>\n <pri>1</pri>\n <seqno>7</seqno>\n" +
        $" <body><![CDATA[\n{body}\n]]></body>\n <time>1751600000.000000</time>\n" +
        "</msg>\n</msgs>\n</boinc_gui_rpc_reply>";

    // --- CDATA: the daemon's own framing for every event-log message body ---

    [Fact]
    public async Task Cdata_message_body_keeps_ampersand_and_angle_brackets_literal()
    {
        // client/client_msgs.cpp writes "<body><![CDATA[\n%s\n]]></body>". Inside CDATA
        // '&' and '<' are ordinary characters, so the body must arrive byte-for-byte.
        await using var client = ClientWith(
            MessagesReply("Einstein@Home", "Requesting work & reporting 1 task; 3 < 4"));

        Message msg = Assert.Single(await client.GetMessagesAsync());

        Assert.Equal("Requesting work & reporting 1 task; 3 < 4", msg.Body);
    }

    [Fact]
    public async Task Cdata_message_body_does_not_decode_entity_like_text()
    {
        // The mirror of the case above: text that merely LOOKS like an entity is also
        // literal inside CDATA, so it must not be decoded into '&'.
        await using var client = ClientWith(MessagesReply("", "wrote &amp; then &lt;"));

        Message msg = Assert.Single(await client.GetMessagesAsync());

        Assert.Equal("wrote &amp; then &lt;", msg.Body);
    }

    [Fact]
    public async Task Bare_ampersand_outside_cdata_is_repaired_in_the_same_reply()
    {
        // <project> is printf'd raw in the same MESSAGE_DESCS::write call, so one reply
        // can carry a bare '&' that IS markup (project) and one that is NOT (body).
        // The two must be treated differently.
        await using var client = ClientWith(MessagesReply("Rosetta & Co", "url http://x/?a=1&b=2"));

        Message msg = Assert.Single(await client.GetMessagesAsync());

        Assert.Equal("Rosetta & Co", msg.Project);
        Assert.Equal("url http://x/?a=1&b=2", msg.Body);
    }

    [Fact]
    public async Task Shipped_get_messages_fixture_carries_the_daemon_cdata_shape()
    {
        await using var client = ClientWith(Fixture("get_messages.xml"));

        IReadOnlyList<Message> msgs = await client.GetMessagesAsync();

        Assert.Equal(2, msgs.Count);
        Assert.Equal("Starting BOINC client version 8.0.4 for x86_64-pc-linux-gnu", msgs[0].Body);
        Assert.Equal("Sending scheduler request: To fetch work & report 1 task", msgs[1].Body);
    }

    [Fact]
    public async Task Control_characters_are_stripped_from_a_cdata_body()
    {
        // App stderr reaches message bodies verbatim and can carry control bytes.
        // XML 1.0 forbids them even inside CDATA, so stripping is the only repair that
        // keeps the reply parseable — and it is a repair we make everywhere, not just
        // in markup.
        await using var client = ClientWith(MessagesReply("", "progress\x01 50%\x1F done"));

        Message msg = Assert.Single(await client.GetMessagesAsync());

        Assert.Equal("progress 50% done", msg.Body);
    }

    [Fact]
    public async Task Unterminated_cdata_is_reported_as_a_protocol_error()
    {
        string reply = "<boinc_gui_rpc_reply>\n<msgs>\n<msg>\n <seqno>1</seqno>\n" +
            " <body><![CDATA[\ntruncated body with no closing marker\n</msg>\n</msgs>\n";
        await using var client = ClientWith(reply);

        var ex = await Assert.ThrowsAsync<BoincProtocolException>(() => client.GetMessagesAsync());

        Assert.Contains("CDATA", ex.RawPayload);
    }

    [Fact]
    public async Task Message_body_containing_the_cdata_close_marker_is_a_protocol_error()
    {
        // DAEMON BUG, documented not worked around: client/client_msgs.cpp interpolates
        // the message into CDATA without checking for "]]>", so a body containing that
        // marker closes the section early and produces XML no parser can rescue. There
        // is no way to tell the intended body from the resulting markup, so the honest
        // outcome is a typed protocol error carrying the payload for diagnosis.
        await using var client = ClientWith(MessagesReply("", "app printed ]]> unexpectedly"));

        var ex = await Assert.ThrowsAsync<BoincProtocolException>(() => client.GetMessagesAsync());

        Assert.Contains("]]>", ex.RawPayload);
    }

    // --- Unescaped markup outside CDATA ---

    [Fact]
    public async Task Undeclared_entity_in_an_unescaped_field_is_repaired()
    {
        // RESULT::write_gui printf's <name> raw; a task name containing "&nbsp;" would
        // otherwise be an undeclared entity and abort the whole parse.
        await using var client = ClientWith(
            "<boinc_gui_rpc_reply>\n<results>\n<result>\n<name>h1&nbsp;0437</name>\n" +
            "<state>2</state>\n</result>\n</results>\n</boinc_gui_rpc_reply>");

        Result result = Assert.Single(await client.GetResultsAsync());

        Assert.Equal("h1&nbsp;0437", result.Name);
    }

    [Fact]
    public async Task Stray_iso_declaration_in_a_passthrough_reply_is_dropped()
    {
        // handle_lookup_account_poll dumps the project server's reply into the envelope
        // verbatim ("grc.mfout.printf(\"%s\", q)"), and BOINC's own server code prefixes
        // that reply with an XML declaration (html/inc/xml.inc). It therefore lands
        // mid-document, where it is illegal.
        await using var client = ClientWith(
            "<boinc_gui_rpc_reply>\n<?xml version=\"1.0\" encoding=\"ISO-8859-1\" ?>\n" +
            "<account_out>\n<authenticator>3f8a9b</authenticator>\n</account_out>\n" +
            "</boinc_gui_rpc_reply>");

        AccountLookupReply reply = await client.PollAccountLookupAsync();

        Assert.Equal(0, reply.ErrorNum);
        Assert.Equal("3f8a9b", reply.Authenticator);
    }

    // --- Shape quirks: unknown, missing, duplicated, empty ---

    [Fact]
    public async Task Unknown_tags_are_ignored()
    {
        // Forward compatibility: a newer daemon adds fields we do not model.
        await using var client = ClientWith(
            "<boinc_gui_rpc_reply>\n<results>\n<result>\n<name>t1</name>\n<state>2</state>\n" +
            "<future_field>42</future_field>\n<nested_future><a>1</a></nested_future>\n" +
            "</result>\n</results>\n<trailing_unknown/>\n</boinc_gui_rpc_reply>");

        Result result = Assert.Single(await client.GetResultsAsync());

        Assert.Equal("t1", result.Name);
        Assert.Equal(ResultState.FilesDownloaded, result.State);
    }

    [Fact]
    public async Task Missing_fields_fall_back_to_documented_defaults()
    {
        // Older daemons omit fields wholesale; each absent field must take its
        // documented zero value rather than aborting the parse of the whole result.
        await using var client = ClientWith(
            "<boinc_gui_rpc_reply>\n<results>\n<result>\n<name>only-a-name</name>\n" +
            "</result>\n</results>\n</boinc_gui_rpc_reply>");

        Result result = Assert.Single(await client.GetResultsAsync());

        Assert.Equal("only-a-name", result.Name);
        Assert.Equal(string.Empty, result.WorkunitName);
        Assert.Equal(string.Empty, result.ProjectUrl);
        Assert.Equal((ResultState)0, result.State);
        Assert.Null(result.ReportDeadline);
        Assert.False(result.ReadyToReport);
        Assert.False(result.SuspendedViaGui);
        Assert.Equal(0, result.FinalCpuTime);
        Assert.Equal(0, result.EstimatedCpuTimeRemaining);
        Assert.Null(result.ActiveTask);
        Assert.Equal(string.Empty, result.SchedulerWaitReason);
    }

    [Fact]
    public async Task Unparseable_numeric_field_falls_back_instead_of_throwing()
    {
        // A truncated or locale-mangled number must not take the whole poll down.
        await using var client = ClientWith(
            "<boinc_gui_rpc_reply>\n<results>\n<result>\n<name>t1</name>\n" +
            "<state>not-a-number</state>\n<final_cpu_time>1.#INF</final_cpu_time>\n" +
            "</result>\n</results>\n</boinc_gui_rpc_reply>");

        Result result = Assert.Single(await client.GetResultsAsync());

        Assert.Equal((ResultState)0, result.State);
        Assert.Equal(0, result.FinalCpuTime);
    }

    [Fact]
    public async Task Duplicated_field_takes_the_first_occurrence()
    {
        // Pinned behavior, and a deliberate divergence: BOINC's own parser loops over
        // tags assigning on each match, so the reference takes the LAST occurrence.
        // No daemon path emits duplicates, so this is a pin rather than a fix — the
        // divergence is recorded as an open question rather than guessed at.
        await using var client = ClientWith(
            "<boinc_gui_rpc_reply>\n<results>\n<result>\n<name>first</name>\n" +
            "<name>second</name>\n</result>\n</results>\n</boinc_gui_rpc_reply>");

        Result result = Assert.Single(await client.GetResultsAsync());

        Assert.Equal("first", result.Name);
    }

    [Fact]
    public async Task Empty_container_yields_an_empty_list()
    {
        // An idle host: handle_get_results still prints the container.
        await using var client = ClientWith(
            "<boinc_gui_rpc_reply>\n<results>\n</results>\n</boinc_gui_rpc_reply>");

        Assert.Empty(await client.GetResultsAsync());
    }

    [Fact]
    public async Task Self_closing_container_yields_an_empty_list()
    {
        await using var client = ClientWith(
            "<boinc_gui_rpc_reply>\n<msgs/>\n</boinc_gui_rpc_reply>");

        Assert.Empty(await client.GetMessagesAsync());
    }

    [Fact]
    public async Task Empty_reply_envelope_is_reported_as_a_missing_container()
    {
        // Distinct from "no tasks": the daemon always prints its container, so its
        // absence is contract-breaking and must not be reported as an empty list.
        await using var client = ClientWith("<boinc_gui_rpc_reply>\n</boinc_gui_rpc_reply>");

        var ex = await Assert.ThrowsAsync<BoincProtocolException>(() => client.GetResultsAsync());

        Assert.Contains("<results>", ex.Message);
    }
}
