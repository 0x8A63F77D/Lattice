using Lattice.App.ViewModels;
using Xunit;
using static Lattice.App.ViewModels.RailState;

namespace Lattice.App.Tests;

/// <summary>
/// Transition table for the Tasks view overlay choice. Invariant: Loading =
/// a first fetch is still plausibly in flight; Empty = a Connected host
/// vouches for having no tasks; all-terminal scopes get neither overlay.
/// </summary>
public class TasksOverlayPolicyTests
{
    private static TasksOverlayPolicy.HostFacts Host(RailState tier, bool snap = false) => new(tier, snap);

    public static TheoryData<TasksOverlayPolicy.HostFacts[], bool, bool, bool> Table => new()
    {
        // scoped hosts,                                  hasRows, expLoading, expEmpty
        { [],                                              false,  false, false }, // no hosts: neither
        { [Host(Connecting)],                              false,  true,  false }, // first fetch in flight
        { [Host(Retrying)],                                false,  true,  false }, // below the tier: still plausible
        { [Host(Connected)],                               false,  true,  false }, // connected, snapshot not yet landed
        { [Host(AuthFailed)],                              false,  false, false }, // terminal park: neither (Codex P2)
        { [Host(Unreachable)],                             false,  false, false }, // terminal tier: neither
        { [Host(AuthFailed), Host(Unreachable)],           false,  false, false }, // all-terminal scope: neither
        { [Host(AuthFailed), Host(Connecting)],            false,  true,  false }, // one live host keeps loading alive
        { [Host(Connected, snap: true)],                   false,  false, true  }, // answered, zero tasks: empty
        { [Host(Connected, snap: true)],                   true,   false, false }, // rows present: neither
        { [Host(AuthFailed), Host(Connected, snap: true)], false,  false, true  }, // the connected host vouches
        { [Host(Retrying, snap: true)],                    false,  false, false }, // stale snapshot, nobody connected: neither
    };

    [Theory]
    [MemberData(nameof(Table))]
    public void Decide_matches_the_table(
        TasksOverlayPolicy.HostFacts[] scoped, bool hasRows, bool expectedLoading, bool expectedEmpty)
    {
        var (isLoading, isEmpty) = TasksOverlayPolicy.Decide(scoped, hasRows);
        Assert.Equal(expectedLoading, isLoading);
        Assert.Equal(expectedEmpty, isEmpty);
    }
}
