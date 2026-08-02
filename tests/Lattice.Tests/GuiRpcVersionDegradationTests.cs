using Lattice.Boinc.GuiRpc;
using Xunit;

namespace Lattice.Tests;

/// <summary>
/// What a consumer sees when it points this package at a daemon older than its target.
/// The protocol carries no version negotiation, so the reference source offers exactly
/// two mechanisms and these tests pin both: the version from exchange_versions (which
/// callers gate newer ops on) and the reply an unknown op produces.
/// </summary>
public class GuiRpcVersionDegradationTests
{
    private static BoincGuiRpcClient ClientWith(params string[] replies) =>
        new(new RpcConnection(ScriptedStream.FromReplies(replies)));

    [Fact]
    public async Task Unknown_op_surfaces_as_an_rpc_exception_with_the_daemon_text_verbatim()
    {
        // client/gui_rpc_server_ops.cpp falls through its dispatch table to
        // "<error>unrecognized op: %s</error>" with the request tag interpolated. That
        // is a typed failure, not a parser crash — but note it is structurally
        // indistinguishable from any other <error>, which is why version gating (below)
        // is the mechanism and error text is never branched on.
        await using var client = ClientWith(
            "<boinc_gui_rpc_reply>\n<error>unrecognized op: get_statistics</error>\n</boinc_gui_rpc_reply>");

        var ex = await Assert.ThrowsAsync<BoincRpcException>(() => client.GetStatisticsAsync());

        Assert.Equal("unrecognized op: get_statistics", ex.ErrorText);
    }

    [Fact]
    public async Task Unknown_op_leaves_the_connection_usable()
    {
        // The daemon keeps serving after an unrecognized op, so a consumer that probes
        // an optional RPC must be able to carry on with the ops it does have.
        await using var client = ClientWith(
            "<boinc_gui_rpc_reply>\n<error>unrecognized op: get_statistics</error>\n</boinc_gui_rpc_reply>",
            "<boinc_gui_rpc_reply>\n<cc_status>\n<task_mode>2</task_mode>\n</cc_status>\n</boinc_gui_rpc_reply>");

        await Assert.ThrowsAsync<BoincRpcException>(() => client.GetStatisticsAsync());
        CcStatus status = await client.GetCcStatusAsync();

        Assert.Equal(RunMode.Auto, status.TaskMode);
    }

    [Fact]
    public async Task Old_daemon_version_is_reported_verbatim_for_gating()
    {
        await using var client = ClientWith(
            "<boinc_gui_rpc_reply>\n<server_version>\n<major>7</major>\n<minor>16</minor>\n" +
            "<release>20</release>\n</server_version>\n</boinc_gui_rpc_reply>");

        VersionInfo version = await client.ExchangeVersionsAsync();

        Assert.Equal(new VersionInfo(7, 16, 20), version);
        Assert.Equal(version, client.DaemonVersion);
        Assert.Equal("7.16.20", version.ToString());
    }

    [Fact]
    public async Task Missing_server_version_container_throws_rather_than_reporting_0_0_0()
    {
        // handle_exchange_versions prints <server_version> unconditionally, so its
        // absence is contract-breaking. Reading it as 0.0.0 would be worse than an
        // error: a consumer gating on DaemonVersion would silently disable every
        // version-gated op against a daemon that is in fact current.
        await using var client = ClientWith("<boinc_gui_rpc_reply>\n</boinc_gui_rpc_reply>");

        var ex = await Assert.ThrowsAsync<BoincProtocolException>(() => client.ExchangeVersionsAsync());

        Assert.Contains("<server_version>", ex.Message);
        Assert.Null(client.DaemonVersion);
    }

    [Fact]
    public async Task Daemon_too_old_to_know_exchange_versions_leaves_the_version_unknown()
    {
        // The op predates BOINC 6, but a consumer must still get a typed failure and an
        // unset DaemonVersion rather than a fabricated one.
        await using var client = ClientWith(
            "<boinc_gui_rpc_reply>\n<error>unrecognized op: exchange_versions</error>\n</boinc_gui_rpc_reply>");

        await Assert.ThrowsAsync<BoincRpcException>(() => client.ExchangeVersionsAsync());

        Assert.Null(client.DaemonVersion);
    }

    [Fact]
    public async Task Partial_server_version_fills_absent_components_with_zero()
    {
        // Distinct from the missing-container case: the container IS present, so the
        // daemon answered the op; a component we cannot read is a field-level default.
        await using var client = ClientWith(
            "<boinc_gui_rpc_reply>\n<server_version>\n<major>8</major>\n</server_version>\n" +
            "</boinc_gui_rpc_reply>");

        VersionInfo version = await client.ExchangeVersionsAsync();

        Assert.Equal(new VersionInfo(8, 0, 0), version);
    }

    [Fact]
    public async Task Newer_daemon_fields_inside_the_version_container_are_ignored()
    {
        await using var client = ClientWith(
            "<boinc_gui_rpc_reply>\n<server_version>\n<major>9</major>\n<minor>1</minor>\n" +
            "<release>2</release>\n<build>4567</build>\n</server_version>\n</boinc_gui_rpc_reply>");

        Assert.Equal(new VersionInfo(9, 1, 2), await client.ExchangeVersionsAsync());
    }
}
