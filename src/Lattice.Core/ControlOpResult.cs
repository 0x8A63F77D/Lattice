using Lattice.Boinc.GuiRpc;

namespace Lattice.Core;

/// <summary>
/// The outcome category of a single control operation. Every category except
/// <see cref="Succeeded"/> is derived from the failing exception's TYPE only
/// (never its message text) — the same discipline as <c>HostMonitor.Classify</c>.
/// </summary>
public enum ControlOpOutcome
{
    /// <summary>The op RPC returned &lt;success/&gt;.</summary>
    Succeeded,
    /// <summary>The connection failed or the host is no longer registered.</summary>
    Unreachable,
    /// <summary>The daemon rejected the password or returned &lt;unauthorized/&gt;.</summary>
    AuthFailed,
    /// <summary>The daemon returned an &lt;error&gt; reply (text is display-only).</summary>
    DaemonError,
    /// <summary>The caller's token cancelled the op (before or during execution).</summary>
    Canceled,
}

/// <summary>
/// The result of one <see cref="HostControlService"/> control operation. Control ops
/// never throw — every path, cancellation included, produces one of these.
/// <paramref name="Error"/> is a display-only string (from the daemon or the transport);
/// it is never parsed or branched on.
/// </summary>
public sealed record ControlOpResult(ControlOpOutcome Outcome, string? Error)
{
    /// <summary>The single success value (no error text).</summary>
    public static ControlOpResult Success { get; } = new(ControlOpOutcome.Succeeded, null);

    /// <summary>
    /// Classifies a failing operation by exception TYPE only (mirrors
    /// <c>HostMonitor.Classify</c>): <see cref="OperationCanceledException"/> →
    /// <see cref="ControlOpOutcome.Canceled"/>; <see cref="BoincUnauthorizedException"/> →
    /// <see cref="ControlOpOutcome.AuthFailed"/>; <see cref="BoincConnectionException"/> →
    /// <see cref="ControlOpOutcome.Unreachable"/>; <see cref="BoincRpcException"/> →
    /// <see cref="ControlOpOutcome.DaemonError"/>; anything else →
    /// <see cref="ControlOpOutcome.Unreachable"/> (an unexpected failure is treated as a
    /// dead connection, the same fallback the monitor uses).
    /// </summary>
    public static ControlOpResult FromException(Exception ex) => ex switch
    {
        OperationCanceledException => new(ControlOpOutcome.Canceled, null),
        BoincUnauthorizedException => new(ControlOpOutcome.AuthFailed, ex.Message),
        BoincConnectionException => new(ControlOpOutcome.Unreachable, ex.Message),
        BoincRpcException => new(ControlOpOutcome.DaemonError, ex.Message),
        _ => new(ControlOpOutcome.Unreachable, ex.Message),
    };
}
