module Lattice.Aggregation.Tests.StatisticsVisibilityTests

open System
open Xunit
open FsCheck.Xunit
open Lattice.App.Aggregation
open Lattice.App.Aggregation.StatisticsVisibility

// The Statistics page's visibility/colour state machine (issue #191). Every transition below was
// previously inline in StatisticsViewModel.Rebuild/TryApplyToggle, threaded through four mutable
// fields; the table here is that logic stated as a table, which is the point of extracting it.
//
// Section headings map 1:1 onto the transitions the old code encoded, so a reader can check the
// migration for holes rather than trust it (the coverage audit in the PR body is this list).

// ---- fixtures ------------------------------------------------------------

let private hostA = Guid("aaaaaaaa-0000-0000-0000-000000000000")
let private hostB = Guid("bbbbbbbb-0000-0000-0000-000000000000")

let private url i = sprintf "p%d" i

/// Project i of a synthetic host: ordinal i, the given RAC. Daily history is irrelevant to
/// visibility (the chart layer reads it) — `charted` is what guarantees there is some.
let private proj i rac : ProjectHistory =
    { MasterUrl = url i
      Name = sprintf "Project %d" i
      Ordinal = i
      Rac = rac
      Daily = [] }

/// n projects whose RAC descends with the ordinal, so the top-6-by-RAC are ordinals 0-5.
let private descending n = [ for i in 0 .. n - 1 -> proj i (float (n - i)) ]

let private data projects = Charted(hostA, projects)

/// Settle onto `projects`, starting from `state`.
let private settle projects state = (step (data projects) Settle state).State

/// Settle from scratch — the state a page reaches on its first poll of a host.
let private fresh projects = settle projects initial

let private toggleOn urlToShow projects state =
    step (data projects) (toggle urlToShow true) state

let private toggleOff urlToHide projects state =
    step (data projects) (toggle urlToHide false) state

let private visibleUrls state = visible state |> Set.toList

// ---- T1: nothing to chart (old: the `!hasHistory` reset block) ------------

[<Fact>]
let ``NoChart resets the host, the override and every colour`` () =
    let overridden = (toggleOff (url 0) (descending 3) (fresh (descending 3))).State
    Assert.NotEqual<VisibilityState>(initial, overridden)
    Assert.Equal<VisibilityDecision>({ State = initial; Refused = false }, step NoChart Settle overridden)

[<Fact>]
let ``a toggle with nothing charted is refused and changes nothing`` () =
    Assert.Equal<VisibilityDecision>(
        { State = initial; Refused = true },
        step NoChart (toggle (url 0) true) initial
    )

[<Fact>]
let ``charted is NoChart without a host or without history`` () =
    Assert.Equal<ChartedData>(NoChart, charted (Nullable()) (descending 3))
    Assert.Equal<ChartedData>(NoChart, charted (Nullable hostA) [])
    Assert.Equal<ChartedData>(Charted(hostA, descending 1), charted (Nullable hostA) (descending 1))

// ---- T2: the charted host changes (old: `_visibleHostId != hostId`) -------

[<Fact>]
let ``a different host starts over: the override is dropped and the default returns`` () =
    let projects = descending 8
    let overridden = (toggleOff (url 0) projects (fresh projects)).State
    Assert.False(Set.contains (url 0) (visible overridden))
    Assert.True(overridden.UserOverrode)

    let onB = (step (Charted(hostB, projects)) Settle overridden).State

    Assert.Equal(Some hostB, onB.HostId)
    Assert.False(onB.UserOverrode)
    Assert.Equal<string list>(visibleUrls (fresh projects), visibleUrls onB)

[<Fact>]
let ``a different host allocates its own slots, never the previous host's`` () =
    // Two hosts reporting the SAME project at different ordinals: carrying the assignment across
    // would hand host B a colour its own ordinals never asked for.
    let onA = fresh [ proj 0 1.0 ]
    let onB = (step (Charted(hostB, [ proj 7 1.0 ])) Settle onA).State
    Assert.Equal(Some 7, slotOf (url 7) onB)
    Assert.Equal(None, slotOf (url 0) onB)

[<Fact>]
let ``the same host keeps its allocation across a settle`` () =
    let projects = descending 8
    let once = fresh projects
    Assert.Equal<VisibilityState>(once, settle projects once)

// ---- T3: no override yet — mirror the LIVE default (old: `!_userOverrode`) -

[<Fact>]
let ``the default visible set is every project when there are at most six`` () =
    let state = fresh (descending 4)
    Assert.Equal<string list>([ url 0; url 1; url 2; url 3 ], visibleUrls state)

[<Fact>]
let ``the default visible set is the top six by RAC past the cap`` () =
    let state = fresh (descending 9)
    Assert.Equal<string list>([ for i in 0..5 -> url i ], visibleUrls state)
    Assert.Equal(StatisticsChart.visibleCap, visibleCount state)

[<Fact>]
let ``a newcomer under the cap becomes visible without a host switch`` () =
    // Codex P2 (PR #167): a project that becomes chartable mid-session must follow the live
    // default, not sit as an unchecked chip until the user switches hosts.
    let state = fresh (descending 2) |> settle (descending 3)
    Assert.Equal<string list>([ url 0; url 1; url 2 ], visibleUrls state)

[<Fact>]
let ``a high-RAC newcomer at the cap displaces the lowest-RAC series`` () =
    let six = descending 6
    let withNewcomer = proj 6 100.0 :: six
    let state = fresh six |> settle withNewcomer
    Assert.True(isVisible (url 6) state)
    Assert.False(isVisible (url 5) state) // the lowest RAC of the old six
    Assert.Equal(StatisticsChart.visibleCap, visibleCount state)

[<Fact>]
let ``a RAC reorder re-ranks the default set`` () =
    let projects = descending 8
    let state = fresh projects
    Assert.False(isVisible (url 7) state)
    // Project 7 out-earns everything: it enters the top six, project 5 falls out.
    let reordered = projects |> List.map (fun p -> if p.Ordinal = 7 then { p with Rac = 99.0 } else p)
    let after = settle reordered state
    Assert.True(isVisible (url 7) after)
    Assert.False(isVisible (url 5) after)

// ---- T4: the user's set is authoritative (old: `_visible.IntersectWith`) --

[<Fact>]
let ``an overridden set survives a settle instead of snapping back to the default`` () =
    let projects = descending 8
    let overridden = (toggleOff (url 2) projects (fresh projects)).State
    let after = settle projects overridden
    Assert.Equal<string list>(visibleUrls overridden, visibleUrls after)
    Assert.False(isVisible (url 2) after)
    Assert.True(after.UserOverrode)

[<Fact>]
let ``a vanished project drops out of the user's set`` () =
    let projects = descending 8
    let overridden = (toggleOff (url 0) projects (fresh projects)).State
    Assert.True(isVisible (url 1) overridden)
    let after = settle (projects |> List.filter (fun p -> p.Ordinal <> 1)) overridden
    Assert.False(isVisible (url 1) after)
    Assert.True(after.UserOverrode) // the rest of their set is still theirs

[<Fact>]
let ``an overridden set does not regrow when a newcomer appears`` () =
    let projects = descending 3
    let overridden = (toggleOff (url 0) projects (fresh projects)).State
    let after = settle (proj 3 0.5 :: projects) overridden
    Assert.Equal<string list>([ url 1; url 2 ], visibleUrls after)

// ---- T5: colours follow the visible set (old: the `SeriesColors.allocate` call) --

[<Fact>]
let ``a series that stays visible keeps its slot when another is hidden`` () =
    // The #171 shape: ordinals 0 and 10 prefer the same home slot, and both are in the default six.
    let projects =
        [ for i in 0..10 -> proj i (if i <= 4 || i = 10 then 1000.0 - float i else 1.0) ]

    let shown = fresh projects
    Assert.Equal(Some 0, slotOf (url 0) shown)
    Assert.Equal(Some 5, slotOf (url 10) shown) // home slot taken → lowest free one

    let after = (toggleOff (url 0) projects shown).State
    Assert.Equal(None, slotOf (url 0) after) // off the chart → no colour at all
    Assert.Equal(Some 5, slotOf (url 10) after) // and nothing that stayed moved
    for i in 1..4 do
        Assert.Equal(slotOf (url i) shown, slotOf (url i) after)

[<Fact>]
let ``on a host of ten or fewer projects a slot is the ordinal itself`` () =
    // Why the shipped ≤10-project chart baselines are untouched: every home slot is uncontended.
    let state = fresh (descending 5)
    for i in 0..4 do
        Assert.Equal(Some i, slotOf (url i) state)

// ---- T6/T7/T9: the toggle and the ≤6 cap (old: TryApplyToggle + CanCheck) --

[<Fact>]
let ``unchecking a visible row hides it and frees the cap`` () =
    let projects = descending 8
    let decision = toggleOff (url 0) projects (fresh projects)
    Assert.False(decision.Refused)
    Assert.False(isVisible (url 0) decision.State)
    Assert.Equal(5, visibleCount decision.State)
    Assert.True(canAdd decision.State)

[<Fact>]
let ``checking a hidden row with room shows it`` () =
    let projects = descending 8
    let withRoom = (toggleOff (url 0) projects (fresh projects)).State
    let decision = toggleOn (url 7) projects withRoom
    Assert.False(decision.Refused)
    Assert.True(isVisible (url 7) decision.State)
    Assert.Equal(StatisticsChart.visibleCap, visibleCount decision.State)

[<Fact>]
let ``the cap refuses a seventh series and leaves the state untouched`` () =
    let projects = descending 8
    let atCap = fresh projects
    Assert.False(canAdd atCap)
    let decision = toggleOn (url 7) projects atCap
    Assert.True(decision.Refused)
    Assert.Equal<VisibilityState>(atCap, decision.State)

[<Fact>]
let ``re-checking a row that was unchecked to make room is refused at the cap`` () =
    // Codex P2 (PR #167): the overflow flyout disables its rows at six, but a re-checked CHIP is
    // the same invariant and must not slip past it — both routes are this one guard.
    let projects = descending 8
    let atCapAgain =
        fresh projects
        |> fun s -> (toggleOff (url 0) projects s).State
        |> fun s -> (toggleOn (url 7) projects s).State

    Assert.Equal(StatisticsChart.visibleCap, visibleCount atCapAgain)
    let decision = toggleOn (url 0) projects atCapAgain
    Assert.True(decision.Refused)
    Assert.Equal<VisibilityState>(atCapAgain, decision.State)

[<Fact>]
let ``unchecking is never refused, even at the cap`` () =
    let projects = descending 8
    let decision = toggleOff (url 0) projects (fresh projects)
    Assert.False(decision.Refused)

[<Fact>]
let ``canCheck is: already shown, or the cap has room`` () =
    let projects = descending 8
    let atCap = fresh projects
    Assert.True(canCheck (url 0) atCap) // shown
    Assert.False(canCheck (url 7) atCap) // hidden and no room
    let withRoom = (toggleOff (url 0) projects atCap).State
    Assert.True(canCheck (url 7) withRoom)

// ---- T8: a toggle makes the user's set authoritative ----------------------

[<Fact>]
let ``a toggle sets the override, so the default never overrules the user again`` () =
    let projects = descending 8
    let before = fresh projects
    Assert.False(before.UserOverrode)
    let after = (toggleOff (url 0) projects before).State
    Assert.True(after.UserOverrode)

[<Fact>]
let ``a refused toggle does not set the override`` () =
    let projects = descending 8
    let decision = toggleOn (url 7) projects (fresh projects)
    Assert.True(decision.Refused)
    Assert.False(decision.State.UserOverrode)

// ---- T12: the shell's toggle → re-settle round trip is idempotent ---------

[<Fact>]
let ``re-settling straight after a toggle changes nothing`` () =
    // The ViewModel applies a toggle and then rebuilds (which settles). If that settle moved the
    // state, a user toggle would be followed by a silent second transition.
    let projects = descending 8
    let toggled = (toggleOff (url 0) projects (fresh projects)).State
    Assert.Equal<VisibilityState>(toggled, settle projects toggled)

// ---- the render gate -----------------------------------------------------

[<Fact>]
let ``colourKey changes exactly when the assignment does`` () =
    let projects = descending 8
    let atCap = fresh projects
    Assert.Equal(colourKey atCap, colourKey (settle projects atCap)) // idle poll: unchanged
    let hidden = (toggleOff (url 0) projects atCap).State
    Assert.NotEqual<string>(colourKey atCap, colourKey hidden)
    Assert.Equal("p0=0,p1=1,p2=2,p3=3,p4=4,p5=5", colourKey atCap)

[<Fact>]
let ``nameKey carries the visible series' names in ordinal order`` () =
    let projects = descending 3
    let state = fresh projects
    Assert.Equal("Project 0\u001fProject 1\u001fProject 2", nameKey projects state)
    // A late-filled name changes the key even though the visible set and the slots do not.
    let renamed = projects |> List.map (fun p -> if p.Ordinal = 1 then { p with Name = "Einstein" } else p)
    Assert.NotEqual<string>(nameKey projects state, nameKey renamed state)
    Assert.Equal(colourKey state, colourKey (settle renamed state))

[<Fact>]
let ``nameKey ignores hidden series`` () =
    let projects = descending 3
    let hidden = (toggleOff (url 1) projects (fresh projects)).State
    Assert.Equal("Project 0\u001fProject 2", nameKey projects hidden)

// ---- properties over arbitrary sessions ----------------------------------

/// Every state a generated user session passes through, on a 15-project host — enough projects that
/// ordinals 0-4 each have a home-slot contender (10-14), so collisions are generated, not hoped for.
/// Each step is a toggle followed by a settle: exactly the round trip the ViewModel performs.
let private sessionStates (steps: (int * bool) list) : VisibilityState list =
    let projects = descending 15

    let events =
        steps
        |> List.collect (fun (i, shouldBeVisible) ->
            [ toggle (url (((i % 15) + 15) % 15)) shouldBeVisible; Settle ])

    events
    |> List.scan (fun state event -> (step (data projects) event state).State) (fresh projects)

[<Property>]
let ``CAP: no reachable state ever holds more than six series`` (steps: (int * bool) list) =
    sessionStates steps |> List.forall (fun s -> visibleCount s <= StatisticsChart.visibleCap)

[<Property>]
let ``INJECTIVITY: no two visible series ever share a palette slot`` (steps: (int * bool) list) =
    sessionStates steps
    |> List.forall (fun s ->
        let slots = s.Colors |> Map.toList |> List.map snd
        List.length slots = List.length (List.distinct slots))

[<Property>]
let ``STABILITY: a series that stays visible keeps its colour`` (steps: (int * bool) list) =
    // The issue's headline invariant, end to end through `step`: toggling one chip can never
    // recolour a line that is still on screen.
    sessionStates steps
    |> List.pairwise
    |> List.forall (fun (before, after) ->
        before.Colors
        |> Map.forall (fun url slot ->
            match Map.tryFind url after.Colors with
            | None -> true // it left the chart, so it holds no colour at all
            | Some current -> current = slot))

[<Property>]
let ``DOMAIN: only reported projects can hold a colour`` (steps: (int * bool) list) =
    let reported = descending 15 |> List.map (fun p -> p.MasterUrl) |> Set.ofList
    sessionStates steps |> List.forall (fun s -> Set.isSubset (visible s) reported)

[<Property>]
let ``REFUSAL: a toggle is refused only when it would exceed the cap`` (steps: (int * bool) list) =
    let projects = descending 15

    steps
    |> List.fold
        (fun (state, ok) (i, shouldBeVisible) ->
            let target = url (((i % 15) + 15) % 15)
            let decision = step (data projects) (toggle target shouldBeVisible) state
            let wouldExceed = shouldBeVisible && not (canCheck target state)
            (decision.State, ok && decision.Refused = wouldExceed))
        (fresh projects, true)
    |> snd
