using Lattice.Boinc.GuiRpc;
using Lattice.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Lattice.Tests.Interleaving;

public class SweepTests
{
    private static HostConfig Config(string password = "pw") =>
        new(Guid.NewGuid(), "test", "localhost", 31416, password);

    [Fact]
    public async Task Probe_seam_freezes_and_releases_at_designated_point()
    {
        var fake = new FakeGuiRpcClient();
        var controller = new ProbeController();
        await using var monitor = new HostMonitor(Config(), () => fake, new FakeTimeProvider(), 5);

        monitor.InterleaveProbe = controller.Probe;
        controller.FreezeAt(InterleavePoints.BeforeAcceptGuard);
        monitor.Start();

        await controller.WaitForAsync(InterleavePoints.BeforeAcceptGuard);
        Assert.Equal(HostConnectionState.FetchingState, monitor.Status.State);

        controller.Release();
        await Wait.UntilAsync(() => monitor.Status.State == HostConnectionState.Connected);
    }
}
