namespace Lattice.App.ViewModels;

/// <summary>
/// Reconciliation identity of a task row: hostId + BOINC result name
/// (unique per host; spec §6 row-key definition). Value equality drives
/// Reconcile.diff's survivor detection.
/// </summary>
public readonly record struct TaskRowKey(Guid HostId, string Name);
