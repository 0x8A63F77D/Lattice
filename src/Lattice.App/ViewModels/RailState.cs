using Lattice.Core;

namespace Lattice.App.ViewModels;

/// <summary>The five rail visuals from the design package (docs/design/m2 §App shell).</summary>
public enum RailState
{
    Connecting,
    Connected,
    Retrying,
    Unreachable,
    AuthFailed,
}

/// <summary>
/// Projects Core's seven-state lifecycle onto the five rail visuals.
/// Unreachable is a UI tier of Retrying: attempt >= 4 means the backoff has
/// reached the 8 s tier (≈15 s of continuous failure). Recorded in spec §9.
/// </summary>
public static class RailStateProjection
{
    public static RailState From(ConnectionStatus status) => status.State switch
    {
        HostConnectionState.Connected => RailState.Connected,
        HostConnectionState.Retrying when status.Attempt >= 4 => RailState.Unreachable,
        HostConnectionState.Retrying => RailState.Retrying,
        HostConnectionState.AuthFailed => RailState.AuthFailed,
        HostConnectionState.Disconnected or HostConnectionState.Connecting
            or HostConnectionState.Authorizing or HostConnectionState.FetchingState => RailState.Connecting,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status.State,
            "HostConnectionState grew — extend the rail projection and spec §9."),
    };
}
