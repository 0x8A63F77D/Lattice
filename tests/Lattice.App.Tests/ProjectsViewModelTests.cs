using Lattice.App.Aggregation;
using Lattice.App.Infrastructure;
using Lattice.App.Localization;
using Lattice.App.Tests.Fakes;
using Lattice.App.ViewModels;
using Lattice.Boinc.GuiRpc;
using Lattice.Core;
using Lattice.Tests;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Lattice.App.Tests;

/// <summary>
/// Fixture mirrors TasksViewModelTests: a RoutingGuiRpcClient hands a distinct
/// fake per host address, the LockingUiDispatcher runs posts inline-but-
/// serialized (two live monitors otherwise race the ObservableCollection), and
/// every settle is a Wait.UntilAsync on an EXPECTED END STATE (row counts /
/// text), never a transient boolean.
/// </summary>
public class ProjectsViewModelTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}.json");
    private readonly Dictionary<string, FakeGuiRpcClient> _fakes = [];
    private HostRegistry _registry = null!;
    private HostMonitorManager _manager = null!;
    private HostStore _store = null!;
    private ManualUiClock _clock = null!;

    public ValueTask InitializeAsync()
    {
        _registry = new HostRegistry(new LatticeConfig(5, []), _path);
        _manager = new HostMonitorManager(_registry, () => new RoutingGuiRpcClient(_fakes), new FakeTimeProvider());
        _store = new HostStore(_registry, _manager, new LockingUiDispatcher());
        _clock = new ManualUiClock();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _manager.DisposeAsync();
        File.Delete(_path);
    }

    private HostConfig AddHost(string address, FakeGuiRpcClient fake)
    {
        var host = TestData.MakeHostConfig(name: address, address: address);
        _fakes[address] = fake;
        _registry.AddHost(host);
        return host;
    }

    private ProjectsViewModel MakeVm() => new(_store, _clock);

    // Projects arrive via get_state (cached once per connection), so a project
    // row needs only the project present in the state — no results required.
    private static Project Proj(string url, string name, double rac = 0, double share = 100) =>
        TestData.MakeProject(url, name, share) with { HostExpavgCredit = rac };

    private static FakeGuiRpcClient FakeWithProjects(params Project[] projects) =>
        new() { OnGetState = () => Task.FromResult(TestData.MakeState(projects: projects)) };

    private static FakeGuiRpcClient FakeWithProject(string url, string name, double rac = 0) =>
        FakeWithProjects(Proj(url, name, rac));

    [Fact]
    public async Task Same_master_url_aggregates_and_expands_to_children()
    {
        AddHost("host-a", FakeWithProject("http://p/", "P", rac: 10));
        AddHost("host-b", FakeWithProject("http://p/", "P", rac: 5));
        var vm = MakeVm();
        _manager.Start();
        await Wait.UntilAsync(() => vm.Rows.Count == 1, "two hosts on one URL collapse to a single parent");

        var parent = vm.Rows[0];
        Assert.True(parent.Data.IsParent);
        Assert.True(parent.Data.ShowChevron);

        vm.ToggleExpandCommand.Execute("http://p/");
        await Wait.UntilAsync(() => vm.Rows.Count == 3, "expanding inserts the two child rows");

        // Parent identity survives the insert (reconciler, no Reset), children
        // in host-name order under it.
        Assert.Same(parent, vm.Rows[0]);
        Assert.False(vm.Rows[1].Data.IsParent);
        Assert.False(vm.Rows[2].Data.IsParent);
        Assert.Equal("host-a", vm.Rows[1].Data.Name);
        Assert.Equal("host-b", vm.Rows[2].Data.Name);
    }

    [Fact]
    public async Task Single_host_registry_is_not_aggregate_presentation()
    {
        // ScopeMachine: a one-host registry has no auto-pin, so Scope stays
        // AllHosts — but the aggregate PRESENTATION (child-row expansion) must
        // key on genuine multi-host, not on Scope.IsAllHosts, or a single-host
        // user wrongly sees multi-host chrome.
        AddHost("host-a", FakeWithProject("http://p/", "P"));
        var vm = MakeVm();
        _manager.Start();
        await Wait.UntilAsync(() => vm.Rows.Count == 1);

        Assert.True(vm.Scope.IsAllHosts);
        Assert.False(vm.IsAllHostsScope);
    }

    [Fact]
    public async Task Single_host_scope_has_no_chevron_and_no_children()
    {
        var hostA = AddHost("host-a", FakeWithProject("http://p/", "P"));
        var vm = MakeVm();
        _manager.Start();
        await Wait.UntilAsync(() => vm.Rows.Count == 1);

        vm.Scope = new ScopeSelection(hostA.Id);
        await Wait.UntilAsync(() => !vm.IsAllHostsScope);

        Assert.False(vm.Rows[0].Data.ShowChevron);

        // Toggling a single-host scope never materializes children (design 2a).
        vm.ToggleExpandCommand.Execute("http://p/");
        Assert.Single(vm.Rows);
        Assert.False(vm.Rows[0].Data.ShowChevron);
    }

    [Fact]
    public async Task Collapse_removes_children_and_preserves_the_parent_holder()
    {
        AddHost("host-a", FakeWithProject("http://p/", "P", rac: 10));
        AddHost("host-b", FakeWithProject("http://p/", "P", rac: 5));
        var vm = MakeVm();
        _manager.Start();
        await Wait.UntilAsync(() => vm.Rows.Count == 1);

        vm.ToggleExpandCommand.Execute("http://p/");
        await Wait.UntilAsync(() => vm.Rows.Count == 3);
        var parent = vm.Rows[0];

        vm.ToggleExpandCommand.Execute("http://p/");
        await Wait.UntilAsync(() => vm.Rows.Count == 1, "collapse removes exactly the children");

        Assert.Same(parent, vm.Rows[0]); // holder identity preserved across the collapse
    }

    [Fact]
    public async Task Expanded_group_shrinks_when_a_host_stops_being_a_row_source()
    {
        // An EXPANDED group must track the row-source set live: when a host
        // drops out of Connected, its stale cached attachment leaves the
        // aggregation (the inScope && isRowSource gate), so its child row
        // vanishes and the parent re-aggregates — while the parent holder
        // identity survives the shrink (in-place Update, no remove+reinsert).
        var fakeA = FakeWithProject("http://p/", "P", rac: 10);
        var fakeB = FakeWithProject("http://p/", "P", rac: 5);
        AddHost("host-a", fakeA);
        var hostB = AddHost("host-b", fakeB);
        var vm = MakeVm();
        _manager.Start();
        await Wait.UntilAsync(() => vm.Rows.Count == 1);

        vm.ToggleExpandCommand.Execute("http://p/");
        await Wait.UntilAsync(() => vm.Rows.Count == 3, "expanding shows both hosts' children");
        var parent = vm.Rows[0];
        Assert.Contains(vm.Rows, r => !r.Data.IsParent && r.Data.Name == "host-b");

        // Before breaking, wait until B's poll on the current connection is
        // observed (Calls log) — TasksViewModelTests' flake lesson: otherwise
        // the break can install itself before B's first tick.
        await Wait.UntilAsync(() => fakeB.Calls.Count(c => c == "get_cc_status") > 0,
            "B's first poll should be observed before the test breaks it");

        // Hold B in the Retrying tier deterministically (same idiom as the
        // Tasks coverage test): its live poll AND every reconnect attempt
        // throw, so it cycles Retrying -> (instant connect failure) ->
        // Retrying, never Connected again — its snapshot stays cached but
        // hidden from the aggregation by the row-source gate.
        fakeB.OnGetCcStatus = () => throw new BoincConnectionException("poll boom");
        fakeB.OnConnect = (_, _) => throw new BoincConnectionException("still down");
        _store.RequestRefresh(hostB.Id);
        await Wait.UntilAsync(() =>
            _store.Hosts.Single(h => h.Config.Id == hostB.Id).Status.State == HostConnectionState.Retrying);
        Assert.NotNull(_store.Hosts.Single(h => h.Config.Id == hostB.Id).Snapshot);

        await Wait.UntilAsync(() => vm.Rows.Count == 2, "B's child row should drop out of the expanded group");

        Assert.Same(parent, vm.Rows[0]); // parent holder survives the shrink
        Assert.True(vm.Rows[0].Data.IsParent);
        Assert.True(vm.Rows[0].Data.IsExpanded, "the group stays expanded across the shrink");
        Assert.False(vm.Rows[1].Data.IsParent);
        Assert.Equal("host-a", vm.Rows[1].Data.Name); // the surviving child
        Assert.DoesNotContain(vm.Rows, r => r.Data.Name == "host-b");
    }

    [Fact]
    public async Task Steady_state_poll_with_unchanged_data_raises_no_collection_events()
    {
        AddHost("host-a", FakeWithProject("http://p/", "P", rac: 10));
        var vm = MakeVm();
        _manager.Start();
        await Wait.UntilAsync(() => vm.Rows.Count == 1);

        var firstRow = vm.Rows[0];
        _clock.Now = _store.Hosts[0].Snapshot!.Timestamp;
        var collectionChanges = 0;
        vm.Rows.CollectionChanged += (_, _) => collectionChanges++;

        _clock.Advance(TimeSpan.FromSeconds(1));
        _clock.Advance(TimeSpan.FromSeconds(1));
        _clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(0, collectionChanges);
        Assert.Same(firstRow, vm.Rows[0]);
    }

    // Display order is now the VIEW's (RowsView), not the source Rows collection —
    // the source stays in reconcile-friendly order. Assert the rendered sequence.
    private static IEnumerable<string> ViewNames(ProjectsViewModel vm) =>
        vm.RowsView.Cast<ProjectRow>().Select(r => r.Data.Name);

    [Fact]
    public async Task Parents_sort_by_aggregate_rac_descending()
    {
        AddHost("host-a", FakeWithProjects(
            Proj("http://low/", "Low", rac: 5),
            Proj("http://high/", "High", rac: 20)));
        var vm = MakeVm();
        _manager.Start();
        await Wait.UntilAsync(() => vm.Rows.Count == 2);

        Assert.Equal(["High", "Low"], ViewNames(vm));
    }

    [Fact]
    public async Task Partial_bar_speaks_of_projects_not_tasks()
    {
        // Codex P3 (PR #46 round 2): the banner copy on the Projects page must
        // say "projects below cover ...", not the Tasks view's "tasks below
        // cover ..." — the shared PartialFmt is user-visibly wrong here.
        // Arrangement stamps TasksViewModelTests' partial-bar baseline:
        // A parks in AuthFailed (unreachable tier), B feeds the grid.
        var fakeA = new FakeGuiRpcClient { OnAuthorize = _ => Task.FromResult(false) };
        var fakeB = FakeWithProject("http://p/", "P", rac: 10);
        var hostA = AddHost("host-a", fakeA);
        var hostB = AddHost("host-b", fakeB);
        var vm = MakeVm();
        _manager.Start();

        await Wait.UntilAsync(() =>
            _store.Hosts.Single(h => h.Config.Id == hostA.Id).Status.State == HostConnectionState.AuthFailed);
        await Wait.UntilAsync(() =>
            _store.Hosts.Single(h => h.Config.Id == hostB.Id).Snapshot is not null);

        // Condition-driven: the store Waits above observe HostStore fields, which
        // are set BEFORE Changed fires — the VM lags the store by one Rebuild.
        await Wait.UntilAsync(
            () => vm.PartialBarText == string.Format(Strings.ProjectsPartialFmt, 1, 2, 1),
            "the Projects partial bar should use the projects-below copy");
        Assert.True(vm.ShowPartialBar);
    }

    [Fact]
    public async Task IsLoading_until_the_first_snapshot_then_IsEmpty_with_zero_projects()
    {
        var fake = new FakeGuiRpcClient(); // default empty state: no projects
        AddHost("host-a", fake);
        var vm = MakeVm();

        Assert.True(vm.IsLoading);
        Assert.False(vm.IsEmpty);

        _manager.Start();
        await Wait.UntilAsync(() => !vm.IsLoading, "loading should clear once the first snapshot lands");

        Assert.False(vm.IsLoading);
        Assert.True(vm.IsEmpty);
        Assert.Empty(vm.Rows);
    }

    // Header-click sort reorders the parent aggregates; child (per-host) rows follow their parent
    // (design: "聚合的时候排序只排序主值…主机名是展开的，不在这个排序里"). Toggling direction moves a
    // parent AND its expanded children together — the built-in flat sort would instead scatter the
    // children, which is why the view cancels it and routes here.
    [Fact]
    public async Task Header_sort_orders_parents_and_expanded_children_follow_their_parent()
    {
        AddHost("host-a", FakeWithProjects(Proj("u-a", "Alpha", rac: 1), Proj("u-b", "Beta", rac: 5)));
        AddHost("host-b", FakeWithProjects(Proj("u-b", "Beta", rac: 5)));
        var vm = MakeVm();
        _manager.Start();
        await Wait.UntilAsync(() => vm.Rows.Count == 2, "two parent groups aggregate");
        // compute's default is RAC descending: Beta (5+5=10) before Alpha (1).
        // Order is the VIEW's (RowsView), not the source Rows collection.
        Assert.Equal(new[] { "Beta", "Alpha" }, ViewNames(vm));

        // Sort by project name ascending: the view re-sorts, parents reorder.
        vm.ToggleSort(ProjectSortColumn.ByName);
        Assert.Equal(new[] { "Alpha", "Beta" }, ViewNames(vm));

        // Expand Beta: its two children sit directly under it in the view.
        vm.ToggleExpandCommand.Execute("u-b");
        var expanded = vm.RowsView.Cast<ProjectRow>().ToList();
        Assert.Equal(4, expanded.Count);
        Assert.Equal("Alpha", expanded[0].Data.Name);
        Assert.Equal("Beta", expanded[1].Data.Name);
        Assert.False(expanded[2].Data.IsParent);
        Assert.False(expanded[3].Data.IsParent);
        Assert.Equal("u-b", expanded[2].Data.MasterUrl);
        Assert.Equal("u-b", expanded[3].Data.MasterUrl);

        // Toggle to descending: Beta moves above Alpha and its expanded children move WITH it.
        vm.ToggleSort(ProjectSortColumn.ByName);
        var descending = vm.RowsView.Cast<ProjectRow>().ToList();
        Assert.Equal(4, descending.Count);
        Assert.Equal("Beta", descending[0].Data.Name);
        Assert.False(descending[1].Data.IsParent);
        Assert.False(descending[2].Data.IsParent);
        Assert.Equal("Alpha", descending[3].Data.Name);
    }

    // Steady-state polls with unchanged data must be zero-churn end-to-end: no
    // CollectionChanged on the source Rows AND no view Refresh (Reset) on
    // RowsView — the order check in Rebuild is conditional precisely so an
    // unchanged poll never re-shapes the view. Holder identity survives too.
    [Fact]
    public async Task Steady_state_poll_raises_no_source_events_and_no_view_refresh()
    {
        AddHost("host-a", FakeWithProject("http://p/", "P", rac: 10));
        var vm = MakeVm();
        _manager.Start();
        await Wait.UntilAsync(() => vm.Rows.Count == 1);

        var firstRow = vm.Rows[0];
        _clock.Now = _store.Hosts[0].Snapshot!.Timestamp;
        var sourceChanges = 0;
        var viewChanges = 0;
        vm.Rows.CollectionChanged += (_, _) => sourceChanges++;
        vm.RowsView.CollectionChanged += (_, _) => viewChanges++;

        _clock.Advance(TimeSpan.FromSeconds(1));
        _clock.Advance(TimeSpan.FromSeconds(1));
        _clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(0, sourceChanges);
        Assert.Equal(0, viewChanges);
        Assert.Same(firstRow, vm.Rows[0]);
    }
}
