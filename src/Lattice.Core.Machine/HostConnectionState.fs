namespace Lattice.Core

/// Connection lifecycle of one host. UI mapping: Connecting/Authorizing/FetchingState
/// all render as "Connecting…" (FetchingState may show "Fetching state from {host}…");
/// Retrying with Attempt >= 5 renders as "Unreachable"; AuthFailed is terminal until
/// the host's credentials are updated.
type HostConnectionState =
    /// Not started, or stopped by disposal.
    | Disconnected = 0
    /// Opening the TCP connection.
    | Connecting = 1
    /// Running the auth1/auth2 handshake (skipped instantly for empty passwords).
    | Authorizing = 2
    /// Fetching exchange_versions and the full get_state join tables.
    | FetchingState = 3
    /// Polling steadily; snapshots flow.
    | Connected = 4
    /// Waiting out an exponential backoff before reconnecting. Never gives up.
    | Retrying = 5
    /// The daemon refused the password. Terminal until UpdateConfig.
    | AuthFailed = 6
