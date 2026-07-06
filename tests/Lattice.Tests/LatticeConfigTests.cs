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
    public void DisplayName_falls_back_to_address()
    {
        var host = new HostConfig(Guid.NewGuid(), "", "10.0.0.5", 31416, "");
        Assert.Equal("10.0.0.5", host.DisplayName);
        Assert.Equal("named", (host with { Name = "named" }).DisplayName);
    }
}
