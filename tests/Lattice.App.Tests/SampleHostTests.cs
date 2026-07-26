#if DEBUG
using Avalonia.Media;
using Lattice.App.Infrastructure;
using Lattice.App.ViewModels;
using Lattice.Boinc.GuiRpc;
using Lattice.Core;
using Lattice.Tests;
using Xunit;

namespace Lattice.App.Tests;

/// <summary>
/// PR F machine gate (shell-design §12.4): the DEBUG sample fleet, fed through the
/// SAME registry → HostMonitor → SnapshotBuilder → HostStore → ViewModel path as a
/// real host (the HostGraphFixture fake-fed pattern), must surface the data-rich
/// states the live daemon cannot (0 attached projects): 500+ task rows
/// (virtualization), transfers in every state, and a multi-host Projects aggregate
/// with <c>Varies</c> share + a mixed status tier.
///
/// This suite is DEBUG-only by construction — <see cref="SampleHost"/> does not
/// exist in Release (SampleHostReleaseExclusionTests enforces that). The gate runs
/// on a local DEBUG `dotnet test`; the Release CI legs run the exclusion test.
/// </summary>
public class SampleHostTests : IAsyncLifetime
{
    private HostGraphFixture _fx = null!;

    public ValueTask InitializeAsync()
    {
        _fx = new HostGraphFixture();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _fx.DisposeAsync();

    // Seeds the whole canned fleet into the fixture, each host's raw RPC replies
    // driving one fake keyed by its address — exactly what SampleRoutingGuiRpcClient
    // does in the app. Built against the fixture's frozen clock so retry countdowns
    // classify in the future.
    private void SeedFleet()
    {
        foreach (SampleHostData data in SampleHost.BuildHosts(_fx.MonitorTime.GetUtcNow()))
            Seed(data);
    }

    private void Seed(SampleHostData data)
    {
        var fake = new FakeGuiRpcClient
        {
            OnExchangeVersions = () => Task.FromResult(data.State.CoreClientVersion),
            OnGetState = () => Task.FromResult(data.State),
            OnGetCcStatus = () => Task.FromResult(data.Status),
            OnGetResults = _ => Task.FromResult(data.Results),
            OnGetFileTransfers = () => Task.FromResult(data.Transfers),
            OnGetMessages = seqno =>
                Task.FromResult<IReadOnlyList<Message>>([.. data.Messages.Where(m => m.Seqno > seqno)]),
            OnGetStatistics = () => Task.FromResult(data.Statistics),
        };
        _fx.AddHost(data.Config.Address, fake, name: data.Config.Name);
    }

    [Fact]
    public async Task Tasks_grid_materializes_more_than_500_rows()
    {
        SeedFleet();
        var vm = new TasksViewModel(_fx.Store, _fx.Clock, _fx.UiState, _fx.Density, _fx.Control);
        _fx.Start();

        // All-hosts scope merges every sample host; the busy host alone carries
        // 520, so the merged grid clears the 500-row virtualization bar.
        await _fx.SettleAsync(() => vm.Rows.Count >= 500, "the sample fleet should materialize 500+ task rows");

        Assert.True(vm.IsAllHostsScope);
        Assert.True(vm.Rows.Count >= 500, $"expected 500+ task rows, saw {vm.Rows.Count}");
    }

    [Fact]
    public async Task Transfers_grid_shows_every_transfer_state()
    {
        SeedFleet();
        var vm = new TransfersViewModel(_fx.Store, _fx.Clock, _fx.Density);
        _fx.Start();

        await _fx.SettleAsync(
            () => vm.Rows.Select(r => r.Data.UiState).Distinct().Count() == 3,
            "the fleet should surface Active, Retrying and Queued transfers");

        var states = vm.Rows.Select(r => r.Data.UiState).ToHashSet();
        Assert.Contains(TransferUiState.Active, states);
        Assert.Contains(TransferUiState.Retrying, states);
        Assert.Contains(TransferUiState.Queued, states);
    }

    [Fact]
    public async Task Projects_grid_shows_a_varies_share_and_a_mixed_status_aggregate()
    {
        SeedFleet();
        var vm = new ProjectsViewModel(_fx.Store, _fx.Clock, _fx.Control);
        _fx.Start();

        // Three distinct master URLs → three parent aggregates in All-hosts scope.
        await _fx.SettleAsync(() => vm.Rows.Count == 3, "the fleet has three attached projects");
        Assert.True(vm.IsAllHostsScope);

        // Einstein@Home is attached on all three hosts with differing resource
        // shares (100/50/200) and differing status (active/suspended/no-new-tasks):
        // Varies share (no uniform bar) + a Mixed status tier — neither reachable
        // from a single host.
        var einstein = vm.Rows.Single(r => r.Data.Name == "Einstein@Home");
        Assert.True(einstein.Data.IsParent);
        Assert.False(einstein.Data.ShowShareBar, "a Varies share renders the range text, not a uniform bar");
        Assert.Equal(ProjectStatusKind.Mixed, einstein.Data.StatusKind);
    }
    // ---- the opt-in eleven-project host (issue #171) ----------------------

    [Fact]
    public void The_walkthrough_fleet_is_unchanged_by_the_many_project_host()
    {
        // It is behind its own flag precisely so the three-host aggregation demo keeps its
        // exact project set; this pins that the default fleet never gains a fourth host.
        var now = _fx.MonitorTime.GetUtcNow();
        Assert.Equal(3, SampleHost.BuildHosts(now).Count);
        Assert.DoesNotContain(SampleHost.BuildHosts(now), h => h.Config.Name == "Sample · Delta");
        Assert.Equal(4, SampleHost.BuildHosts(now, includeManyProjects: true).Count);
    }

    [Fact]
    public async Task The_eleven_project_host_gives_every_visible_series_its_own_colour()
    {
        // The reason this host exists: past ten projects two of them prefer the same palette
        // slot, and Delta's RACs put exactly that pair — World Community Grid (ordinal 0) and
        // SiDock@home (ordinal 10) — in the default-visible six. The old colour-by-ordinal
        // model painted both cornflower; every visible series now holds its own slot.
        SampleHostData delta = SampleHost.BuildHosts(_fx.MonitorTime.GetUtcNow(), includeManyProjects: true)[^1];
        Seed(delta);
        var vm = new StatisticsViewModel(_fx.Store, _fx.Clock);
        _fx.Start();

        await _fx.SettleAsync(() => vm.HasChart && vm.Chips.Count == 6, "the eleven-project host caps its legend at six chips");

        Assert.Equal([0, 1, 2, 3, 4, 10], vm.Chips.Select(c => c.Ordinal));
        Assert.Equal("World Community Grid", vm.Chips[0].Name);
        Assert.Equal("SiDock@home", vm.Chips[^1].Name);

        var swatches = vm.Chips.Select(c => ((SolidColorBrush)c.Swatch!).Color).ToList();
        Assert.Equal(6, swatches.Distinct().Count());

        // Five projects left over → the "+5 more" flyout has real rows to click.
        Assert.Equal(5, vm.Overflow.Count);
        vm.Dispose();
    }

}
#endif
