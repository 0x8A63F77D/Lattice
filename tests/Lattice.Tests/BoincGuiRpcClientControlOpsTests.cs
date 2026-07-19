using System.Globalization;
using System.Text;
using Lattice.Boinc.GuiRpc;
using Xunit;

namespace Lattice.Tests;

public class BoincGuiRpcClientControlOpsTests
{
    private const string SuccessReply = "<boinc_gui_rpc_reply>\n<success/>\n</boinc_gui_rpc_reply>";
    private const string ErrorReply = "<boinc_gui_rpc_reply>\n<error>some text</error>\n</boinc_gui_rpc_reply>";
    private const string UnauthorizedReply = "<boinc_gui_rpc_reply>\n<unauthorized/>\n</boinc_gui_rpc_reply>";

    private static BoincGuiRpcClient ClientWith(ScriptedStream stream) =>
        new(new RpcConnection(stream));

    private static string Sent(ScriptedStream stream) =>
        Encoding.ASCII.GetString(stream.Written.ToArray());

    [Theory]
    [InlineData(TaskOp.Suspend, "suspend_result")]
    [InlineData(TaskOp.Resume, "resume_result")]
    [InlineData(TaskOp.Abort, "abort_result")]
    public async Task TaskOp_sends_exact_body(TaskOp op, string tag)
    {
        var stream = ScriptedStream.FromReplies(SuccessReply);
        await using var client = ClientWith(stream);

        await client.PerformTaskOpAsync(op, "http://example.com/", "task_1");

        Assert.Contains(
            $"<{tag}>\n<project_url>http://example.com/</project_url>\n<name>task_1</name>\n</{tag}>",
            Sent(stream));
    }

    [Theory]
    [InlineData(ProjectOp.Suspend, "project_suspend")]
    [InlineData(ProjectOp.Resume, "project_resume")]
    [InlineData(ProjectOp.Update, "project_update")]
    [InlineData(ProjectOp.Detach, "project_detach")]
    public async Task ProjectOp_sends_exact_body(ProjectOp op, string tag)
    {
        var stream = ScriptedStream.FromReplies(SuccessReply);
        await using var client = ClientWith(stream);

        await client.PerformProjectOpAsync(op, "http://example.com/");

        Assert.Contains(
            $"<{tag}>\n<project_url>http://example.com/</project_url>\n</{tag}>",
            Sent(stream));
    }

    [Theory]
    [InlineData(ModeLane.Cpu, "set_run_mode")]
    [InlineData(ModeLane.Gpu, "set_gpu_mode")]
    [InlineData(ModeLane.Network, "set_network_mode")]
    public async Task SetMode_selects_the_lane_tag(ModeLane lane, string tag)
    {
        var stream = ScriptedStream.FromReplies(SuccessReply);
        await using var client = ClientWith(stream);

        await client.SetModeAsync(lane, RunMode.Auto, TimeSpan.Zero);

        Assert.Contains($"<{tag}>\n<auto/>\n<duration>0</duration>\n</{tag}>", Sent(stream));
    }

    // Mode tags must be self-closing with NO space before the slash: the daemon's
    // hand-rolled parser does not recognize "<always />" (protocol landmine).
    [Theory]
    [InlineData(RunMode.Always, "always")]
    [InlineData(RunMode.Auto, "auto")]
    [InlineData(RunMode.Never, "never")]
    [InlineData(RunMode.Restore, "restore")]
    public async Task SetMode_sends_exact_self_closing_mode_tag(RunMode mode, string tag)
    {
        var stream = ScriptedStream.FromReplies(SuccessReply);
        await using var client = ClientWith(stream);

        await client.SetModeAsync(ModeLane.Cpu, mode, TimeSpan.FromHours(1));

        string sent = Sent(stream);
        Assert.Contains($"<set_run_mode>\n<{tag}/>\n<duration>3600</duration>\n</set_run_mode>", sent);
        Assert.DoesNotContain($"<{tag} />", sent);
    }

    [Fact]
    public async Task SetMode_duration_uses_invariant_culture()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE"); // decimal comma culture
        try
        {
            var stream = ScriptedStream.FromReplies(SuccessReply);
            await using var client = ClientWith(stream);

            await client.SetModeAsync(ModeLane.Gpu, RunMode.Never, TimeSpan.FromSeconds(1.5));

            Assert.Contains("<duration>1.5</duration>", Sent(stream));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public async Task TaskOp_xml_escapes_interpolated_fields()
    {
        var stream = ScriptedStream.FromReplies(SuccessReply);
        await using var client = ClientWith(stream);

        await client.PerformTaskOpAsync(TaskOp.Abort, "http://example.com/?a=1&b=2", "wu<1>&_0");

        string sent = Sent(stream);
        Assert.Contains("<project_url>http://example.com/?a=1&amp;b=2</project_url>", sent);
        Assert.Contains("<name>wu&lt;1&gt;&amp;_0</name>", sent);
    }

    [Fact]
    public async Task ProjectOp_xml_escapes_the_url()
    {
        var stream = ScriptedStream.FromReplies(SuccessReply);
        await using var client = ClientWith(stream);

        await client.PerformProjectOpAsync(ProjectOp.Update, "http://example.com/?a=1&b=2");

        Assert.Contains("<project_url>http://example.com/?a=1&amp;b=2</project_url>", Sent(stream));
    }

    private static Task PerformOp(BoincGuiRpcClient client, string opName) => opName switch
    {
        "task" => client.PerformTaskOpAsync(TaskOp.Suspend, "http://example.com/", "task_1"),
        "project" => client.PerformProjectOpAsync(ProjectOp.Suspend, "http://example.com/"),
        "mode" => client.SetModeAsync(ModeLane.Cpu, RunMode.Never, TimeSpan.Zero),
        _ => throw new ArgumentOutOfRangeException(nameof(opName)),
    };

    [Theory]
    [InlineData("task")]
    [InlineData("project")]
    [InlineData("mode")]
    public async Task Success_reply_completes(string opName)
    {
        var stream = ScriptedStream.FromReplies(SuccessReply);
        await using var client = ClientWith(stream);

        await PerformOp(client, opName);
    }

    [Theory]
    [InlineData("task")]
    [InlineData("project")]
    [InlineData("mode")]
    public async Task Error_reply_throws_with_text_preserved(string opName)
    {
        var stream = ScriptedStream.FromReplies(ErrorReply);
        await using var client = ClientWith(stream);

        var ex = await Assert.ThrowsAsync<BoincRpcException>(() => PerformOp(client, opName));

        Assert.Equal("some text", ex.ErrorText);
    }

    [Theory]
    [InlineData("task")]
    [InlineData("project")]
    [InlineData("mode")]
    public async Task Unauthorized_reply_throws(string opName)
    {
        var stream = ScriptedStream.FromReplies(UnauthorizedReply);
        await using var client = ClientWith(stream);

        await Assert.ThrowsAsync<BoincUnauthorizedException>(() => PerformOp(client, opName));
    }
}
