#if DEBUG
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
            };
            _fx.AddHost(data.Config.Address, fake, name: data.Config.Name);
        }
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
}
#endif
