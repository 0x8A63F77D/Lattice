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
    public void SetFullSpeedHiddenPolling_persists_and_raises_intervalchanged()
    {
        string path = TempPath();
        var registry = new HostRegistry(LatticeConfig.Default, path);
        List<RegistryChangedEventArgs> events = [];
        registry.Changed += (_, e) => events.Add(e);

        Assert.False(registry.FullSpeedHiddenPolling);

        registry.SetFullSpeedHiddenPolling(true);
        Assert.True(registry.FullSpeedHiddenPolling);
        Assert.True(LatticeConfig.Load(path).FullSpeedHiddenPolling);
        // Reuses IntervalChanged rather than adding a RegistryChangeKind case (plan Part 4).
        Assert.Equal(RegistryChangeKind.IntervalChanged, events.Single().Kind);
        Assert.Null(events.Single().Host);
        // The polling interval itself is untouched by the flag change.
        Assert.Equal(LatticeConfig.Default.PollingIntervalSeconds, registry.PollingIntervalSeconds);
    }

    [Fact]
    public void SetFullSpeedHiddenPolling_is_noop_when_unchanged()
    {
        string path = TempPath();
        var registry = new HostRegistry(LatticeConfig.Default, path);
        List<RegistryChangedEventArgs> events = [];
        registry.Changed += (_, e) => events.Add(e);

        // Already false: setting false again neither persists nor raises.
        registry.SetFullSpeedHiddenPolling(false);
        Assert.Empty(events);

        registry.SetFullSpeedHiddenPolling(true);
        registry.SetFullSpeedHiddenPolling(true);
        Assert.Single(events);
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

    [Fact]
    public void Mutation_that_fails_to_save_leaves_registry_state_unchanged()
    {
        // Make the config's parent "directory" an existing file, so
        // Directory.CreateDirectory inside Save throws IOException. If Mutate swaps
        // _config to the new value before Save succeeds, memory would diverge from
        // disk (and from every already-connected monitor holding the old config).
        string bogusParent = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}");
        File.WriteAllText(bogusParent, "not a directory");
        string path = Path.Combine(bogusParent, "config.json");
        try
        {
            var registry = new HostRegistry(LatticeConfig.Default, path);
            List<RegistryChangedEventArgs> events = [];
            registry.Changed += (_, e) => events.Add(e);

            Assert.ThrowsAny<IOException>(() => registry.AddHost(NewHost()));

            Assert.Empty(registry.Hosts);
            Assert.Empty(events);
        }
        finally
        {
            File.Delete(bogusParent);
        }
    }
}
