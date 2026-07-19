using Lattice.App.Aggregation;
using Lattice.App.Localization;
using Lattice.App.ViewModels;
using Lattice.App.Views;
using Lattice.Boinc.GuiRpc;
using Lattice.Core;
using Lattice.Tests;
using Xunit;

namespace Lattice.App.Tests;

/// <summary>
/// M3 PR F control-op gates for the Tasks view: DI-3 enablement vs connection
/// state, policy → dialog-seam routing (the dialog itself is faked at the VM
/// boundary), the failure surface, and the in-flight disable. Composition
/// root / dispatcher / settle discipline all come from HostGraphFixture.
/// </summary>
public class TasksViewModelControlTests : IAsyncLifetime
{
    private HostGraphFixture _fx = null!;

    public ValueTask InitializeAsync()
    {
        _fx = new HostGraphFixture();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _fx.DisposeAsync();

    private TasksViewModel MakeVm() =>
        new(_fx.Store, _fx.Clock, _fx.UiState, _fx.Density, _fx.Control);

    private async Task<(TasksViewModel Vm, FakeGuiRpcClient Fake)> ConnectedVmWithSelectedRowAsync()
    {
        var fake = new FakeGuiRpcClient
        {
            OnGetResults = _ => Task.FromResult<IReadOnlyList<Result>>(
                [TestData.MakeResult(name: "wu_1", projectUrl: "http://proj.example/")]),
        };
        _fx.AddHost("host-a", fake);
        var vm = MakeVm();
        _fx.Start();
        await _fx.SettleAsync(() => vm.Rows.Count == 1);
        vm.SelectedRow = vm.Rows[0];
        return (vm, fake);
    }

    [Fact]
    public async Task Commands_disabled_without_selection()
    {
        _fx.AddHost("host-a", new FakeGuiRpcClient());
        var vm = MakeVm();
        _fx.Start();
        await _fx.SettleAsync(() => _fx.Store.Hosts.Count == 1);

        Assert.False(vm.SuspendSelectedCommand.CanExecute(null));
        Assert.False(vm.ResumeSelectedCommand.CanExecute(null));
        Assert.False(vm.AbortSelectedCommand.CanExecute(null));
        Assert.Null(vm.ControlDisabledReason);
    }

    [Fact]
    public async Task Commands_enabled_when_selected_rows_host_is_connected()
    {
        var (vm, _) = await ConnectedVmWithSelectedRowAsync();

        Assert.True(vm.SuspendSelectedCommand.CanExecute(null));
        Assert.True(vm.ResumeSelectedCommand.CanExecute(null));
        Assert.True(vm.AbortSelectedCommand.CanExecute(null));
        Assert.Null(vm.ControlDisabledReason);
    }

    [Fact]
    public async Task Commands_disable_with_reason_when_the_host_leaves_Connected()
    {
        var (vm, fake) = await ConnectedVmWithSelectedRowAsync();
        Assert.True(vm.SuspendSelectedCommand.CanExecute(null));

        // The next poll finds the daemon gone; the monitor leaves Connected.
        // The stale selection object survives in the VM (the grid clears it in
        // production, but DI-3 must not depend on that) — commands go disabled
        // with the tooltip reason the instant the store reports the drop.
        fake.OnGetResults = _ => throw new BoincConnectionException("gone");
        _fx.Store.RequestRefresh();
        await _fx.SettleAsync(() => !vm.SuspendSelectedCommand.CanExecute(null),
            "commands must disable once the host is no longer Connected");

        Assert.False(vm.AbortSelectedCommand.CanExecute(null));
        Assert.Equal(Strings.ControlHostNotConnected, vm.ControlDisabledReason);
    }

    [Fact]
    public async Task Instant_suspend_executes_without_consulting_the_dialog_seam()
    {
        var (vm, fake) = await ConnectedVmWithSelectedRowAsync();
        var consulted = new List<ConfirmationRequest>();
        vm.ConfirmationHandler = r => { consulted.Add(r); return Task.FromResult(true); };

        await vm.SuspendSelectedCommand.ExecuteAsync(null);
        await _fx.SettleAsync(() => fake.Calls.Any(c => c.StartsWith("task_op:suspend:")));

        Assert.Empty(consulted);
        Assert.Contains("task_op:suspend:http://proj.example/:wu_1", fake.Calls);
    }

    [Fact]
    public async Task Instant_resume_maps_to_the_resume_wire_op()
    {
        var (vm, fake) = await ConnectedVmWithSelectedRowAsync();

        await vm.ResumeSelectedCommand.ExecuteAsync(null);
        await _fx.SettleAsync(() => fake.Calls.Any(c => c.StartsWith("task_op:resume:")));

        Assert.Contains("task_op:resume:http://proj.example/:wu_1", fake.Calls);
    }

    [Fact]
    public async Task Abort_consults_the_seam_and_declining_prevents_the_op()
    {
        var (vm, fake) = await ConnectedVmWithSelectedRowAsync();
        var consulted = new List<ConfirmationRequest>();
        vm.ConfirmationHandler = r => { consulted.Add(r); return Task.FromResult(false); };

        await vm.AbortSelectedCommand.ExecuteAsync(null);

        var request = Assert.Single(consulted);
        Assert.Equal(ConfirmSeverity.Destructive, request.Severity);
        Assert.Contains("wu_1", request.Body);
        Assert.Contains("host-a", request.Body);
        Assert.DoesNotContain(fake.Calls, c => c.StartsWith("task_op:abort:"));
    }

    [Fact]
    public async Task Abort_executes_after_the_seam_confirms()
    {
        var (vm, fake) = await ConnectedVmWithSelectedRowAsync();
        vm.ConfirmationHandler = _ => Task.FromResult(true);

        await vm.AbortSelectedCommand.ExecuteAsync(null);
        await _fx.SettleAsync(() => fake.Calls.Any(c => c.StartsWith("task_op:abort:")));

        Assert.Contains("task_op:abort:http://proj.example/:wu_1", fake.Calls);
    }

    [Fact]
    public async Task Abort_with_no_seam_wired_declines_safely()
    {
        var (vm, fake) = await ConnectedVmWithSelectedRowAsync();
        Assert.Null(vm.ConfirmationHandler);

        await vm.AbortSelectedCommand.ExecuteAsync(null);

        Assert.DoesNotContain(fake.Calls, c => c.StartsWith("task_op:abort:"));
    }

    [Fact]
    public async Task Failure_reports_to_the_surface_and_a_later_success_clears_it()
    {
        var (vm, fake) = await ConnectedVmWithSelectedRowAsync();
        fake.OnTaskOp = (_, _, _) => throw new BoincRpcException("daemon says no");

        await vm.SuspendSelectedCommand.ExecuteAsync(null);
        await _fx.SettleAsync(() => vm.ControlFailure.IsOpen);

        Assert.Equal(string.Format(Strings.ControlOpFailedTitleFmt, Strings.Suspend), vm.ControlFailure.Title);
        Assert.Contains("daemon says no", vm.ControlFailure.Message);

        fake.OnTaskOp = (_, _, _) => Task.CompletedTask;
        await vm.ResumeSelectedCommand.ExecuteAsync(null);
        await _fx.SettleAsync(() => !vm.ControlFailure.IsOpen,
            "an op success must clear the failure surface");
    }

    [Fact]
    public async Task Command_disables_while_its_op_is_in_flight()
    {
        var (vm, fake) = await ConnectedVmWithSelectedRowAsync();
        var gate = new TaskCompletionSource();
        fake.OnTaskOp = (_, _, _) => gate.Task;

        var run = vm.SuspendSelectedCommand.ExecuteAsync(null);
        Assert.False(vm.SuspendSelectedCommand.CanExecute(null));

        gate.SetResult();
        await run;
        await _fx.SettleAsync(() => vm.SuspendSelectedCommand.CanExecute(null),
            "command must re-enable after the op completes");
    }
}
