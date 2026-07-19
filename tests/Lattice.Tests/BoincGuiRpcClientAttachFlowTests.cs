using System.Text;
using Lattice.Boinc.GuiRpc;
using Xunit;

namespace Lattice.Tests;

public class BoincGuiRpcClientAttachFlowTests
{
    private const string SuccessReply = "<boinc_gui_rpc_reply>\n<success/>\n</boinc_gui_rpc_reply>";
    private const string UnauthorizedReply = "<boinc_gui_rpc_reply>\n<unauthorized/>\n</boinc_gui_rpc_reply>";

    private static BoincGuiRpcClient ClientWith(ScriptedStream stream) =>
        new(new RpcConnection(stream));

    private static string Sent(ScriptedStream stream) =>
        Encoding.ASCII.GetString(stream.Written.ToArray());

    [Fact]
    public async Task RequestAccountLookup_sends_lowercased_email_and_password_email_hash()
    {
        var stream = ScriptedStream.FromReplies(SuccessReply);
        await using var client = ClientWith(stream);

        await client.RequestAccountLookupAsync("http://example.com/", "User@Example.COM", "pw");

        // Known vector: MD5("pw" + "user@example.com") — the daemon compares
        // MD5(password + lowercased email), W4.
        Assert.Contains(
            "<lookup_account>\n<url>http://example.com/</url>\n<email_addr>user@example.com</email_addr>\n" +
            "<passwd_hash>e5cd733f5dbad36cb094ff65a6948b61</passwd_hash>\n<ldap_auth>0</ldap_auth>\n</lookup_account>",
            Sent(stream));
    }

    [Fact]
    public async Task RequestAccountLookup_never_writes_the_password()
    {
        var stream = ScriptedStream.FromReplies(SuccessReply);
        await using var client = ClientWith(stream);

        await client.RequestAccountLookupAsync("http://example.com/", "user@example.com", "hunter2!secret");

        Assert.DoesNotContain("hunter2!secret", Sent(stream));
    }

    [Fact]
    public async Task RequestAccountLookup_escapes_url_and_email_but_hashes_the_raw_email()
    {
        var stream = ScriptedStream.FromReplies(SuccessReply);
        await using var client = ClientWith(stream);

        await client.RequestAccountLookupAsync("http://example.com/?a=1&b=2", "a&b@example.com", "pw");

        string sent = Sent(stream);
        Assert.Contains("<url>http://example.com/?a=1&amp;b=2</url>", sent);
        Assert.Contains("<email_addr>a&amp;b@example.com</email_addr>", sent);
        // MD5("pw" + "a&b@example.com"): the hash covers the RAW lowercased email;
        // escaping applies only to the XML serialization.
        Assert.Contains("<passwd_hash>98b0edd2c0aa277a04163cb317e7fcb7</passwd_hash>", sent);
        Assert.DoesNotContain("a&b", sent);
    }

    [Fact]
    public async Task RequestAccountLookup_error_reply_throws_rpc_exception()
    {
        var stream = ScriptedStream.FromReplies("<boinc_gui_rpc_reply>\n<error>some daemon text</error>\n</boinc_gui_rpc_reply>");
        await using var client = ClientWith(stream);

        var ex = await Assert.ThrowsAsync<BoincRpcException>(
            () => client.RequestAccountLookupAsync("http://example.com/", "a@b.c", "pw"));
        Assert.Equal("some daemon text", ex.ErrorText);
    }

    [Fact]
    public async Task PollAccountLookup_sends_bare_poll_tag_without_space()
    {
        var stream = ScriptedStream.FromReplies(
            "<boinc_gui_rpc_reply>\n<account_out>\n<error_num>-204</error_num>\n</account_out>\n</boinc_gui_rpc_reply>");
        await using var client = ClientWith(stream);

        await client.PollAccountLookupAsync();

        string sent = Sent(stream);
        Assert.Contains("<lookup_account_poll/>", sent);
        Assert.DoesNotContain("<lookup_account_poll />", sent);
    }

    [Fact]
    public async Task PollAccountLookup_in_progress_reply_maps_to_InProgress_code()
    {
        var stream = ScriptedStream.FromReplies(
            "<boinc_gui_rpc_reply>\n<account_out>\n<error_num>-204</error_num>\n</account_out>\n</boinc_gui_rpc_reply>");
        await using var client = ClientWith(stream);

        AccountLookupReply reply = await client.PollAccountLookupAsync();

        Assert.Equal(BoincErrorCodes.InProgress, reply.ErrorNum);
        Assert.Equal(string.Empty, reply.ErrorMessage);
        Assert.Equal(string.Empty, reply.Authenticator);
    }

    [Fact]
    public async Task PollAccountLookup_success_reply_carries_authenticator()
    {
        var stream = ScriptedStream.FromReplies(
            "<boinc_gui_rpc_reply>\n<account_out>\n<authenticator>abc123def456</authenticator>\n</account_out>\n</boinc_gui_rpc_reply>");
        await using var client = ClientWith(stream);

        AccountLookupReply reply = await client.PollAccountLookupAsync();

        Assert.Equal(0, reply.ErrorNum);
        Assert.Equal("abc123def456", reply.Authenticator);
    }

    [Fact]
    public async Task PollAccountLookup_failure_reply_carries_code_and_message()
    {
        var stream = ScriptedStream.FromReplies(
            "<boinc_gui_rpc_reply>\n<account_out>\n<error_num>-161</error_num>\n<error_msg>user not found</error_msg>\n</account_out>\n</boinc_gui_rpc_reply>");
        await using var client = ClientWith(stream);

        AccountLookupReply reply = await client.PollAccountLookupAsync();

        Assert.Equal(-161, reply.ErrorNum);
        Assert.Equal("user not found", reply.ErrorMessage);
    }

    [Fact]
    public async Task PollAccountLookup_bare_error_reply_means_lookup_failed_not_rpc_failure()
    {
        // handle_lookup_account_poll passes the project server's raw <error> through
        // (design 1.2): the LOOKUP failed; the RPC itself worked. Must not throw.
        var stream = ScriptedStream.FromReplies(
            "<boinc_gui_rpc_reply>\n<error>project says no</error>\n</boinc_gui_rpc_reply>");
        await using var client = ClientWith(stream);

        AccountLookupReply reply = await client.PollAccountLookupAsync();

        Assert.Equal(-1, reply.ErrorNum);
        Assert.Equal("project says no", reply.ErrorMessage);
        Assert.Equal(string.Empty, reply.Authenticator);
    }

    [Fact]
    public async Task RequestProjectAttach_sends_all_four_fields_escaped()
    {
        var stream = ScriptedStream.FromReplies(SuccessReply);
        await using var client = ClientWith(stream);

        await client.RequestProjectAttachAsync("http://example.com/?x=1&y=2", "key&123", "P&Q Grid", "a@b.c");

        Assert.Contains(
            "<project_attach>\n<project_url>http://example.com/?x=1&amp;y=2</project_url>\n" +
            "<authenticator>key&amp;123</authenticator>\n<project_name>P&amp;Q Grid</project_name>\n" +
            "<email_addr>a@b.c</email_addr>\n</project_attach>",
            Sent(stream));
    }

    [Fact]
    public async Task PollProjectAttach_sends_bare_poll_tag_and_parses_messages_in_order()
    {
        var stream = ScriptedStream.FromReplies(
            "<boinc_gui_rpc_reply>\n<project_attach_reply>\n<message>first</message>\n<message>second</message>\n" +
            "<error_num>-107</error_num>\n</project_attach_reply>\n</boinc_gui_rpc_reply>");
        await using var client = ClientWith(stream);

        ProjectAttachReply reply = await client.PollProjectAttachAsync();

        Assert.Equal(-107, reply.ErrorNum);
        Assert.Equal(["first", "second"], reply.Messages);
        string sent = Sent(stream);
        Assert.Contains("<project_attach_poll/>", sent);
        Assert.DoesNotContain("<project_attach_poll />", sent);
    }

    [Fact]
    public async Task PollProjectAttach_success_reply_has_zero_code_and_no_messages()
    {
        var stream = ScriptedStream.FromReplies(
            "<boinc_gui_rpc_reply>\n<project_attach_reply>\n<error_num>0</error_num>\n</project_attach_reply>\n</boinc_gui_rpc_reply>");
        await using var client = ClientWith(stream);

        ProjectAttachReply reply = await client.PollProjectAttachAsync();

        Assert.Equal(0, reply.ErrorNum);
        Assert.Empty(reply.Messages);
    }

    [Fact]
    public async Task PollProjectAttach_bare_error_reply_throws_rpc_exception()
    {
        // Deliberate asymmetry with the lookup poll: only handle_lookup_account_poll
        // passes a project server's <error> through as flow data (design 1.2). For the
        // attach poll an <error> reply keeps the generic meaning: the RPC failed.
        var stream = ScriptedStream.FromReplies(
            "<boinc_gui_rpc_reply>\n<error>Already attached to project</error>\n</boinc_gui_rpc_reply>");
        await using var client = ClientWith(stream);

        await Assert.ThrowsAsync<BoincRpcException>(() => client.PollProjectAttachAsync());
    }

    public static TheoryData<string> AttachFlowOps => new("lookup", "lookup_poll", "attach", "attach_poll");

    [Theory]
    [MemberData(nameof(AttachFlowOps))]
    public async Task Unauthorized_reply_throws_on_every_attach_flow_rpc(string op)
    {
        var stream = ScriptedStream.FromReplies(UnauthorizedReply);
        await using var client = ClientWith(stream);

        Func<Task> act = op switch
        {
            "lookup" => () => client.RequestAccountLookupAsync("http://example.com/", "a@b.c", "pw"),
            "lookup_poll" => () => client.PollAccountLookupAsync(),
            "attach" => () => client.RequestProjectAttachAsync("http://example.com/", "auth", "name", "a@b.c"),
            "attach_poll" => () => client.PollProjectAttachAsync(),
            _ => throw new ArgumentOutOfRangeException(nameof(op)),
        };

        await Assert.ThrowsAsync<BoincUnauthorizedException>(act);
    }

    [Fact]
    public void BoincErrorCodes_pin_the_upstream_values()
    {
        Assert.Equal(-204, BoincErrorCodes.InProgress);
        Assert.Equal(-199, BoincErrorCodes.Retry);
    }
}
