using System.Text.Json;
using Lattice.Core;
using Xunit;

namespace Lattice.Tests;

public class LatticeConfigTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}", "config.json");

    [Fact]
    public void Load_returns_default_when_file_missing()
    {
        LatticeConfig config = LatticeConfig.Load(TempPath());
        Assert.Equal(5, config.PollingIntervalSeconds);
        Assert.Empty(config.Hosts);
    }

    [Fact]
    public void Save_then_load_round_trips()
    {
        string path = TempPath();
        var host = new HostConfig(Guid.NewGuid(), "office-pc", "192.168.1.40", 31416, "secret");
        var config = new LatticeConfig(10, [host]);
        config.Save(path);
        LatticeConfig loaded = LatticeConfig.Load(path);
        Assert.Equal(10, loaded.PollingIntervalSeconds);
        Assert.Equal([host], loaded.Hosts);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Save_overwrites_existing_file()
    {
        string path = TempPath();
        new LatticeConfig(5, []).Save(path);
        new LatticeConfig(30, []).Save(path);
        Assert.Equal(30, LatticeConfig.Load(path).PollingIntervalSeconds);
    }

    [Fact]
    public void Load_throws_on_corrupt_file()
    {
        string path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "not json {");
        Assert.ThrowsAny<JsonException>(() => LatticeConfig.Load(path));
    }

    [Fact]
    public void Load_normalizes_missing_pollingIntervalSeconds_to_default()
    {
        string path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"hosts\":[]}");

        LatticeConfig config = LatticeConfig.Load(path);

        Assert.Equal(5, config.PollingIntervalSeconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public void Load_normalizes_unsupported_pollingIntervalSeconds_to_default(int seconds)
    {
        string path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"{{\"pollingIntervalSeconds\":{seconds},\"hosts\":[]}}");

        LatticeConfig config = LatticeConfig.Load(path);

        Assert.Equal(5, config.PollingIntervalSeconds);
    }

    [Fact]
    public void Load_keeps_valid_pollingIntervalSeconds_unchanged()
    {
        string path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"pollingIntervalSeconds\":30,\"hosts\":[]}");

        LatticeConfig config = LatticeConfig.Load(path);

        Assert.Equal(30, config.PollingIntervalSeconds);
    }

    [Fact]
    public void Load_normalizes_missing_hosts_to_empty_list()
    {
        string path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"pollingIntervalSeconds\":10}");

        LatticeConfig config = LatticeConfig.Load(path);

        Assert.NotNull(config.Hosts);
        Assert.Empty(config.Hosts);
    }

    [Fact]
    public void Load_normalizes_missing_hosts_and_still_round_trips_after_save()
    {
        string path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"pollingIntervalSeconds\":10}");

        LatticeConfig loaded = LatticeConfig.Load(path);
        loaded.Save(path);
        LatticeConfig reloaded = LatticeConfig.Load(path);

        Assert.Equal(10, reloaded.PollingIntervalSeconds);
        Assert.Empty(reloaded.Hosts);
    }

    [Fact]
    public void FullSpeedHiddenPolling_defaults_false_and_round_trips_when_true()
    {
        string path = TempPath();
        var config = new LatticeConfig(10, []) { FullSpeedHiddenPolling = true };
        config.Save(path);
        LatticeConfig loaded = LatticeConfig.Load(path);
        Assert.True(loaded.FullSpeedHiddenPolling);

        Assert.False(new LatticeConfig(5, []).FullSpeedHiddenPolling);
        Assert.False(LatticeConfig.Default.FullSpeedHiddenPolling);
    }

    [Fact]
    public void Load_defaults_fullSpeedHiddenPolling_false_for_pre_92_config()
    {
        // A config.json written before #92 has no fullSpeedHiddenPolling key; the new
        // positional component's default must fill in as false so old files round-trip.
        string path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            "{\"pollingIntervalSeconds\":10,\"hosts\":[]}");

        LatticeConfig config = LatticeConfig.Load(path);

        Assert.Equal(10, config.PollingIntervalSeconds);
        Assert.False(config.FullSpeedHiddenPolling);
    }

    [Fact]
    public void DisplayName_falls_back_to_address()
    {
        var host = new HostConfig(Guid.NewGuid(), "", "10.0.0.5", 31416, "");
        Assert.Equal("10.0.0.5", host.DisplayName);
        Assert.Equal("named", (host with { Name = "named" }).DisplayName);
    }

    [Fact]
    public void Save_creates_file_with_user_only_permissions_on_unix()
    {
        // Windows has no POSIX mode bits; ACLs there inherit from the user profile
        // directory instead. This assertion is only meaningful on the ubuntu CI leg.
        if (OperatingSystem.IsWindows())
            return;

        string path = TempPath();
        new LatticeConfig(5, []).Save(path);

        UnixFileMode mode = File.GetUnixFileMode(path);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    [Fact]
    public void Save_deletes_stale_tmp_before_recreating_it()
    {
        // A stale path.tmp (e.g. orphaned by an earlier serialize failure) must not be
        // reused: FileMode.Create on an EXISTING file truncates it in place without
        // applying UnixCreateMode, so a tmp left over from before the permissions fix
        // (or simply created under a looser umask) would carry group/world-readable
        // bits through the rename and into config.json, which holds plaintext
        // passwords. Save must delete the stale tmp first so it is freshly CREATED
        // (not reused) with user-only permissions.
        string path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, "stale junk from an earlier failed save");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(tmp,
                UnixFileMode.UserRead | UnixFileMode.UserWrite
                | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        var host = new HostConfig(Guid.NewGuid(), "office-pc", "192.168.1.40", 31416, "secret");
        var config = new LatticeConfig(10, [host]);
        config.Save(path);

        // Round-trips correctly on every platform even with a stale tmp present.
        LatticeConfig loaded = LatticeConfig.Load(path);
        Assert.Equal(10, loaded.PollingIntervalSeconds);
        Assert.Equal([host], loaded.Hosts);
        Assert.False(File.Exists(tmp));

        // Windows has no POSIX mode bits; this assertion is only meaningful on the
        // ubuntu/macos CI legs, matching Save_creates_file_with_user_only_permissions_on_unix.
        if (OperatingSystem.IsWindows())
            return;
        UnixFileMode mode = File.GetUnixFileMode(path);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }
}
