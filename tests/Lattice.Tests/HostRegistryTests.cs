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

    // --- published-list immutability (Codex R3 P2 on PR #196) -----------------------
    //
    // The class doc promises that a reader enumerating Hosts on any thread cannot see the
    // list change underneath it. That promise is only worth making if it survives an
    // ill-behaved caller, since the constructor is public API in a library: both tests below
    // fail without HostRegistry.Sealed's defensive copy + ReadOnlyCollection wrapper.

    [Fact]
    public void A_caller_that_keeps_its_host_list_cannot_mutate_the_registrys_view()
    {
        // The caller hands in a MUTABLE list and holds on to it — the shape a defensive
        // copy exists for. Mutating it afterwards must not reach the registry, whose
        // readers may be mid-enumeration on a control lane.
        List<HostConfig> retained = [NewHost("a")];
        var registry = new HostRegistry(new LatticeConfig(5, retained), TempPath());

        retained.Add(NewHost("smuggled"));
        retained[0] = NewHost("swapped");

        Assert.Single(registry.Hosts);
        Assert.Equal("a", registry.Hosts[0].Name);
    }

    [Fact]
    public void Hosts_cannot_be_downcast_to_a_mutable_list()
    {
        // The other half: IReadOnlyList is a VIEW, not a guarantee — a consumer holding one
        // whose runtime type is List<T> can cast the promise away. Both publication paths are
        // pinned because both once produced a List: the ctor whenever a caller passes one, and
        // UpdateHost/RemoveHost, which build `List<HostConfig> hosts = [.. Config.Hosts]` and
        // publish that very instance. (AddHost would not discriminate — its collection
        // expression already yields an array — so asserting on it alone is a false green.)
        HostConfig a = NewHost("a");
        var registry = new HostRegistry(new LatticeConfig(5, new List<HostConfig> { a }), TempPath());
        Assert.Null(registry.Hosts as List<HostConfig>);

        registry.UpdateHost(a with { Name = "renamed" });
        Assert.Null(registry.Hosts as List<HostConfig>);
        Assert.Equal("renamed", Assert.Single(registry.Hosts).Name);
    }
}
