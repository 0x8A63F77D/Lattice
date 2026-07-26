using Lattice.App.Infrastructure;

namespace Lattice.App.Tests.Fakes;

/// <summary>
/// Records what the ViewModel/preference layer asked the OS to do, without touching the machine
/// running the suite. <see cref="Fails"/> drives the write-failure path (a read-only home, a
/// sandbox, a Linux box with no config dir).
/// </summary>
public sealed class FakeStartupRegistration : IStartupRegistration
{
    public bool IsSupported { get; set; } = true;
    public bool IsRegistered { get; private set; }

    /// <summary>When true, every <see cref="Apply"/> reports failure and changes nothing.</summary>
    public bool Fails { get; set; }

    /// <summary>Every call, in order, as (enabled, startMinimized).</summary>
    public List<(bool Enabled, bool StartMinimized)> Calls { get; } = [];

    public bool Apply(bool enabled, bool startMinimized)
    {
        Calls.Add((enabled, startMinimized));
        if (Fails)
            return false;
        IsRegistered = enabled;
        return true;
    }
}
