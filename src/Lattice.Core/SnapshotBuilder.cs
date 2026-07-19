using Lattice.Boinc.GuiRpc;

namespace Lattice.Core;

/// <summary>
/// Pure derivation of a <see cref="HostSnapshot"/> from raw RPC models.
/// All M2 business rules live here: the Application-column join, the
/// elapsed-column rule, deadline-at-risk, and the transfer tri-state.
/// </summary>
public static class SnapshotBuilder
{
    /// <summary>
    /// Builds a snapshot. <paramref name="now"/> anchors deadline and retry-window checks.
    /// <paramref name="projectStatuses"/> is the tick-fresh get_project_status list — the
    /// authoritative project source (design DI-5): it carries the same fields as
    /// <paramref name="state"/>.Projects but reflects suspends/detaches immediately, so
    /// project rows come from it, not from the cached state.
    /// </summary>
    public static HostSnapshot Build(
        Guid hostId,
        string hostName,
        DateTimeOffset now,
        CcState state,
        CcStatus ccStatus,
        IReadOnlyList<Project> projectStatuses,
        IReadOnlyList<Result> results,
        IReadOnlyList<FileTransfer> transfers)
    {
        Dictionary<string, Workunit> workunits = [];
        foreach (Workunit w in state.Workunits)
            workunits.TryAdd(w.Name, w);
        Dictionary<string, App> apps = [];
        foreach (App a in state.Apps)
            apps.TryAdd(a.Name, a);
        // Name-join dictionary: fresh status entries first (they win on shared URLs),
        // cached state entries fill any gap — a same-tick straggler result from a
        // just-detached project must still resolve its display name.
        Dictionary<string, Project> projects = [];
        foreach (Project p in projectStatuses)
            projects.TryAdd(p.MasterUrl, p);
        foreach (Project p in state.Projects)
            projects.TryAdd(p.MasterUrl, p);

        List<TaskSnapshot> tasks = new(results.Count);
        foreach (Result r in results)
        {
            // Application = WorkunitName → Workunit.AppName → App.UserFriendlyName,
            // falling back to the internal app name, then "".
            string applicationName = "";
            if (workunits.TryGetValue(r.WorkunitName, out Workunit? wu))
                applicationName = apps.TryGetValue(wu.AppName, out App? app) && app.UserFriendlyName.Length > 0
                    ? app.UserFriendlyName
                    : wu.AppName;

            string projectName = projects.TryGetValue(r.ProjectUrl, out Project? proj) && proj.ProjectName.Length > 0
                ? proj.ProjectName
                : r.ProjectUrl;

            // Elapsed-column rule: live elapsed while running, final elapsed otherwise.
            double elapsed = r.ActiveTask?.ElapsedTime ?? r.FinalElapsedTime;

            bool atRisk = r.ReportDeadline is { } deadline
                && !r.ReadyToReport
                && now + TimeSpan.FromSeconds(r.EstimatedCpuTimeRemaining) > deadline;

            tasks.Add(new TaskSnapshot(r, projectName, applicationName, elapsed, atRisk));
        }

        List<TransferSnapshot> transferSnapshots = new(transfers.Count);
        foreach (FileTransfer t in transfers)
        {
            string projectName = t.ProjectName.Length > 0
                ? t.ProjectName
                : projects.TryGetValue(t.ProjectUrl, out Project? proj) && proj.ProjectName.Length > 0
                    ? proj.ProjectName
                    : t.ProjectUrl;

            TransferUiState uiState = t.XferActive
                ? TransferUiState.Active
                : t.NextRequestTime is { } next && next > now
                    ? TransferUiState.Retrying
                    : TransferUiState.Queued;

            transferSnapshots.Add(new TransferSnapshot(t, projectName, uiState));
        }

        List<ProjectSnapshot> projectSnapshots = new(projectStatuses.Count);
        foreach (Project p in projectStatuses)
            projectSnapshots.Add(new ProjectSnapshot(p, results.Count(r => r.ProjectUrl == p.MasterUrl)));

        return new HostSnapshot(hostId, hostName, now, ccStatus, tasks, transferSnapshots, projectSnapshots);
    }
}
