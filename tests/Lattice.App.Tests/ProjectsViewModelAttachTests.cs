using Lattice.App.Localization;
using Lattice.App.Tests.Fakes;
using Lattice.App.ViewModels;
using Lattice.Boinc.GuiRpc;
using Lattice.Core;
using Lattice.Tests;
using Xunit;

namespace Lattice.App.Tests;

/// <summary>
/// M3 PR I gates for the Projects view's "Add project…" entry: DI-3 enablement +
/// the disabled tooltip reason vs connection state, and the ready-built dialog VM
/// the command raises (host options + scope lock). Composition/dispatcher/settle
/// come from HostGraphFixture.
/// </summary>
public class ProjectsViewModelAttachTests : IAsyncLifetime
{
    private HostGraphFixture _fx = null!;
    private readonly List<AttachProjectViewModel> _raised = [];

    public ValueTask InitializeAsync()
    {
        _fx = new HostGraphFixture();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _fx.DisposeAsync();

    private static Task<AttachFlowResult> NoopRun(
        Guid id, AttachMachine.AttachRequest r, IProgress<AttachMachine.Stage>? p, CancellationToken ct) =>
        Task.FromResult(new AttachFlowResult(AttachFlowOutcome.Attached, [], null));

    private ProjectsViewModel MakeVm()
    {
        var vm = new ProjectsViewModel(_fx.Store, _fx.Clock, _fx.Control, NoopRun, new ImmediateUiDispatcher());
        vm.AddProjectRequested += (_, avm) => _raised.Add(avm);
        return vm;
    }

    [Fact]
    public void Disabled_with_a_reason_when_no_host_is_connected()
    {
        _fx.AddHost("host-a", new FakeGuiRpcClient());   // never started ⇒ Disconnected
        var vm = MakeVm();

        Assert.False(vm.AddProjectCommand.CanExecute(null));
        Assert.Equal(Strings.ProjectsAddProjectDisabledReason, vm.AddProjectDisabledReason);
    }

    [Fact]
    public async Task Enabled_with_no_reason_once_a_host_is_connected()
    {
        _fx.AddHost("host-a", new FakeGuiRpcClient());
        var vm = MakeVm();
        _fx.Start();

        await _fx.SettleAsync(() => vm.AddProjectCommand.CanExecute(null),
            "the command enables once the host is Connected");
        Assert.Null(vm.AddProjectDisabledReason);
    }

    [Fact]
    public async Task Add_project_raises_a_dialog_vm_with_the_connected_host()
    {
        _fx.AddHost("host-a", new FakeGuiRpcClient());
        var vm = MakeVm();
        _fx.Start();
        await _fx.SettleAsync(() => vm.AddProjectCommand.CanExecute(null));

        vm.AddProjectCommand.Execute(null);

        var dialogVm = Assert.Single(_raised);
        var option = Assert.Single(dialogVm.Hosts);
        Assert.Equal("host-a", option.DisplayName);
        // The sole connectable option is pre-selected (all-hosts scope, one host).
        Assert.Same(option, dialogVm.SelectedHost);
    }
}
