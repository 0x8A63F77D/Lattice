using Lattice.Core;
using Xunit;

namespace Lattice.Tests;

public class HostRegistryTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}", "config.json");

    private static HostConfig NewHost(string name = "h1") =>
        new(Guid.NewGuid(), name, "localhost", 31416, "pw");

    [Fact]
    public void AddHost_persists_and_raises()
    {
        string path = TempPath();
        var registry = new HostRegistry(LatticeConfig.Default, path);
        List<RegistryChangedEventArgs> events = [];
        registry.Changed += (_, e) => events.Add(e);

        HostConfig host = NewHost();
        registry.AddHost(host);

        Assert.Equal([host], registry.Hosts);
        Assert.Equal([host], LatticeConfig.Load(path).Hosts);
        Assert.Equal([new RegistryChangedEventArgs(RegistryChangeKind.HostAdded, host)], events);
    }

    [Fact]
    public void AddHost_rejects_duplicate_id()
    {
        var registry = new HostRegistry(LatticeConfig.Default, TempPath());
        HostConfig host = NewHost();
        registry.AddHost(host);
        Assert.Throws<ArgumentException>(() => registry.AddHost(host with { Name = "other" }));
    }

    [Fact]
    public void UpdateHost_replaces_matching_id()
    {
        string path = TempPath();
        HostConfig host = NewHost();
        var registry = new HostRegistry(new LatticeConfig(5, [host]), path);
        List<RegistryChangedEventArgs> events = [];
        registry.Changed += (_, e) => events.Add(e);

        HostConfig updated = host with { Address = "10.0.0.9" };
        registry.UpdateHost(updated);

        Assert.Equal([updated], registry.Hosts);
        Assert.Equal([updated], LatticeConfig.Load(path).Hosts);
        Assert.Equal(RegistryChangeKind.HostUpdated, events.Single().Kind);
        Assert.Throws<ArgumentException>(() => registry.UpdateHost(NewHost("unknown")));
    }

    [Fact]
    public void RemoveHost_removes_and_raises()
    {
        string path = TempPath();
        HostConfig host = NewHost();
        var registry = new HostRegistry(new LatticeConfig(5, [host]), path);
        List<RegistryChangedEventArgs> events = [];
        registry.Changed += (_, e) => events.Add(e);

        registry.RemoveHost(host.Id);

        Assert.Empty(registry.Hosts);
        Assert.Empty(LatticeConfig.Load(path).Hosts);
        Assert.Equal([new RegistryChangedEventArgs(RegistryChangeKind.HostRemoved, host)], events);
        Assert.Throws<ArgumentException>(() => registry.RemoveHost(Guid.NewGuid()));
    }

    [Fact]
    public void SetPollingInterval_validates_and_persists()
    {
        string path = TempPath();
        var registry = new HostRegistry(LatticeConfig.Default, path);
        List<RegistryChangedEventArgs> events = [];
        registry.Changed += (_, e) => events.Add(e);

        Assert.Throws<ArgumentOutOfRangeException>(() => registry.SetPollingInterval(7));
        Assert.Empty(events);

        registry.SetPollingInterval(30);
        Assert.Equal(30, registry.PollingIntervalSeconds);
        Assert.Equal(30, LatticeConfig.Load(path).PollingIntervalSeconds);
        Assert.Equal(RegistryChangeKind.IntervalChanged, events.Single().Kind);
    }

    [Fact]
    public void Load_reads_existing_file()
    {
        string path = TempPath();
        HostConfig host = NewHost();
        new LatticeConfig(10, [host]).Save(path);
        HostRegistry registry = HostRegistry.Load(path);
        Assert.Equal([host], registry.Hosts);
        Assert.Equal(10, registry.PollingIntervalSeconds);
    }
}
