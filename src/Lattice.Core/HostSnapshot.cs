using Lattice.Boinc.GuiRpc;

namespace Lattice.Core;

/// <summary>
/// Immutable view of one host at one poll tick. Replaced wholesale on every tick;
/// consumers bind to the latest instance and never see partial mutation.
/// </summary>
public sealed record HostSnapshot(
    Guid HostId,
    string HostName,
    DateTimeOffset Timestamp,
    CcStatus CcStatus,
    IReadOnlyList<TaskSnapshot> Tasks,
    IReadOnlyList<TransferSnapshot> Transfers,
    IReadOnlyList<ProjectSnapshot> Projects);

/// <summary>A task with the view-ready values derived from the cached get_state join tables.</summary>
public sealed record TaskSnapshot(
    Result Result,
    string ProjectName,
    string ApplicationName,
    double ElapsedSeconds,
    bool IsDeadlineAtRisk);

/// <summary>How the Transfers view renders a row (spec: docs/design/m2/README.md).</summary>
public enum TransferUiState
{
    /// <summary>Socket open, live speed is meaningful.</summary>
    Active,
    /// <summary>Waiting for a scheduled retry; countdown from NextRequestTime.</summary>
    Retrying,
    /// <summary>Neither transferring nor scheduled — waiting its turn.</summary>
    Queued,
}

/// <summary>A file transfer with its derived UI state and display project name.</summary>
public sealed record TransferSnapshot(
    FileTransfer Transfer,
    string ProjectName,
    TransferUiState UiState);

/// <summary>A project with this host's task count for nav and child-row display.</summary>
public sealed record ProjectSnapshot(Project Project, int TaskCount);
