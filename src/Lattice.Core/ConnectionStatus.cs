using Lattice.Boinc.GuiRpc;

namespace Lattice.Core;

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
