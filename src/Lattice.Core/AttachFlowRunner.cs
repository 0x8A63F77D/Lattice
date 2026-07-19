using Lattice.Boinc.GuiRpc;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;

namespace Lattice.Core;

/// <summary>Terminal category of one attach flow, for the App's dialog surface.</summary>
public enum AttachFlowOutcome
{
    /// <summary>
    /// The daemon accepted the attach and created the project entry. "Attached",
    /// NOT "verified" (design 2.3): the daemon does not validate the authenticator
    /// at attach time — a bad account key surfaces later in the event log via the
    /// first scheduler RPC.
    /// </summary>
    Attached,
    /// <summary>The project rejected the account lookup (bad credentials, unknown account…).</summary>
    LookupFailed,
    /// <summary>The daemon rejected the attach request itself (e.g. already attached).</summary>
    AttachFailed,
    /// <summary>A stage exceeded the poll cap (60 polls at 1 s) without settling.</summary>
    TimedOut,
    /// <summary>Transport/auth failure, or the host is no longer registered.</summary>
    Faulted,
    /// <summary>The caller's CancellationToken fired mid-flow.</summary>
    Canceled,
}

/// <summary>
/// Terminal result of one attach flow. <paramref name="Messages"/> carries the
/// daemon's attach messages (success greetings, or the failure explanations on
/// <see cref="AttachFlowOutcome.AttachFailed"/>); <paramref name="Error"/> is
/// display-only text — never branch on it.
/// </summary>
public sealed record AttachFlowResult(
    AttachFlowOutcome Outcome, IReadOnlyList<string> Messages, string? Error);

/// <summary>
/// Interprets <see cref="AttachMachine"/> commands against one short-lived
/// <see cref="IGuiRpcClient"/> connection — the I/O shell of the attach flow,
/// mirroring how HostMonitor.RunAsync drives HostMachine: the machine decides,
/// this class only connects, calls RPCs, delays, and feeds inputs back.
/// </summary>
/// <remarks>
/// Stated invariants (async discipline rule):
/// <list type="bullet">
/// <item>(I-AF1) The whole flow — lookup request, its polls, attach, its poll —
/// runs on ONE connection: the daemon's lookup/attach state is per-connection
/// (grc.lookup_account_op), so a poll on a fresh connection would never settle.</item>
/// <item>(I-AF2) The client is disposed on every path (<c>await using</c>),
/// including cancellation and fault paths.</item>
/// <item>(I-AF3) RunAsync never throws: every path returns an
/// <see cref="AttachFlowResult"/> (cancellation included).</item>
/// <item>(I-AF4) The host's config is read fresh from the registry at flow start;
/// an unknown id fails fast (host removed between dialog open and submit).</item>
/// <item>(I-AF5) The 1 s poll cadence lives here (via <see cref="TimeProvider"/>);
/// the poll COUNT cap lives in the machine. Exceptions are classified by type
/// only: <see cref="BoincRpcException"/> during a poll RPC is that stage's
/// failure reply (design 1.2), everything else non-cancellation folds to a
/// Faulted input and the machine settles the flow.</item>
/// </list>
/// Lane note: control ops serialize per host on HostControlService's lane (PR D,
/// in flight in parallel). Until both are merged this runner opens its own side
/// connection exactly like HostMonitorManager.TestConnectionAsync; routing the
/// flow body through the service's lane hook is the follow-up integration.
/// </remarks>
public sealed class AttachFlowRunner
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly HostRegistry _registry;
    private readonly HostMonitorManager _monitors;
    private readonly Func<IGuiRpcClient> _clientFactory;
    private readonly TimeProvider _time;

    /// <summary>Creates a runner over the app's registry, monitor manager, and client factory.</summary>
    public AttachFlowRunner(HostRegistry registry, HostMonitorManager monitors,
                            Func<IGuiRpcClient> clientFactory, TimeProvider timeProvider)
    {
        _registry = registry;
        _monitors = monitors;
        _clientFactory = clientFactory;
        _time = timeProvider;
    }

    /// <summary>
    /// Runs one attach flow against the given host. Stage transitions are reported
    /// on <paramref name="progress"/> (from the RPC thread; the caller marshals).
    /// Never throws (I-AF3).
    /// </summary>
    public async Task<AttachFlowResult> RunAsync(
        Guid hostId, AttachMachine.AttachRequest request,
        IProgress<AttachMachine.Stage>? progress = null, CancellationToken ct = default)
    {
        HostConfig? config = _registry.Hosts.FirstOrDefault(h => h.Id == hostId);
        if (config is null)
            return new(AttachFlowOutcome.Faulted, [], "The host is no longer registered.");

        try
        {
            await using IGuiRpcClient client = _clientFactory();
            await client.ConnectAsync(config.Address, config.Port, ct).ConfigureAwait(false);
            if (config.Password.Length > 0
                && !await client.AuthorizeAsync(config.Password, ct).ConfigureAwait(false))
                return new(AttachFlowOutcome.Faulted, [], "The host refused the password.");

            AttachFlowResult result = await DriveAsync(client, request, progress, ct).ConfigureAwait(false);
            if (result.Outcome == AttachFlowOutcome.Attached)
                // Converge the read path within ~1 tick; no optimistic snapshot
                // mutation — HostSnapshot stays single-writer (the monitor).
                _monitors.Monitors.FirstOrDefault(m => m.HostId == hostId)?.RequestRefresh();
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new(AttachFlowOutcome.Canceled, [], null);
        }
        catch (Exception ex)
        {
            // Connect/auth-leg failures (the flow itself classifies inside DriveAsync).
            return new(AttachFlowOutcome.Faulted, [], ex.Message);
        }
    }

    // The interpreter loop, shaped like HostMonitor.RunAsync's: execute the batch,
    // feed the trailing request command's produced Input back (EffectOk when none);
    // a command exception folds to a Faulted input by TYPE only and the machine
    // settles the flow in Done — the loop itself never decides routing.
    private async Task<AttachFlowResult> DriveAsync(
        IGuiRpcClient client, AttachMachine.AttachRequest request,
        IProgress<AttachMachine.Stage>? progress, CancellationToken ct)
    {
        var state = AttachMachine.initial;
        AttachMachine.Input input = AttachMachine.Input.NewStart(request);
        while (true)
        {
            var result = AttachMachine.step(state, input);
            state = result.Item1;
            FSharpList<AttachMachine.Command> commands = result.Item2;
            if (state.Phase is AttachMachine.Phase.Done done)
                return Map(done.Item);
            input = AttachMachine.Input.EffectOk;
            foreach (var command in commands)
            {
                try
                {
                    AttachMachine.Input? produced =
                        await ExecuteAsync(client, command, progress, ct).ConfigureAwait(false);
                    if (produced is not null)
                        input = produced;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    input = AttachMachine.Input.NewFaulted(ex.Message);
                    break; // skip the rest of the batch
                }
            }
        }
    }

    // Executes one command; returns the produced Input for request commands, null
    // for fire-and-forget ones. The BoincRpcException catch on the two poll RPCs is
    // the design-1.2 rule: an <error> reply to a poll means the STAGE failed (-1 is
    // upstream's own generic-failure code), not the connection.
    private async Task<AttachMachine.Input?> ExecuteAsync(
        IGuiRpcClient client, AttachMachine.Command command,
        IProgress<AttachMachine.Stage>? progress, CancellationToken ct)
    {
        switch (command)
        {
            case AttachMachine.Command.Report report:
                progress?.Report(report.Item);
                return null;

            case AttachMachine.Command.SendLookup lookup:
                await client.RequestAccountLookupAsync(lookup.url, lookup.email, lookup.password, ct)
                    .ConfigureAwait(false);
                return AttachMachine.Input.EffectOk;

            case AttachMachine.Command.SendAttach attach:
                await client.RequestProjectAttachAsync(
                        attach.url, attach.authenticator, attach.projectName, attach.email, ct)
                    .ConfigureAwait(false);
                return AttachMachine.Input.EffectOk;

            default:
                if (command.IsPollLookup)
                {
                    await Task.Delay(PollInterval, _time, ct).ConfigureAwait(false);
                    try
                    {
                        AccountLookupReply reply =
                            await client.PollAccountLookupAsync(ct).ConfigureAwait(false);
                        return AttachMachine.Input.NewLookupReply(
                            reply.ErrorNum, reply.ErrorMessage, reply.Authenticator);
                    }
                    catch (BoincRpcException ex)
                    {
                        // ErrorText, not Message: the same verbatim daemon text a
                        // bare-<error> poll body parses into (design 1.2).
                        return AttachMachine.Input.NewLookupReply(-1, ex.ErrorText, "");
                    }
                }

                if (command.IsPollAttach)
                {
                    await Task.Delay(PollInterval, _time, ct).ConfigureAwait(false);
                    try
                    {
                        ProjectAttachReply reply =
                            await client.PollProjectAttachAsync(ct).ConfigureAwait(false);
                        return AttachMachine.Input.NewAttachReply(
                            reply.ErrorNum, ListModule.OfSeq(reply.Messages));
                    }
                    catch (BoincRpcException ex)
                    {
                        return AttachMachine.Input.NewAttachReply(
                            -1, ListModule.OfSeq([ex.ErrorText]));
                    }
                }

                // Every Command case is handled above; a new case reaching here is an
                // interpreter bug — surface it as a flow fault, never throw (I-AF3).
                return AttachMachine.Input.NewFaulted($"unhandled command {command}");
        }
    }

    private static AttachFlowResult Map(
        FSharpResult<FSharpList<string>, AttachMachine.AttachError> terminal)
    {
        if (terminal.IsOk)
            return new(AttachFlowOutcome.Attached, [.. terminal.ResultValue], null);

        return terminal.ErrorValue switch
        {
            AttachMachine.AttachError.LookupFailed f => new(
                AttachFlowOutcome.LookupFailed, [],
                f.message.Length > 0 ? f.message : $"Account lookup failed (error {f.errorNum})."),
            AttachMachine.AttachError.AttachFailed f => new(
                AttachFlowOutcome.AttachFailed, [.. f.messages],
                $"The daemon rejected the attach (error {f.errorNum})."),
            AttachMachine.AttachError.FlowFaulted f => new(
                AttachFlowOutcome.Faulted, [], f.message),
            AttachMachine.AttachError.TimedOut t => new(
                AttachFlowOutcome.TimedOut, [],
                t.Item.IsLookupStage
                    ? "The project did not answer the account lookup in time."
                    : "The daemon did not report an attach verdict in time."),
            // F# DU subclasses are an open hierarchy to the C# compiler; the F#
            // side is exhaustive, so this arm is unreachable.
            _ => new(AttachFlowOutcome.Faulted, [], "unknown attach error"),
        };
    }
}
