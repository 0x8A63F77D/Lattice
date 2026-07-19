using Lattice.Boinc.GuiRpc;
using Lattice.Core;
using Xunit;

namespace Lattice.Tests;

public class SnapshotBuilderTests
{
    private static readonly Guid HostId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_752_000_000);

    // projectStatuses defaults to the state's own project list: most tests care about
    // the join tables, not the DI-5 freshness split, and in a steady tick the two lists
    // describe the same attachments anyway. Tests exercising the split pass both explicitly.
    private static HostSnapshot Build(
        CcState? state = null,
        IReadOnlyList<Result>? results = null,
        IReadOnlyList<FileTransfer>? transfers = null,
        IReadOnlyList<Project>? projectStatuses = null)
    {
        CcState ccState = state ?? TestData.MakeState();
        return SnapshotBuilder.Build(HostId, "host-1", Now, ccState,
            FakeGuiRpcClient.DefaultStatus, projectStatuses ?? ccState.Projects, results ?? [], transfers ?? []);
    }

    [Fact]
    public void Joins_application_name_through_workunit_and_app()
    {
        CcState state = TestData.MakeState(
            apps: [new App("einstein_O3", "Einstein@Home O3 search")],
            workunits: [new Workunit("wu_1", "einstein_O3", 0)]);
        HostSnapshot snapshot = Build(state, results: [TestData.MakeResult(wuName: "wu_1")]);
        Assert.Equal("Einstein@Home O3 search", snapshot.Tasks[0].ApplicationName);
    }

    [Fact]
    public void Application_name_falls_back_to_app_name_then_empty()
    {
        CcState state = TestData.MakeState(
            apps: [new App("einstein_O3", "")],
            workunits: [new Workunit("wu_1", "einstein_O3", 0)]);
        Assert.Equal("einstein_O3",
            Build(state, results: [TestData.MakeResult(wuName: "wu_1")]).Tasks[0].ApplicationName);
        Assert.Equal("",
            Build(results: [TestData.MakeResult(wuName: "unknown")]).Tasks[0].ApplicationName);
    }

    [Fact]
    public void Joins_project_name_with_url_fallback()
    {
        CcState state = TestData.MakeState(projects: [TestData.MakeProject("https://example.org/", "Example")]);
        Assert.Equal("Example", Build(state, results: [TestData.MakeResult()]).Tasks[0].ProjectName);
        Assert.Equal("https://other.org/",
            Build(results: [TestData.MakeResult(projectUrl: "https://other.org/")]).Tasks[0].ProjectName);
    }

    [Fact]
    public void Task_project_name_falls_back_to_url_when_joined_project_name_is_empty()
    {
        // Boundary pin for SnapshotBuilder.cs:43 (`proj.ProjectName.Length > 0`).
        // A project exists in state for the result's URL but carries an EMPTY ProjectName
        // (BOINC emits this before the project's account info arrives). An empty name is not
        // a usable display value, so the Application/Project column must fall back to the
        // master URL — not render blank. The strict `> 0` is what makes empty fall through;
        // `>= 0` would (wrongly) treat "" as a real name.
        CcState state = TestData.MakeState(projects: [TestData.MakeProject("https://example.org/", name: "")]);
        HostSnapshot snapshot = Build(state, results: [TestData.MakeResult(projectUrl: "https://example.org/")]);
        Assert.Equal("https://example.org/", snapshot.Tasks[0].ProjectName);
    }

    [Fact]
    public void Elapsed_uses_active_task_then_final_elapsed()
    {
        Result active = TestData.MakeResult(activeTask: new ActiveTask(1, 0.5, 100, 123), finalElapsed: 999);
        Result finished = TestData.MakeResult(finalElapsed: 456);
        HostSnapshot snapshot = Build(results: [active, finished]);
        Assert.Equal(123, snapshot.Tasks[0].ElapsedSeconds);
        Assert.Equal(456, snapshot.Tasks[1].ElapsedSeconds);
    }

    [Fact]
    public void Deadline_at_risk_when_remaining_overshoots_deadline()
    {
        Result atRisk = TestData.MakeResult(deadline: Now.AddHours(1), estRemaining: 7200);
        Result safe = TestData.MakeResult(deadline: Now.AddHours(1), estRemaining: 600);
        Result readyToReport = TestData.MakeResult(deadline: Now.AddHours(1), estRemaining: 7200, readyToReport: true);
        Result noDeadline = TestData.MakeResult(estRemaining: 7200);
        HostSnapshot snapshot = Build(results: [atRisk, safe, readyToReport, noDeadline]);
        Assert.True(snapshot.Tasks[0].IsDeadlineAtRisk);
        Assert.False(snapshot.Tasks[1].IsDeadlineAtRisk);
        Assert.False(snapshot.Tasks[2].IsDeadlineAtRisk);
        Assert.False(snapshot.Tasks[3].IsDeadlineAtRisk);
    }

    [Fact]
    public void Deadline_at_risk_is_false_when_projected_finish_exactly_meets_deadline()
    {
        // Boundary pin for SnapshotBuilder.cs:52 (`now + remaining > deadline`).
        // A projected finish (now + EstimatedCpuTimeRemaining) landing EXACTLY on the
        // deadline is on-time, not at-risk: the rule is strictly-after. `estRemaining`
        // is chosen so `Now + FromSeconds(estRemaining)` equals the deadline instant to the
        // tick, exercising the `>` vs `>=` tie. `>=` would (wrongly) flag it at-risk.
        Result exact = TestData.MakeResult(deadline: Now.AddSeconds(3600), estRemaining: 3600);
        HostSnapshot snapshot = Build(results: [exact]);
        Assert.False(snapshot.Tasks[0].IsDeadlineAtRisk);
    }

    [Fact]
    public void Transfer_state_is_active_retrying_or_queued()
    {
        FileTransfer active = TestData.MakeTransfer(xferActive: true);
        FileTransfer retrying = TestData.MakeTransfer(nextRequest: Now.AddMinutes(3));
        FileTransfer queuedPast = TestData.MakeTransfer(nextRequest: Now.AddMinutes(-3));
        FileTransfer queuedNone = TestData.MakeTransfer();
        HostSnapshot snapshot = Build(transfers: [active, retrying, queuedPast, queuedNone]);
        Assert.Equal(TransferUiState.Active, snapshot.Transfers[0].UiState);
        Assert.Equal(TransferUiState.Retrying, snapshot.Transfers[1].UiState);
        Assert.Equal(TransferUiState.Queued, snapshot.Transfers[2].UiState);
        Assert.Equal(TransferUiState.Queued, snapshot.Transfers[3].UiState);
    }

    [Fact]
    public void Transfer_project_name_prefers_own_field_then_join_then_url()
    {
        CcState state = TestData.MakeState(projects: [TestData.MakeProject("https://example.org/", "Example")]);
        Assert.Equal("Direct",
            Build(state, transfers: [TestData.MakeTransfer(projectName: "Direct")]).Transfers[0].ProjectName);
        Assert.Equal("Example",
            Build(state, transfers: [TestData.MakeTransfer()]).Transfers[0].ProjectName);
        Assert.Equal("https://x.org/",
            Build(transfers: [TestData.MakeTransfer(projectUrl: "https://x.org/")]).Transfers[0].ProjectName);
    }

    [Fact]
    public void Transfer_project_name_falls_back_to_url_when_own_and_joined_names_are_empty()
    {
        // Boundary pin for SnapshotBuilder.cs:62 (inner `proj.ProjectName.Length > 0` on the
        // transfer join). A transfer with no own ProjectName whose joined project also carries
        // an empty ProjectName must fall back to the master URL, not render blank. This is the
        // transfer-branch twin of the L43 gap; the strict `> 0` makes the empty joined name
        // fall through to the URL, whereas `>= 0` would surface "".
        CcState state = TestData.MakeState(projects: [TestData.MakeProject("https://example.org/", name: "")]);
        HostSnapshot snapshot = Build(state,
            transfers: [TestData.MakeTransfer(projectUrl: "https://example.org/", projectName: "")]);
        Assert.Equal("https://example.org/", snapshot.Transfers[0].ProjectName);
    }

    [Fact]
    public void Project_snapshots_count_tasks_per_project()
    {
        CcState state = TestData.MakeState(projects: [
            TestData.MakeProject("https://a.org/", "A"),
            TestData.MakeProject("https://b.org/", "B")]);
        HostSnapshot snapshot = Build(state, results: [
            TestData.MakeResult(projectUrl: "https://a.org/"),
            TestData.MakeResult(projectUrl: "https://a.org/"),
            TestData.MakeResult(projectUrl: "https://b.org/")]);
        Assert.Equal(2, snapshot.Projects[0].TaskCount);
        Assert.Equal(1, snapshot.Projects[1].TaskCount);
        Assert.Equal("A", snapshot.Projects[0].Project.ProjectName);
    }

    [Fact]
    public void Duplicate_project_and_workunit_keys_do_not_throw_and_first_entry_wins()
    {
        CcState state = TestData.MakeState(
            projects: [
                TestData.MakeProject("https://dup.org/", "First"),
                TestData.MakeProject("https://dup.org/", "Second"),
            ],
            apps: [new App("app", "App")],
            workunits: [
                new Workunit("wu_dup", "app", 0),
                new Workunit("wu_dup", "app", 0),
            ]);
        HostSnapshot snapshot = Build(state, results: [TestData.MakeResult(wuName: "wu_dup", projectUrl: "https://dup.org/")]);

        Assert.Equal("First", snapshot.Tasks[0].ProjectName);
        Assert.Equal("App", snapshot.Tasks[0].ApplicationName);
        Assert.Equal(2, snapshot.Projects.Count);
        Assert.Equal("First", snapshot.Projects[0].Project.ProjectName);
        Assert.Equal("Second", snapshot.Projects[1].Project.ProjectName);
    }

    [Fact]
    public void Project_rows_come_from_the_status_list_not_the_cached_state()
    {
        // DI-5: a suspend flipped since the last get_state must render suspended. The
        // cached state still says active; the tick-fresh status list says suspended —
        // the status list wins because it is the ProjectSnapshot source, not a join input.
        CcState state = TestData.MakeState(projects: [TestData.MakeProject("https://example.org/", "Example")]);
        HostSnapshot snapshot = Build(state,
            projectStatuses: [TestData.MakeProject("https://example.org/", "Example") with { SuspendedViaGui = true }]);
        ProjectSnapshot row = Assert.Single(snapshot.Projects);
        Assert.True(row.Project.SuspendedViaGui);
    }

    [Fact]
    public void Project_detached_since_last_state_disappears_but_still_names_its_tasks()
    {
        // A project missing from the fresh status list (detached) must drop its row
        // immediately, while a same-tick straggler result still shows the cached
        // project name — the name join reads the merged dictionary, not the row source.
        CcState state = TestData.MakeState(projects: [TestData.MakeProject("https://example.org/", "Example")]);
        HostSnapshot snapshot = Build(state,
            results: [TestData.MakeResult(projectUrl: "https://example.org/")],
            projectStatuses: []);
        Assert.Empty(snapshot.Projects);
        Assert.Equal("Example", snapshot.Tasks[0].ProjectName);
    }

    [Fact]
    public void Project_attached_since_last_state_appears_and_names_its_tasks()
    {
        // The mirror case: a status-list project the cached state has never seen (attached
        // externally) must both get a row and win the name join for its tasks.
        HostSnapshot snapshot = Build(TestData.MakeState(),
            results: [TestData.MakeResult(projectUrl: "https://new.org/")],
            projectStatuses: [TestData.MakeProject("https://new.org/", "Fresh")]);
        ProjectSnapshot row = Assert.Single(snapshot.Projects);
        Assert.Equal("Fresh", row.Project.ProjectName);
        Assert.Equal(1, row.TaskCount);
        Assert.Equal("Fresh", snapshot.Tasks[0].ProjectName);
    }

    [Fact]
    public void Name_join_prefers_the_fresh_status_entry_over_the_cached_state_entry()
    {
        // Same URL in both sources with different names (project renamed server-side):
        // the merged dictionary must surface the FRESH name on task rows.
        CcState state = TestData.MakeState(projects: [TestData.MakeProject("https://example.org/", "Stale")]);
        HostSnapshot snapshot = Build(state,
            results: [TestData.MakeResult(projectUrl: "https://example.org/")],
            projectStatuses: [TestData.MakeProject("https://example.org/", "Renamed")]);
        Assert.Equal("Renamed", snapshot.Tasks[0].ProjectName);
    }

    [Fact]
    public void Snapshot_carries_identity_and_timestamp()
    {
        HostSnapshot snapshot = Build();
        Assert.Equal(HostId, snapshot.HostId);
        Assert.Equal("host-1", snapshot.HostName);
        Assert.Equal(Now, snapshot.Timestamp);
        Assert.Equal(FakeGuiRpcClient.DefaultStatus, snapshot.CcStatus);
    }
}
