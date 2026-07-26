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

    /// <summary>Settable so a test can stage "a record exists that this session did not
    /// write" — the launch-time self-heal case — and "the OS switched it off behind us".</summary>
    public bool IsRegistered { get; set; }

    /// <summary>When true, every call reports failure and changes nothing.</summary>
    public bool Fails { get; set; }

    /// <summary>When true, <see cref="Apply"/> reports success without the OS state actually
    /// moving — the shape of a macOS enable whose launchd disable could not be cleared.</summary>
    public bool SucceedsWithoutRegistering { get; set; }

    /// <summary>Every <see cref="Apply"/>, in order, as (enabled, startMinimized). Kept
    /// separate from <see cref="Heals"/> because the whole point of the two methods is that
    /// they are NOT interchangeable: Apply is the user's explicit request and may clear an
    /// OS-level disable, Heal may not.</summary>
    public List<(bool Enabled, bool StartMinimized)> Calls { get; } = [];

    /// <summary>Every <see cref="Heal"/>, in order, as the requested startMinimized flag.</summary>
    public List<bool> Heals { get; } = [];

    public bool Apply(bool enabled, bool startMinimized)
    {
        Calls.Add((enabled, startMinimized));
        if (Fails)
            return false;
        if (!SucceedsWithoutRegistering)
            IsRegistered = enabled;
        return true;
    }

    public bool Heal(bool startMinimized)
    {
        // Mirrors the real contract: nothing registered (or switched off by the OS, which the
        // production readers fold into IsRegistered) means nothing to repair.
        if (!IsRegistered)
            return true;
        Heals.Add(startMinimized);
        return !Fails;
    }
}
