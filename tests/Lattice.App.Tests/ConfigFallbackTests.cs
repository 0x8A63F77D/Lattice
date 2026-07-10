using Lattice.Tests;
using Xunit;

namespace Lattice.App.Tests;

/// <summary>
/// Pins App.LoadRegistryWithFallback's two recovery shapes: confirmed-unloadable
/// files are quarantined and the fresh registry reclaims the original path; a
/// file that cannot even be moved aside may still be a valid config, so the
/// fresh registry must bind elsewhere and never overwrite it.
/// </summary>
public class ConfigFallbackTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Corrupt_config_is_quarantined_and_the_fresh_registry_reclaims_the_path()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "config.json");
        File.WriteAllText(path, "{ this is not json");
        try
        {
            var registry = App.LoadRegistryWithFallback(path);

            Assert.Empty(registry.Hosts);
            Assert.False(File.Exists(path));
            Assert.Single(Directory.GetFiles(dir, "config.json.corrupt-*"));

            registry.AddHost(TestData.MakeHostConfig());
            Assert.True(File.Exists(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Unquarantinable_config_is_never_overwritten_by_the_fresh_registry()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "config.json");
        const string original = """{"pollingIntervalSeconds":5,"hosts":[]}""";
        File.WriteAllText(path, original);

        // Make the config unreadable AND unmovable, per platform: on Windows an
        // exclusive open denies both; on Unix strip the file's read bits and the
        // directory's write bit (rename needs directory write).
        FileStream? windowsLock = null;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                windowsLock = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            }
            else
            {
                File.SetUnixFileMode(path, UnixFileMode.None);
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            }

            var registry = App.LoadRegistryWithFallback(path);
            Assert.Empty(registry.Hosts);

            // The transient condition clears while the app is still open — the
            // exact window where a mutation on a registry still bound to the
            // original path would overwrite a config that was valid all along.
            Unlock(dir, path, ref windowsLock);

            registry.AddHost(TestData.MakeHostConfig());

            Assert.Equal(original, File.ReadAllText(path));
            Assert.Single(Directory.GetFiles(dir, "config.json.recovery-*"));
        }
        finally
        {
            Unlock(dir, path, ref windowsLock);
            Directory.Delete(dir, recursive: true);
        }
    }

    private static void Unlock(string dir, string path, ref FileStream? windowsLock)
    {
        windowsLock?.Dispose();
        windowsLock = null;
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(dir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
