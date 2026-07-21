using System.Globalization;
using Lattice.App.Localization;
using Lattice.App.ViewModels;
using Lattice.Boinc.GuiRpc;
using Lattice.Core;
using Lattice.Tests;
using Xunit;

namespace Lattice.App.Tests;

/// <summary>
/// M3 PR H (DI-4) run-mode / snooze surface gates: the rail host VM's
/// intent → wire-args → HostControlService.SetModeAsync chain for every menu
/// item, DI-3 enablement vs connection state, the "Snoozed until" chip
/// derivation from a frozen TimeProvider, and the shell's scoped-host surface
/// (present when a single host is scoped, absent in All-hosts scope).
/// </summary>
public class RunModeControlTests : IAsyncLifetime
{
    private HostGraphFixture _fx = null!;

    public ValueTask InitializeAsync()
    {
        _fx = new HostGraphFixture();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _fx.DisposeAsync();

    // A rail VM over a Connected host: the DI-3 precondition for run-mode ops.
    private async Task<(HostRailItemViewModel Vm, FakeGuiRpcClient Fake)> ConnectedRailVmAsync(
        FakeGuiRpcClient? fake = null)
    {
        fake ??= new FakeGuiRpcClient();
        _fx.AddHost("host-a", fake);
        _fx.Start();
        await _fx.SettleAsync(() => _fx.Store.Hosts[0].Status.State == HostConnectionState.Connected);
        var vm = new HostRailItemViewModel(_fx.Store.Hosts[0], _fx.Clock, _fx.Control);
        return (vm, fake);
    }

    // --- DI-3 enablement ---

    [Fact]
    public async Task Run_mode_disabled_until_the_host_is_connected()
    {
        _fx.AddHost("host-a", new FakeGuiRpcClient());
        // Constructed BEFORE Start: the entry is not yet Connected.
        var vm = new HostRailItemViewModel(_fx.Store.Hosts[0], _fx.Clock, _fx.Control);
        Assert.False(vm.SetRunModeCommand.CanExecute("cpu:never"));

        _fx.Start();
        await _fx.SettleAsync(() => _fx.Store.Hosts[0].Status.State == HostConnectionState.Connected);
        vm.Refresh();

        Assert.True(vm.SetRunModeCommand.CanExecute("cpu:never"));
    }

    // --- intent → wire-args → SetModeAsync, every menu item ---

    [Theory]
    [InlineData("cpu:always", "set_mode:cpu:always:0")]
    [InlineData("cpu:auto", "set_mode:cpu:auto:0")]
    [InlineData("cpu:never", "set_mode:cpu:never:0")]
    [InlineData("gpu:always", "set_mode:gpu:always:0")]
    [InlineData("gpu:auto", "set_mode:gpu:auto:0")]
    [InlineData("gpu:never", "set_mode:gpu:never:0")]
    [InlineData("net:always", "set_mode:network:always:0")]
    [InlineData("net:auto", "set_mode:network:auto:0")]
    [InlineData("net:never", "set_mode:network:never:0")]
    // Snooze = temporary CPU Never with the duration in seconds (design 1.4).
    [InlineData("snooze:15", "set_mode:cpu:never:900")]
    [InlineData("snooze:60", "set_mode:cpu:never:3600")]
    [InlineData("snooze:240", "set_mode:cpu:never:14400")]
    // Un-snooze = restore on the CPU lane.
    [InlineData("resume", "set_mode:cpu:restore:0")]
    public async Task Every_menu_token_maps_to_its_wire_call(string token, string expectedCall)
    {
        var (vm, fake) = await ConnectedRailVmAsync();

        await vm.SetRunModeCommand.ExecuteAsync(token);
        await _fx.SettleAsync(() => fake.Calls.Contains(expectedCall),
            $"token '{token}' must issue '{expectedCall}'");

        Assert.Contains(expectedCall, fake.Calls);
    }

    [Fact]
    public async Task Failure_surfaces_on_the_rows_action_result_line()
    {
        var (vm, _) = await ConnectedRailVmAsync(new FakeGuiRpcClient
        {
            OnSetMode = (_, _, _) => throw new BoincRpcException("daemon says no"),
        });

        await vm.SetRunModeCommand.ExecuteAsync("cpu:never");

        Assert.NotNull(vm.TestResultText);
        Assert.Contains("daemon says no", vm.TestResultText);
    }

    // --- "Snoozed until hh:mm" chip derivation (frozen TimeProvider) ---

    private static CcStatus StatusWithCpuDelay(double delaySeconds) => new(
        RunMode.Never, RunMode.Auto, RunMode.Auto,
        SuspendReason.UserRequest, SuspendReason.NotSuspended, SuspendReason.NotSuspended,
        RunMode.Auto, delaySeconds, RunMode.Auto, 0, RunMode.Auto, 0);

    [Fact]
    public async Task Snooze_chip_shows_the_local_deadline_while_a_cpu_override_is_active()
    {
        // Align the monitor clock to the UI clock's default instant so the derived
        // deadline (stamp + 15 min) is still in the UI clock's future — the chip only
        // shows while the override is live (it retires on the UI clock past the deadline).
        _fx.MonitorTime.SetUtcNow(_fx.Clock.Now);
        var fake = new FakeGuiRpcClient { OnGetCcStatus = () => Task.FromResult(StatusWithCpuDelay(900)) };
        _fx.AddHost("host-a", fake);
        _fx.Start();
        await _fx.SettleAsync(() => _fx.Store.Hosts[0].Snapshot is { CcStatus.TaskModeDelaySeconds: 900 });

        var vm = new HostRailItemViewModel(_fx.Store.Hosts[0], _fx.Clock, _fx.Control);

        // The deadline is snapshot.Timestamp (the monitor's frozen TimeProvider
        // instant) + the CPU mode_delay, resolved to local wall-clock AT the deadline
        // (add-then-convert, so a DST-spanning snooze stays correct — Codex R1 P2).
        DateTimeOffset stamp = _fx.Store.Hosts[0].Snapshot!.Timestamp;
        string expected = string.Format(
            Strings.RailSnoozedUntilFmt,
            stamp.AddSeconds(900).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture));

        Assert.True(vm.IsSnoozed);
        Assert.Equal(expected, vm.SnoozedUntilText);
    }

    [Fact]
    public async Task Snooze_chip_retires_on_the_ui_clock_when_the_deadline_passes()
    {
        _fx.MonitorTime.SetUtcNow(_fx.Clock.Now);
        var fake = new FakeGuiRpcClient { OnGetCcStatus = () => Task.FromResult(StatusWithCpuDelay(900)) };
        _fx.AddHost("host-a", fake);
        _fx.Start();
        await _fx.SettleAsync(() => _fx.Store.Hosts[0].Snapshot is { CcStatus.TaskModeDelaySeconds: 900 });
        var vm = new HostRailItemViewModel(_fx.Store.Hosts[0], _fx.Clock, _fx.Control);
        Assert.True(vm.IsSnoozed);

        // Advance the UI clock past the 15-minute deadline: the tick retires the chip
        // even though no fresh poll (reporting delay 0) has arrived yet.
        _fx.Clock.Advance(TimeSpan.FromMinutes(16));

        Assert.False(vm.IsSnoozed);
        Assert.Equal("", vm.SnoozedUntilText);
    }

    [Fact]
    public async Task Snooze_chip_is_absent_without_a_cpu_override()
    {
        // Default status: every mode_delay is 0 → no temporary override.
        var (vm, _) = await ConnectedRailVmAsync();

        Assert.False(vm.IsSnoozed);
        Assert.Equal("", vm.SnoozedUntilText);
    }

    // --- shell scoped-host surface (DI-4: present single-host, absent All-hosts) ---

    private ShellViewModel MakeShell() =>
        new(_fx.Registry, _fx.Store, _fx.Clock, _fx.UiState, () => new FakeGuiRpcClient());

    [Fact]
    public void No_run_mode_surface_in_all_hosts_scope()
    {
        _fx.AddHost("host-a");
        _fx.AddHost("host-b");
        using var shell = MakeShell();

        // Default scope is All hosts (two hosts → genuinely aggregated).
        Assert.True(shell.Scope.IsAllHosts);
        Assert.False(shell.HasScopedHost);
        Assert.Null(shell.ScopedHostRow);
        Assert.Null(shell.Tasks.ScopedHost);
    }

    [Fact]
    public void Run_mode_surface_appears_for_the_scoped_host()
    {
        HostConfig a = _fx.AddHost("host-a");
        _fx.AddHost("host-b");
        using var shell = MakeShell();

        shell.SelectHostScope(a.Id);

        Assert.True(shell.HasScopedHost);
        Assert.NotNull(shell.ScopedHostRow);
        Assert.Equal(a.Id, shell.ScopedHostRow!.HostId);
        // The Tasks command-bar dropdown reaches the same row VM via the push.
        Assert.Same(shell.ScopedHostRow, shell.Tasks.ScopedHost);

        // Returning to All hosts withdraws the surface.
        shell.SelectAllHostsScope();
        Assert.False(shell.HasScopedHost);
        Assert.Null(shell.Tasks.ScopedHost);
    }
}
