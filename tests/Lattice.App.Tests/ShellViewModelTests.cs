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

public class ShellViewModelTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}.json");
    private readonly string _uiPath = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}-ui.json");
    private readonly Dictionary<string, FakeGuiRpcClient> _fakes = [];
    private HostRegistry _registry = null!;
    private HostMonitorManager _manager = null!;
    private HostStore _store = null!;
    private ManualUiClock _clock = null!;
    private ShellViewModel _shell = null!;

    public ValueTask InitializeAsync()
    {
        _registry = new HostRegistry(new LatticeConfig(5, []), _path);
        // Frozen fake clock: the count-mirroring facts settle on the immediate first
        // poll, so no natural steady-state poll is needed (shared rationale on
        // TasksViewModelTests).
        _manager = new HostMonitorManager(_registry, () => new RoutingGuiRpcClient(_fakes), new FakeTimeProvider());
        _store = new HostStore(_registry, _manager, new ImmediateUiDispatcher());
        _clock = new ManualUiClock();
        _shell = new ShellViewModel(_registry, _store, _clock, new UiStateStore(_uiPath),
            () => new RoutingGuiRpcClient(_fakes));
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _manager.DisposeAsync();
        File.Delete(_path);
        File.Delete(_uiPath);
    }

    [Fact]
    public void Has_five_views_and_starts_on_tasks()
    {
        Assert.Equal(
            [Strings.NavTasks, Strings.NavProjects, Strings.NavTransfers, Strings.NavStatistics, Strings.NavEventLog],
            _shell.Views.Select(v => v.Title).ToArray());
        Assert.Same(_shell.Views[0], _shell.SelectedView);
        Assert.Same(_shell.Views[0].Page, _shell.CurrentPage);
    }

    [Fact]
    public void First_run_flag_follows_host_count()
    {
        Assert.False(_shell.HasHosts);
        _registry.AddHost(TestData.MakeHostConfig());
        Assert.True(_shell.HasHosts);
    }

    [Fact]
    public void Rail_items_reconcile_with_registry_changes()
    {
        var a = TestData.MakeHostConfig(name: "a");
        var b = TestData.MakeHostConfig(name: "b");
        _registry.AddHost(a);
        _registry.AddHost(b);
        _shell.SetRailViewportHeight(1000.0);   // tall viewport → Flat, so both host rows materialize
        Assert.Equal(2, _shell.RailEntries.OfType<HostRailItemViewModel>().Count());

        _registry.RemoveHost(a.Id);
        Assert.Equal("b", Assert.Single(_shell.RailEntries.OfType<HostRailItemViewModel>()).Name);
    }

    [Fact]
    public void Removing_a_host_disposes_its_rail_item_clock_subscription()
    {
        // Baseline includes Tasks' own permanent Tick subscription (it lives for
        // the shell's lifetime, independent of any host); the rail item adds
        // exactly one more on top of that and removes it again on host removal.
        var baseline = _clock.SubscriberCount;
        var host = TestData.MakeHostConfig();
        _registry.AddHost(host);
        Assert.Equal(baseline + 1, _clock.SubscriberCount);

        _registry.RemoveHost(host.Id);
        Assert.Equal(baseline, _clock.SubscriberCount);
    }

    [Fact]
    public void Scope_defaults_to_all_hosts_and_survives_view_switches()
    {
        var host = TestData.MakeHostConfig();
        _registry.AddHost(host);
        Assert.True(_shell.Scope.IsAllHosts);

        _shell.Scope = new ScopeSelection(host.Id);
        _shell.SelectedView = _shell.Views[2];

        Assert.Equal(host.Id, _shell.Scope.HostId);
        Assert.Same(_shell.Views[2].Page, _shell.CurrentPage);
    }

    [Fact]
    public void SelectView_command_switches_by_index()
    {
        _shell.SelectViewCommand.Execute("3");
        Assert.Same(_shell.Views[3], _shell.SelectedView);
    }

    [Fact]
    public void Tasks_page_is_a_TasksViewModel_and_receives_scope_changes()
    {
        var tasks = Assert.IsType<TasksViewModel>(_shell.Views[0].Page);
        Assert.Same(_shell.Tasks, tasks);
        Assert.True(tasks.Scope.IsAllHosts);

        var host = TestData.MakeHostConfig();
        _registry.AddHost(host);
        _shell.Scope = new ScopeSelection(host.Id);

        Assert.Equal(host.Id, tasks.Scope.HostId);
    }

    // A per-host message batch raised straight on the manager (real polling
    // couples messages with snapshots; this is the only way to exercise the
    // message-only path). ImmediateUiDispatcher makes MessagesReceived fire
    // synchronously, so no drain is needed.
    private static Message Alert(int seqno, string body) =>
        new("Proj", MessagePriority.UserAlert, seqno,
            new DateTimeOffset(2026, 7, 11, 12, 0, seqno, TimeSpan.Zero), body);

    [Fact]
    public void EventLog_page_is_an_EventLogViewModel_and_receives_scope_changes()
    {
        var eventLog = Assert.IsType<EventLogViewModel>(_shell.Views[4].Page);
        Assert.Same(_shell.EventLog, eventLog);
        Assert.True(eventLog.Scope.IsAllHosts);

        var host = TestData.MakeHostConfig();
        _registry.AddHost(host);
        _shell.Scope = new ScopeSelection(host.Id);

        Assert.Equal(host.Id, eventLog.Scope.HostId);
    }

    [Fact]
    public void Statistics_page_is_a_StatisticsViewModel_and_receives_scope_changes()
    {
        var statistics = Assert.IsType<StatisticsViewModel>(_shell.Views[3].Page);
        Assert.Same(_shell.Statistics, statistics);
        Assert.True(statistics.Scope.IsAllHosts);

        var host = TestData.MakeHostConfig();
        _registry.AddHost(host);
        _shell.Scope = new ScopeSelection(host.Id);

        Assert.Equal(host.Id, statistics.Scope.HostId);
    }

    [Fact]
    public void EventLogUnread_mirrors_the_event_log_view_models_unread_count()
    {
        var host = TestData.MakeHostConfig(name: "host-a", address: "host-a");
        _registry.AddHost(host);
        Assert.Equal(0, _shell.EventLogUnread);
        Assert.False(_shell.HasEventLogUnread);

        // The Event log is not the current page (shell starts on Tasks), so a
        // warning message accrues into the unread badge.
        ManagerTestAccess.RaiseMessagesAdded(
            _manager, new MessagesAddedEventArgs(host.Id, [Alert(1, "boom")]));

        Assert.Equal(1, _shell.EventLogUnread);
        Assert.True(_shell.HasEventLogUnread);
    }

    [Fact]
    public void Navigating_to_the_event_log_zeroes_the_unread_badge()
    {
        var host = TestData.MakeHostConfig(name: "host-a", address: "host-a");
        _registry.AddHost(host);
        ManagerTestAccess.RaiseMessagesAdded(
            _manager, new MessagesAddedEventArgs(host.Id, [Alert(1, "boom")]));
        Assert.Equal(1, _shell.EventLogUnread);

        _shell.SelectViewCommand.Execute("4");

        Assert.Same(_shell.EventLog, _shell.CurrentPage);
        Assert.Equal(0, _shell.EventLogUnread);
        Assert.False(_shell.HasEventLogUnread);
    }

    [Fact]
    public void Navigating_away_from_the_event_log_re_enables_unread_counting()
    {
        var host = TestData.MakeHostConfig(name: "host-a", address: "host-a");
        _registry.AddHost(host);

        // Activate (zeroes + stops counting), then leave for Tasks.
        _shell.SelectViewCommand.Execute("4");
        _shell.SelectViewCommand.Execute("0");

        ManagerTestAccess.RaiseMessagesAdded(
            _manager, new MessagesAddedEventArgs(host.Id, [Alert(1, "boom")]));

        Assert.Equal(1, _shell.EventLogUnread);
        Assert.True(_shell.HasEventLogUnread);
    }

    [Fact]
    public void NavigateToSettings_deactivates_the_event_log()
    {
        var host = TestData.MakeHostConfig(name: "host-a", address: "host-a");
        _registry.AddHost(host);

        _shell.SelectViewCommand.Execute("4");
        Assert.True(_shell.EventLog.IsViewActive);

        _shell.NavigateToSettings();
        Assert.False(_shell.EventLog.IsViewActive);

        // Counting resumes once the Event log is no longer the current page.
        ManagerTestAccess.RaiseMessagesAdded(
            _manager, new MessagesAddedEventArgs(host.Id, [Alert(1, "boom")]));
        Assert.Equal(1, _shell.EventLogUnread);
    }

    [Fact]
    public void Projects_page_is_a_ProjectsViewModel_and_receives_scope_changes()
    {
        var projects = Assert.IsType<ProjectsViewModel>(_shell.Views[1].Page);
        Assert.Same(_shell.Projects, projects);
        Assert.True(projects.Scope.IsAllHosts);

        var host = TestData.MakeHostConfig();
        _registry.AddHost(host);
        _shell.Scope = new ScopeSelection(host.Id);

        Assert.Equal(host.Id, projects.Scope.HostId);
    }

    [Fact]
    public void Transfers_page_is_a_TransfersViewModel_and_receives_scope_changes()
    {
        var transfers = Assert.IsType<TransfersViewModel>(_shell.Views[2].Page);
        Assert.Same(_shell.Transfers, transfers);
        Assert.True(transfers.Scope.IsAllHosts);

        var host = TestData.MakeHostConfig();
        _registry.AddHost(host);
        _shell.Scope = new ScopeSelection(host.Id);

        Assert.Equal(host.Id, transfers.Scope.HostId);
    }

    [Fact]
    public void Density_toggle_on_one_view_syncs_to_the_other_in_session()
    {
        // Codex round-3 P2 (PR #45): TasksViewModel and TransfersViewModel each
        // cached IsCompact at construction with no way to observe a later change
        // from the other, sibling, long-lived view — flipping density in one
        // left the other showing the OLD density until app restart.
        // ShellViewModel wires both through one shared DensityPreference now,
        // so a toggle on either side must reach the other within the session.
        Assert.False(_shell.Tasks.IsCompact);
        Assert.False(_shell.Transfers.IsCompact);

        _shell.Transfers.IsCompact = true;
        Assert.True(_shell.Tasks.IsCompact);

        _shell.Tasks.IsCompact = false;
        Assert.False(_shell.Transfers.IsCompact);
    }

    [Fact]
    public async Task TasksCount_mirrors_the_tasks_view_models_row_count()
    {
        Assert.Equal(0, _shell.TasksCount);

        var fake = new FakeGuiRpcClient
        {
            OnGetResults = _ => Task.FromResult<IReadOnlyList<Result>>([TestData.MakeResult(name: "task_a")]),
        };
        _fakes["host-a"] = fake;
        _registry.AddHost(TestData.MakeHostConfig(name: "host-a", address: "host-a"));
        _manager.Start();

        // Settle on the mirror itself, not Tasks.Rows.Count: the count is bumped
        // by ObservableCollection.Add BEFORE the CollectionChanged handler that
        // syncs TasksCount runs, so waiting on the collection races the mirror
        // (and a frozen fake clock never re-polls to paper over the gap).
        await Wait.UntilAsync(() => _shell.TasksCount == 1);

        Assert.Equal(1, _shell.TasksCount);
    }

    [Fact]
    public async Task TransfersCount_mirrors_the_transfers_view_models_row_count()
    {
        Assert.Equal(0, _shell.TransfersCount);

        var fake = new FakeGuiRpcClient
        {
            OnGetFileTransfers = () => Task.FromResult<IReadOnlyList<FileTransfer>>([TestData.MakeTransfer(name: "file_a")]),
        };
        _fakes["host-a"] = fake;
        _registry.AddHost(TestData.MakeHostConfig(name: "host-a", address: "host-a"));
        _manager.Start();

        // Settle on the mirror itself, not Transfers.Rows.Count (same proxy race
        // as TasksCount above).
        await Wait.UntilAsync(() => _shell.TransfersCount == 1);

        Assert.Equal(1, _shell.TransfersCount);
    }

    [Fact]
    public async Task HasTransfersCount_is_false_at_zero_and_true_above_zero()
    {
        Assert.Equal(0, _shell.TransfersCount);
        Assert.False(_shell.HasTransfersCount);

        var fake = new FakeGuiRpcClient
        {
            OnGetFileTransfers = () => Task.FromResult<IReadOnlyList<FileTransfer>>([TestData.MakeTransfer(name: "file_a")]),
        };
        _fakes["host-a"] = fake;
        _registry.AddHost(TestData.MakeHostConfig(name: "host-a", address: "host-a"));
        _manager.Start();

        // Settle on the mirror itself, not Transfers.Rows.Count (same proxy race
        // as TasksCount above); HasTransfersCount derives from TransfersCount.
        await Wait.UntilAsync(() => _shell.TransfersCount == 1);

        Assert.True(_shell.HasTransfersCount);
    }

    private void AddHosts(int n)
    {
        for (var i = 0; i < n; i++)
            _registry.AddHost(TestData.MakeHostConfig(name: $"h{i}"));
    }

    [Fact]
    public void Small_viewport_with_many_hosts_groups_the_rail()
    {
        AddHosts(4);
        // Budget below (4 + 1) * 40 = 200 forces grouped under Auto.
        _shell.SetRailViewportHeight(180.0); // well below ReservedRailChrome(290) => available 0 < 200
        Assert.Contains(_shell.RailEntries, e => e is GroupHeaderRailItemViewModel);
        Assert.True(_shell.ShowRailToggle);
    }

    [Fact]
    public void Tall_viewport_keeps_the_rail_flat_and_hides_the_toggle()
    {
        AddHosts(4);
        _shell.SetRailViewportHeight(1000.0);
        Assert.DoesNotContain(_shell.RailEntries, e => e is GroupHeaderRailItemViewModel);
        Assert.False(_shell.ShowRailToggle);
    }

    [Fact]
    public void Toggling_grouping_forces_the_opposite_layout_and_persists()
    {
        AddHosts(4);
        _shell.SetRailViewportHeight(1000.0);              // fits => Flat
        _shell.ToggleRailGroupingCommand.Execute(null);    // force grouped
        Assert.Contains(_shell.RailEntries, e => e is GroupHeaderRailItemViewModel);
        // Persisted: a fresh shell on the same ui-state file restores grouped.
        var shell2 = new ShellViewModel(_registry, _store, _clock, new UiStateStore(_uiPath),
            () => new RoutingGuiRpcClient(_fakes));
        shell2.SetRailViewportHeight(1000.0);
        Assert.Contains(shell2.RailEntries, e => e is GroupHeaderRailItemViewModel);
        shell2.Dispose();
    }

    [Fact]
    public void Toggling_back_returns_to_adaptive_so_the_toggle_hides_once_it_fits()
    {
        AddHosts(4);
        _shell.SetRailViewportHeight(180.0);               // well below ReservedRailChrome(290) => available 0 < 200 => Auto => Grouped
        Assert.True(_shell.ShowRailToggle);
        _shell.ToggleRailGroupingCommand.Execute(null);    // Auto -> ForceFlat
        _shell.ToggleRailGroupingCommand.Execute(null);    // ForceFlat(overflow) -> Auto (re-adaptive)
        // Proof we returned to Auto (not stuck on a Force): growing to fit hides the toggle.
        _shell.SetRailViewportHeight(1000.0);
        Assert.False(_shell.ShowRailToggle);
        Assert.DoesNotContain(_shell.RailEntries, e => e is GroupHeaderRailItemViewModel);
    }

    [Fact]
    public void Selecting_a_host_survives_a_rail_rebuild()
    {
        AddHosts(3);
        _shell.SetRailViewportHeight(1000.0);
        var hostVm = _shell.RailEntries.OfType<HostRailItemViewModel>().First();
        _shell.SelectHostScope(hostVm.HostId);             // the click gesture's VM entry point
        Assert.Equal(hostVm.HostId, _shell.Scope.HostId);

        _shell.SetRailViewportHeight(1001.0);              // triggers a rebuild
        Assert.Equal(hostVm.HostId, _shell.Scope.HostId);  // scope preserved
    }

    [Fact]
    public void Single_host_is_presentation_only_scope_stays_all_hosts_host_highlighted()
    {
        AddHosts(1);
        _shell.SetRailViewportHeight(1000.0);
        // SingleHost is presentation only (owned by RailLayout): it renders + highlights the sole
        // host row but does NOT mutate Scope (host-added is not a ScopeEvent). Scope stays All hosts
        // (data-identical for one host). No sentinel row.
        var row = Assert.Single(_shell.RailEntries.OfType<HostRailItemViewModel>());
        Assert.Same(row, _shell.SelectedRailEntry);          // host row highlighted
        Assert.True(_shell.Scope.IsAllHosts);                // ...but scope is All hosts
        Assert.DoesNotContain(_shell.RailEntries, e => e is AllHostsRailItemViewModel);
    }

    [Fact]
    public void Scope_persists_when_the_scoped_hosts_group_collapses()
    {
        AddHosts(3);                              // all Healthy tier (Disconnected → Connecting → Healthy)
        _shell.SetRailViewportHeight(1000.0);     // fits → Flat, every host row present
        var row = _shell.RailEntries.OfType<HostRailItemViewModel>().First();
        var scopedId = row.HostId;
        _shell.SelectHostScope(scopedId);
        Assert.Equal(scopedId, _shell.Scope.HostId);

        // Force Grouped; Healthy defaults collapsed, so the scoped host's row is NOT emitted.
        _shell.ToggleRailGroupingCommand.Execute(null);
        Assert.DoesNotContain(_shell.RailEntries.OfType<HostRailItemViewModel>(),
            h => h.HostId == scopedId);

        // Intended behavior: scope persists as DATA even with no highlighted row — it must
        // NOT silently fall back to All hosts (regression: SelectedItem-not-in-Items → null).
        Assert.Equal(scopedId, _shell.Scope.HostId);
        Assert.False(_shell.Scope.IsAllHosts);

        // Expanding the group again re-highlights the row without changing scope.
        var healthyHeader = _shell.RailEntries.OfType<GroupHeaderRailItemViewModel>()
            .Single(g => g.Tier.Equals(RailTier.Healthy));
        healthyHeader.ToggleCommand.Execute(null);
        Assert.Equal(scopedId, _shell.Scope.HostId);
    }

    [Fact]
    public void A_header_assigned_to_SelectedRailEntry_is_not_a_scope_change()
    {
        // The ListBox two-way binding assigns SelectedRailEntry = header on a header click.
        // SelectedRailEntry is now a PURE highlight (scope rides the click gesture, not this
        // property), so such an assignment must be inert w.r.t. scope — no reset to All hosts
        // (round-5 P2 — the structural invariant behind the collapsed-group loss).
        AddHosts(3);
        _shell.SetRailViewportHeight(1000.0);
        var host = _shell.RailEntries.OfType<HostRailItemViewModel>().First();
        var scopedId = host.HostId;
        _shell.SelectHostScope(scopedId);
        _shell.ToggleRailGroupingCommand.Execute(null);         // ForceGrouped => a header exists

        var header = _shell.RailEntries.OfType<GroupHeaderRailItemViewModel>()
            .Single(g => g.Tier.Equals(RailTier.Healthy));
        _shell.SelectedRailEntry = header;                       // the binding's assignment (inert)

        Assert.Equal(scopedId, _shell.Scope.HostId);
        Assert.False(_shell.Scope.IsAllHosts);
    }

    // --- persisted global host scope (design README:80/108) ---

    [Fact]
    public void Persisted_scope_restores_the_host_on_construction()
    {
        var h1 = TestData.MakeHostConfig(name: "a");
        var h2 = TestData.MakeHostConfig(name: "b");
        _registry.AddHost(h1);
        _registry.AddHost(h2);
        new UiStateStore(_uiPath).Save(UiState.Default with { ScopeHostId = h2.Id });

        // Fresh shell over the same registry + ui-state restores the persisted scope. Two hosts
        // (not the degenerate single-host pin) so the default would otherwise be All hosts.
        var shell2 = new ShellViewModel(_registry, _store, _clock, new UiStateStore(_uiPath),
            () => new RoutingGuiRpcClient(_fakes));
        shell2.SetRailViewportHeight(1000.0);
        Assert.Equal(h2.Id, shell2.Scope.HostId);
        shell2.Dispose();
    }

    [Fact]
    public void Unknown_persisted_scope_falls_back_to_all_hosts()
    {
        _registry.AddHost(TestData.MakeHostConfig(name: "a"));
        _registry.AddHost(TestData.MakeHostConfig(name: "b"));
        new UiStateStore(_uiPath).Save(UiState.Default with { ScopeHostId = Guid.NewGuid() }); // no such host

        var shell2 = new ShellViewModel(_registry, _store, _clock, new UiStateStore(_uiPath),
            () => new RoutingGuiRpcClient(_fakes));
        shell2.SetRailViewportHeight(1000.0);
        Assert.True(shell2.Scope.IsAllHosts);   // missing id → fallback, no throw
        shell2.Dispose();
    }

    [Fact]
    public void Selecting_a_host_persists_the_scope_id()
    {
        var h1 = TestData.MakeHostConfig(name: "a");
        var h2 = TestData.MakeHostConfig(name: "b");
        _registry.AddHost(h1);
        _registry.AddHost(h2);
        _shell.SetRailViewportHeight(1000.0);

        _shell.SelectHostScope(h2.Id);
        Assert.Equal(h2.Id, new UiStateStore(_uiPath).Load().ScopeHostId);

        _shell.SelectAllHostsScope();
        Assert.Null(new UiStateStore(_uiPath).Load().ScopeHostId);   // All hosts clears the id
    }

    [Fact]
    public void Explicit_click_on_the_sole_host_scopes_and_persists_and_survives_a_second_host()
    {
        var solo = TestData.MakeHostConfig(name: "solo");
        _registry.AddHost(solo);
        _shell.SetRailViewportHeight(1000.0);

        // An explicit click on the sole host row IS a real selection (ExplicitSelect) — scope + persist.
        _shell.SelectHostScope(_shell.RailEntries.OfType<HostRailItemViewModel>().Single().HostId);
        Assert.Equal(solo.Id, _shell.Scope.HostId);
        Assert.Equal(solo.Id, new UiStateStore(_uiPath).Load().ScopeHostId);

        // A deliberate choice survives adding a 2nd host (unlike a mere presentation pin).
        _registry.AddHost(TestData.MakeHostConfig(name: "second"));
        Assert.Equal(solo.Id, _shell.Scope.HostId);
    }

    [Fact]
    public void Adding_a_second_host_without_an_explicit_selection_stays_all_hosts()
    {
        _registry.AddHost(TestData.MakeHostConfig(name: "solo"));
        _shell.SetRailViewportHeight(1000.0);
        Assert.True(_shell.Scope.IsAllHosts);            // no auto-pin (host-added is not a ScopeEvent)

        _registry.AddHost(TestData.MakeHostConfig(name: "second"));
        // Still All hosts, and the (now-present) sentinel is highlighted.
        Assert.True(_shell.Scope.IsAllHosts);
        Assert.IsType<AllHostsRailItemViewModel>(_shell.SelectedRailEntry);
    }

    [Fact]   // R11 at the shell boundary: removing the scoped host fires HostRemoved → step clears scope
    public void Removing_the_scoped_host_falls_back_to_all_hosts_and_clears_persistence()
    {
        var h1 = TestData.MakeHostConfig(name: "a");
        var h2 = TestData.MakeHostConfig(name: "b");
        _registry.AddHost(h1);
        _registry.AddHost(h2);
        _shell.SetRailViewportHeight(1000.0);

        _shell.SelectHostScope(h2.Id);
        Assert.Equal(h2.Id, _shell.Scope.HostId);
        Assert.Equal(h2.Id, new UiStateStore(_uiPath).Load().ScopeHostId);

        _registry.RemoveHost(h2.Id);   // ReconcileHosts → ScopeMachine.HostRemoved(h2) → AllHosts + ClearPersisted

        Assert.True(_shell.Scope.IsAllHosts);                              // no longer pointing at a dead host
        Assert.Null(new UiStateStore(_uiPath).Load().ScopeHostId);        // stale id wiped
        // Removing a NON-scoped host leaves the surviving scope alone (covered by ScopeMachine table row 4).
    }
}
