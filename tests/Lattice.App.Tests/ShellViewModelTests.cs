using Lattice.App.Infrastructure;
using Lattice.App.Localization;
using Lattice.App.Tests.Fakes;
using Lattice.App.ViewModels;
using Lattice.Boinc.GuiRpc;
using Lattice.Core;
using Lattice.Tests;
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
        _manager = new HostMonitorManager(_registry, () => new RoutingGuiRpcClient(_fakes), TimeProvider.System);
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
    public void Has_four_views_and_starts_on_tasks()
    {
        Assert.Equal([Strings.NavTasks, Strings.NavProjects, Strings.NavTransfers, Strings.NavEventLog],
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
    public void NavigateToSettings_shows_settings_page_and_expands_target_host()
    {
        var host = TestData.MakeHostConfig();
        _registry.AddHost(host);

        _shell.NavigateToSettings(host.Id);

        Assert.Same(_shell.Settings, _shell.CurrentPage);
        Assert.True(_shell.Settings.Hosts.Single(h => h.HostId == host.Id).IsExpanded);
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
        var eventLog = Assert.IsType<EventLogViewModel>(_shell.Views[3].Page);
        Assert.Same(_shell.EventLog, eventLog);
        Assert.True(eventLog.Scope.IsAllHosts);

        var host = TestData.MakeHostConfig();
        _registry.AddHost(host);
        _shell.Scope = new ScopeSelection(host.Id);

        Assert.Equal(host.Id, eventLog.Scope.HostId);
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

        _shell.SelectViewCommand.Execute("3");

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
        _shell.SelectViewCommand.Execute("3");
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

        _shell.SelectViewCommand.Execute("3");
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

        await Wait.UntilAsync(() => _shell.Tasks.Rows.Count == 1);

        Assert.Equal(1, _shell.TasksCount);
    }
}
