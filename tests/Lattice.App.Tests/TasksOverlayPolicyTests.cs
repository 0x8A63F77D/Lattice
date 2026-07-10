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
        // hasTasks is UNFILTERED task presence (a filter miss is not empty).
        // scoped hosts,                                 hasTasks, expLoading, expEmpty
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
        // Codex P2 round 6: a stale snapshot (cached but hidden by the grid's
        // Connected-only filter) must count for NOTHING — it neither suppresses
        // the first-fetch loading state nor vouches for emptiness.
        { [Host(Connected), Host(Retrying, snap: true)],   false,  true,  false }, // THE finding: visible host still fetching; stale snap must not hide loading
        { [Host(Retrying, snap: true), Host(Connecting)],  false,  true,  false }, // stale host doesn't suppress a genuinely pending one
        { [Host(Connected, snap: true), Host(Retrying, snap: true)], false, false, true }, // a contributing host suppresses loading and vouches empty
        { [Host(Connected, snap: true), Host(Retrying, snap: true)], true,  false, false }, // rows present: neither, stale host irrelevant
    };

    [Theory]
    [MemberData(nameof(Table))]
    public void Decide_matches_the_table(
        TasksOverlayPolicy.HostFacts[] scoped, bool hasTasks, bool expectedLoading, bool expectedEmpty)
    {
        var (isLoading, isEmpty) = TasksOverlayPolicy.Decide(scoped, hasTasks);
        Assert.Equal(expectedLoading, isLoading);
        Assert.Equal(expectedEmpty, isEmpty);
    }
}
