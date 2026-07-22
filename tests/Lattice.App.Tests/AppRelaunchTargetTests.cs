using Xunit;

namespace Lattice.App.Tests;

/// <summary>
/// App.PlanRelaunch decides how a language-restart relaunches (#147). It must prefer the AppImage
/// launcher ($APPIMAGE) over Environment.ProcessPath — which inside a running AppImage is the
/// in-mount apphost the runtime unmounts on exit — and force extract-and-run for the AppImage so
/// the child starts on FUSE-less hosts too and outlives this instance's mount (Codex P2 ×2, PR #149).
/// </summary>
public class AppRelaunchTargetTests
{
    [Fact]
    public void AppImage_relaunches_the_outer_appimage_with_extract_and_run()
    {
        var plan = App.PlanRelaunch("/home/u/Lattice-x86_64.AppImage", "/tmp/.mount_abc/usr/bin/Lattice");
        Assert.Equal(new App.RelaunchPlan("/home/u/Lattice-x86_64.AppImage", ExtractAndRun: true), plan);
    }

    [Theory]
    // Not an AppImage (win exe, mac/linux-tarball apphost): relaunch the process path, no extract-and-run.
    [InlineData("/Applications/Lattice.app/Contents/MacOS/Lattice")]
    [InlineData("C:/Program Files/Lattice/Lattice.exe")]
    public void Non_appimage_relaunches_the_process_path_without_extract_and_run(string processPath)
        => Assert.Equal(new App.RelaunchPlan(processPath, ExtractAndRun: false), App.PlanRelaunch(null, processPath));

    [Fact]
    public void No_known_path_yields_no_plan()
        => Assert.Null(App.PlanRelaunch(null, null));
}
