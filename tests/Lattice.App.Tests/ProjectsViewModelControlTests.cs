using System.Globalization;
using Lattice.App.Aggregation;
using Lattice.App.Localization;
using Lattice.App.Tests.Fakes;
using Lattice.App.ViewModels;
using Lattice.App.Views;
using Lattice.Boinc.GuiRpc;
using Lattice.Core;
using Lattice.Tests;
using Xunit;

namespace Lattice.App.Tests;

/// <summary>
/// M3 PR G control-op gates for the Projects view: DI-2 parent-row fan-out over
/// every attachment host with a blast-radius confirm, DI-1 destructive detach,
/// DI-3 enablement vs connection state, classify routing (1 vs N hosts), the
/// op→wire mapping, and aggregated partial-failure surfacing. The dialog is
/// faked at the VM boundary; settles are on observed fake calls / end state.
/// </summary>
public class ProjectsViewModelControlTests : IAsyncLifetime
{
    private HostGraphFixture _fx = null!;

    public ValueTask InitializeAsync()
    {
        _fx = new HostGraphFixture();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _fx.DisposeAsync();

    private ProjectsViewModel MakeVm() =>
        new(_fx.Store, _fx.Clock, _fx.Control, FakeAttachFlow.NoopRun, new ImmediateUiDispatcher());

    private static Project Proj(string url, string name) => TestData.MakeProject(url, name);

    private static FakeGuiRpcClient FakeWithProjects(params Project[] projects) =>
        new() { OnGetState = () => Task.FromResult(TestData.MakeState(projects: projects)) };

    private ProjectRow ParentRow(ProjectsViewModel vm, string masterUrl) =>
        (ProjectRow)vm.Rows.Single(r => r.Data.IsParent && r.Data.MasterUrl == masterUrl);

    /// <summary>
    /// "This group has aggregated exactly <paramref name="hosts"/> attachments" — the
    /// settle condition every MULTI-host case here needs. A parent row's HostsText IS
    /// its attachment count (<see cref="ProjectRowViewModel.Parent"/>). A row COUNT
    /// cannot express this: hosts sharing a project merge into ONE row, so `Rows.Count
    /// == 1` is already true with the first host alone and stays true afterwards —
    /// the settle can return mid-fleet, and the op under test then classifies as
    /// single-host and skips the confirmation seam entirely (issue #189). Written as a
    /// predicate over Rows, not a Single() lookup, so it is safe before the row exists.
    /// </summary>
    private static bool Aggregates(ProjectsViewModel vm, string masterUrl, int hosts) =>
        vm.Rows.Any(r => r.Data.IsParent && r.Data.MasterUrl == masterUrl
                         && r.Data.HostsText == hosts.ToString(CultureInfo.InvariantCulture));

    // --- DI-3 enablement -----------------------------------------------------

    [Fact]
    public async Task Commands_disabled_without_selection()
    {
        _fx.AddHost("host-a", FakeWithProjects(Proj("http://p/", "P")));
        var vm = MakeVm();
        _fx.Start();
        await _fx.SettleAsync(() => vm.Rows.Count == 1);

        Assert.False(vm.SuspendSelectedCommand.CanExecute(null));
        Assert.False(vm.UpdateSelectedCommand.CanExecute(null));
        Assert.False(vm.ResumeSelectedCommand.CanExecute(null));
        Assert.False(vm.DetachSelectedCommand.CanExecute(null));
        Assert.Null(vm.ControlDisabledReason);
    }

    [Fact]
    public async Task Commands_enabled_when_the_selected_rows_hosts_are_connected()
    {
        _fx.AddHost("host-a", FakeWithProjects(Proj("http://p/", "P")));
        _fx.AddHost("host-b", FakeWithProjects(Proj("http://p/", "P")));
        var vm = MakeVm();
        _fx.Start();
        // BOTH hosts, not just enough hosts to make one row: the test name says "the
        // selected row's hostS", so a settle host-a alone satisfies would silently
        // assert the single-host case (issue #189's class).
        await _fx.SettleAsync(() => vm.Rows.Count == 1 && Aggregates(vm, "http://p/", hosts: 2));
        vm.SelectedRow = vm.Rows[0];

        Assert.True(vm.SuspendSelectedCommand.CanExecute(null));
        Assert.True(vm.UpdateSelectedCommand.CanExecute(null));
        Assert.True(vm.DetachSelectedCommand.CanExecute(null));
        Assert.Null(vm.ControlDisabledReason);
    }

    [Fact]
    public async Task Child_commands_disable_with_reason_when_its_host_leaves_connected()
    {
        _fx.AddHost("host-a", FakeWithProjects(Proj("http://p/", "P")));
        var fakeB = FakeWithProjects(Proj("http://p/", "P"));
        _fx.AddHost("host-b", fakeB);
        var vm = MakeVm();
        _fx.Start();
        await _fx.SettleAsync(() => vm.Rows.Count == 1 && Aggregates(vm, "http://p/", hosts: 2));

        vm.ToggleExpandCommand.Execute("http://p/");
        await _fx.SettleAsync(() => vm.Rows.Count == 3, "expanding shows the two child rows");
        // Children in host-name order: Rows[2] is host-b.
        var childB = vm.Rows[2];
        Assert.Equal("host-b", childB.Data.Name);
        vm.SelectedRow = childB;
        Assert.True(vm.SuspendSelectedCommand.CanExecute(null));

        // host-b's next poll finds the daemon gone; the monitor leaves Connected.
        fakeB.OnGetResults = _ => throw new BoincConnectionException("gone");
        _fx.Store.RequestRefresh();
        await _fx.SettleAsync(() => !vm.SuspendSelectedCommand.CanExecute(null),
            "the child command must disable once its host is no longer Connected");

        Assert.False(vm.DetachSelectedCommand.CanExecute(null));
        Assert.Equal(Strings.ControlHostNotConnected, vm.ControlDisabledReason);
    }

    // --- DI-2 fan-out + classify routing ------------------------------------

    [Fact]
    public async Task Parent_op_fans_out_to_every_attachment_host_and_no_others()
    {
        var fakeA = FakeWithProjects(Proj("http://p/", "P"));
        var fakeB = FakeWithProjects(Proj("http://p/", "P"));
        var fakeC = FakeWithProjects(Proj("http://q/", "Q")); // other project — untouched
        _fx.AddHost("host-a", fakeA);
        _fx.AddHost("host-b", fakeB);
        _fx.AddHost("host-c", fakeC);
        var vm = MakeVm();
        _fx.Start();
        // P must have aggregated BOTH a and b before the row is selected: `Rows.Count == 2`
        // alone is satisfied by host-a (P) + host-c (Q) with host-b still in flight, and the
        // fan-out would then never reach fakeB — the settle below would hang to the ceiling
        // rather than fail fast (issue #189's class).
        await _fx.SettleAsync(
            () => vm.Rows.Count == 2 && Aggregates(vm, "http://p/", hosts: 2) && Aggregates(vm, "http://q/", hosts: 1),
            "two project groups: P over a+b, Q on c");

        vm.SelectedRow = ParentRow(vm, "http://p/");
        vm.ConfirmationHandler = _ => Task.FromResult(true); // Caution (2 hosts)

        await vm.SuspendSelectedCommand.ExecuteAsync(null);
        await _fx.SettleAsync(() =>
            fakeA.Calls.Contains("project_op:suspend:http://p/")
            && fakeB.Calls.Contains("project_op:suspend:http://p/"));

        Assert.DoesNotContain(fakeC.Calls, c => c.StartsWith("project_op:"));
    }

    [Fact]
    public async Task Multi_host_reversible_op_confirms_with_the_host_list_and_declining_prevents_it()
    {
        var fakeA = FakeWithProjects(Proj("http://p/", "P"));
        var fakeB = FakeWithProjects(Proj("http://p/", "P"));
        _fx.AddHost("host-a", fakeA);
        _fx.AddHost("host-b", fakeB);
        var vm = MakeVm();
        _fx.Start();
        // The multi-host CLASSIFICATION is what this test gates, and it is read off the
        // row's attachments at execute time. `Rows.Count == 1` is true the moment host-a
        // lands and never becomes false, so it let the op run as single-host — the instant
        // path, which never consults the seam, leaving `consulted` empty (issue #189, seen
        // on a loaded windows-latest runner).
        await _fx.SettleAsync(() => vm.Rows.Count == 1 && Aggregates(vm, "http://p/", hosts: 2),
            "the row must span BOTH hosts before the op classifies");
        vm.SelectedRow = vm.Rows[0];

        var consulted = new List<ConfirmationRequest>();
        vm.ConfirmationHandler = r => { consulted.Add(r); return Task.FromResult(false); };

        await vm.SuspendSelectedCommand.ExecuteAsync(null);

        var request = Assert.Single(consulted);
        Assert.Equal(ConfirmSeverity.Caution, request.Severity);
        Assert.Contains("host-a", request.Body);
        Assert.Contains("host-b", request.Body);
        // Declined → neither host is touched.
        Assert.DoesNotContain(fakeA.Calls, c => c.StartsWith("project_op:"));
        Assert.DoesNotContain(fakeB.Calls, c => c.StartsWith("project_op:"));
    }

    [Fact]
    public async Task Single_host_reversible_op_is_instant_and_never_consults_the_seam()
    {
        var fakeA = FakeWithProjects(Proj("http://p/", "P"));
        _fx.AddHost("host-a", fakeA);
        var vm = MakeVm();
        _fx.Start();
        await _fx.SettleAsync(() => vm.Rows.Count == 1);
        vm.SelectedRow = vm.Rows[0];

        var consulted = new List<ConfirmationRequest>();
        vm.ConfirmationHandler = r => { consulted.Add(r); return Task.FromResult(true); };

        await vm.SuspendSelectedCommand.ExecuteAsync(null);
        await _fx.SettleAsync(() => fakeA.Calls.Contains("project_op:suspend:http://p/"));

        Assert.Empty(consulted);
    }

    // --- DI-1 destructive detach --------------------------------------------

    [Fact]
    public async Task Detach_single_host_is_destructive_and_always_dialogs()
    {
        var fakeA = FakeWithProjects(Proj("http://p/", "P"));
        _fx.AddHost("host-a", fakeA);
        var vm = MakeVm();
        _fx.Start();
        await _fx.SettleAsync(() => vm.Rows.Count == 1);
        vm.SelectedRow = vm.Rows[0];

        var consulted = new List<ConfirmationRequest>();
        vm.ConfirmationHandler = r => { consulted.Add(r); return Task.FromResult(true); };

        await vm.DetachSelectedCommand.ExecuteAsync(null);
        await _fx.SettleAsync(() => fakeA.Calls.Contains("project_op:detach:http://p/"));

        var request = Assert.Single(consulted);
        Assert.Equal(ConfirmSeverity.Destructive, request.Severity);
        Assert.Contains("host-a", request.Body);
    }

    [Fact]
    public async Task Detach_with_no_seam_wired_declines_safely()
    {
        var fakeA = FakeWithProjects(Proj("http://p/", "P"));
        _fx.AddHost("host-a", fakeA);
        var vm = MakeVm();
        _fx.Start();
        await _fx.SettleAsync(() => vm.Rows.Count == 1);
        vm.SelectedRow = vm.Rows[0];
        Assert.Null(vm.ConfirmationHandler);

        await vm.DetachSelectedCommand.ExecuteAsync(null);

        Assert.DoesNotContain(fakeA.Calls, c => c.StartsWith("project_op:detach:"));
    }

    // --- op → wire mapping (single-host, seam auto-confirms) -----------------

    [Theory]
    [InlineData("suspend")]
    [InlineData("resume")]
    [InlineData("update")]
    [InlineData("detach")]
    public async Task Each_command_maps_to_its_wire_op(string wire)
    {
        var fakeA = FakeWithProjects(Proj("http://p/", "P"));
        _fx.AddHost("host-a", fakeA);
        var vm = MakeVm();
        _fx.Start();
        await _fx.SettleAsync(() => vm.Rows.Count == 1);
        vm.SelectedRow = vm.Rows[0];
        vm.ConfirmationHandler = _ => Task.FromResult(true);

        var command = wire switch
        {
            "suspend" => vm.SuspendSelectedCommand,
            "resume" => vm.ResumeSelectedCommand,
            "update" => vm.UpdateSelectedCommand,
            "detach" => vm.DetachSelectedCommand,
            _ => throw new ArgumentOutOfRangeException(nameof(wire)),
        };
        await command.ExecuteAsync(null);
        await _fx.SettleAsync(() => fakeA.Calls.Contains($"project_op:{wire}:http://p/"));
    }

    // --- failure aggregation -------------------------------------------------

    [Fact]
    public async Task Partial_fan_out_failure_aggregates_naming_the_failed_host()
    {
        var fakeA = FakeWithProjects(Proj("http://p/", "P"));
        var fakeB = FakeWithProjects(Proj("http://p/", "P"));
        fakeB.OnProjectOp = (_, _) => throw new BoincRpcException("daemon says no");
        _fx.AddHost("host-a", fakeA);
        _fx.AddHost("host-b", fakeB);
        var vm = MakeVm();
        _fx.Start();
        // Without host-b in the row the fan-out only reaches the host that SUCCEEDS, so
        // ControlFailure never opens and the settle below burns the hang ceiling (#189's class).
        await _fx.SettleAsync(() => vm.Rows.Count == 1 && Aggregates(vm, "http://p/", hosts: 2),
            "the failing host must be in the row before the fan-out runs");
        vm.SelectedRow = vm.Rows[0];
        vm.ConfirmationHandler = _ => Task.FromResult(true);

        await vm.SuspendSelectedCommand.ExecuteAsync(null);
        await _fx.SettleAsync(() => vm.ControlFailure.IsOpen);

        // The receipt names the host that failed and carries its error text.
        Assert.Contains("host-b", vm.ControlFailure.Message);
        Assert.Contains("daemon says no", vm.ControlFailure.Message);
        // Fan-out continued past the failure: the reachable host still got the op.
        Assert.Contains("project_op:suspend:http://p/", fakeA.Calls);
    }

    [Fact]
    public async Task All_succeeded_fan_out_clears_the_failure_surface()
    {
        var fakeA = FakeWithProjects(Proj("http://p/", "P"));
        _fx.AddHost("host-a", fakeA);
        var vm = MakeVm();
        _fx.Start();
        await _fx.SettleAsync(() => vm.Rows.Count == 1);
        vm.SelectedRow = vm.Rows[0];

        vm.ControlFailure.Report("stale", "earlier failure");
        await vm.SuspendSelectedCommand.ExecuteAsync(null); // single-host: Instant
        await _fx.SettleAsync(() => !vm.ControlFailure.IsOpen,
            "an all-succeeded op must clear the failure surface");
    }
}
