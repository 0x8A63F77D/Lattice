using Xunit;

namespace Lattice.App.Tests;

/// <summary>
/// App.RelaunchTarget picks what a language-restart relaunches (#147). It must prefer the
/// AppImage launcher ($APPIMAGE) over Environment.ProcessPath, which inside a running AppImage
/// is the in-mount apphost the runtime unmounts on exit (Codex P2, PR #149).
/// </summary>
public class AppRelaunchTargetTests
{
    [Theory]
    // Running as an AppImage: $APPIMAGE (outer .AppImage) wins over the in-mount apphost.
    [InlineData("/home/u/Lattice-x86_64.AppImage", "/tmp/.mount_abc/usr/bin/Lattice", "/home/u/Lattice-x86_64.AppImage")]
    // Not an AppImage (win exe, mac/linux-tarball apphost): fall back to the process path.
    [InlineData(null, "/Applications/Lattice.app/Contents/MacOS/Lattice", "/Applications/Lattice.app/Contents/MacOS/Lattice")]
    [InlineData(null, "C:/Program Files/Lattice/Lattice.exe", "C:/Program Files/Lattice/Lattice.exe")]
    // Neither known: no relaunch target (caller no-ops).
    [InlineData(null, null, null)]
    public void Prefers_appimage_then_process_path(string? appImage, string? processPath, string? expected)
        => Assert.Equal(expected, App.RelaunchTarget(appImage, processPath));
}
