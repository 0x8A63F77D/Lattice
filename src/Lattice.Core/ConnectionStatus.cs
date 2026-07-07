using Lattice.Boinc.GuiRpc;

namespace Lattice.Core;

/// <summary>
/// Connection lifecycle of one host. UI mapping: Connecting/Authorizing/FetchingState
/// all render as "Connecting…" (FetchingState may show "Fetching state from {host}…");
/// Retrying with Attempt >= 5 renders as "Unreachable"; AuthFailed is terminal until
/// the host's credentials are updated.
/// </summary>
public enum HostConnectionState
{
    /// <summary>Not started, or stopped by disposal.</summary>
    Disconnected,
    /// <summary>Opening the TCP connection.</summary>
    Connecting,
    /// <summary>Running the auth1/auth2 handshake (skipped instantly for empty passwords).</summary>
    Authorizing,
    /// <summary>Fetching exchange_versions and the full get_state join tables.</summary>
    FetchingState,
    /// <summary>Polling steadily; snapshots flow.</summary>
    Connected,
    /// <summary>Waiting out an exponential backoff before reconnecting. Never gives up.</summary>
    Retrying,
    /// <summary>The daemon refused the password. Terminal until UpdateConfig.</summary>
    AuthFailed,
}

/// <summary>
/// Immutable status raised once per state transition. Core does not tick countdowns;
/// the UI derives "Retrying in Ns" itself, once per second, from <see cref="NextAttemptAt"/>.
/// </summary>
public sealed record ConnectionStatus(
    Guid HostId,
    HostConnectionState State,
    int Attempt,
    DateTimeOffset? NextAttemptAt,
    string? LastError,
    VersionInfo? DaemonVersion);
