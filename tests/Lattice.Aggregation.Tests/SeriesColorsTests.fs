module Lattice.Aggregation.Tests.SeriesColorsTests

open Xunit
open FsCheck.Xunit
open Lattice.App.Aggregation

// The Statistics palette-slot allocator (design contract §2, issue #171 ruling). The defect this
// replaces was not a wrong branch — it was the data model: every PROJECT held a colour for life,
// keyed `ordinal mod 10`, so past ten projects two of them owned one colour before anything had
// decided what to draw. Here only the series ON THE CHART hold slots, and the visible cap is below
// the palette size, so "two visible series share a colour" has no conditions under which to occur.
//
// The four properties below are that claim, stated executably: injectivity over the visible set
// under ANY toggle sequence, stability while visible, determinism, and degeneration to the old
// slot == ordinal on ≤ 10-project hosts (which is what keeps the shipped baselines valid).

// ---- fixtures ------------------------------------------------------------

let key url ordinal = { MasterUrl = url; Ordinal = ordinal }

/// Project i of a synthetic host: ordinals run 0.. so a host with more than ten projects has
/// real home-slot contenders (0 vs 10, 1 vs 11, ...).
let p i = key (sprintf "p%d" i) i

let slotsOf (colors: Map<string, int>) = colors |> Map.toList |> List.map snd

let isInjective (colors: Map<string, int>) =
    let slots = slotsOf colors
    List.length slots = List.length (List.distinct slots)

// ---- homeSlot ------------------------------------------------------------

[<Fact>]
let ``homeSlot is the ordinal itself inside the palette, and folds past it`` () =
    Assert.Equal<int list>([ 0..9 ], [ 0..9 ] |> List.map SeriesColors.homeSlot)
    Assert.Equal(0, SeriesColors.homeSlot 10)
    Assert.Equal(3, SeriesColors.homeSlot 23)

[<Property>]
let ``homeSlot is total and always lands in the palette`` (ordinal: int) =
    let slot = SeriesColors.homeSlot ordinal
    slot >= 0 && slot < SeriesColors.paletteSize

// ---- the cap constraint --------------------------------------------------

// The whole design rests on this: with the cap at or below the palette size there are always
// enough slots for everything on screen. Raising the cap past ten without adding official colours
// would resurrect #171, so the constraint is pinned here rather than left to a comment.
[<Fact>]
let ``the visible cap never exceeds the palette size`` () =
    Assert.True(
        StatisticsChart.visibleCap <= SeriesColors.paletteSize,
        sprintf
            "visibleCap %d exceeds paletteSize %d — visible series could no longer each hold their own colour (#171)"
            StatisticsChart.visibleCap
            SeriesColors.paletteSize
    )

// ---- allocation basics ---------------------------------------------------

[<Fact>]
let ``only visible series hold a colour`` () =
    let colors = SeriesColors.ofVisible [ p 0; p 3 ]
    Assert.Equal<string list>([ "p0"; "p3" ], colors |> Map.toList |> List.map fst)
    Assert.False(SeriesColors.isVisible "p1" colors)
    Assert.Equal(None, SeriesColors.trySlot "p1" colors)

[<Fact>]
let ``a series becoming visible takes its home slot when it is free`` () =
    let colors = SeriesColors.ofVisible [ p 2; p 5 ]
    Assert.Equal(Some 2, SeriesColors.trySlot "p2" colors)
    Assert.Equal(Some 5, SeriesColors.trySlot "p5" colors)

[<Fact>]
let ``a contended home slot yields to the lowest free slot`` () =
    // Ordinals 0 and 10 share a home slot — the exact #171 case. The lower ordinal is served
    // first, so ordinal 10 takes the lowest slot nobody holds.
    let colors = SeriesColors.ofVisible [ p 0; p 1; p 10 ]
    Assert.Equal(Some 0, SeriesColors.trySlot "p0" colors)
    Assert.Equal(Some 1, SeriesColors.trySlot "p1" colors)
    Assert.Equal(Some 2, SeriesColors.trySlot "p10" colors)
    Assert.True(isInjective colors)

[<Fact>]
let ``hiding a series frees its slot for the next newcomer`` () =
    let shown = SeriesColors.ofVisible [ p 0; p 1; p 2 ]
    let hidden = SeriesColors.allocate [ p 0; p 2 ] shown
    Assert.Equal(None, SeriesColors.trySlot "p1" hidden)
    // Slot 1 is free again, so ordinal 11 (home slot 1) can have it.
    let regrown = SeriesColors.allocate [ p 0; p 2; p 11 ] hidden
    Assert.Equal(Some 1, SeriesColors.trySlot "p11" regrown)

[<Fact>]
let ``re-allocating an unchanged visible set changes nothing`` () =
    let once = SeriesColors.ofVisible [ p 0; p 10; p 4 ]
    Assert.Equal<Map<string, int>>(once, SeriesColors.allocate [ p 0; p 10; p 4 ] once)

[<Fact>]
let ``a full palette falls back to the home slot instead of failing`` () =
    // Unreachable while the cap constraint holds (asserted above); pinned so the function stays
    // total — the degenerate case is the OLD behaviour, never a crash or an invented colour.
    let colors = SeriesColors.ofVisible [ for i in 0..11 -> p i ]
    Assert.Equal(12, colors.Count)
    Assert.Equal(Some 0, SeriesColors.trySlot "p10" colors) // home slot, aliasing p0
    Assert.False(isInjective colors)

// ---- the four ruling properties -----------------------------------------

/// An arbitrary toggle sequence over a 15-project host: each round is the visible set after some
/// user toggle, sanitized to the ≤ 6 cap the ViewModel enforces. 15 projects means ordinals 0-4
/// each have a home-slot contender (10-14), so collisions are generated, not hoped for.
let private toggleRounds (rounds: int list list) : SeriesKey list list =
    rounds
    |> List.map (fun round ->
        round
        |> List.map (fun i -> ((i % 15) + 15) % 15)
        |> List.distinct
        |> List.truncate StatisticsChart.visibleCap
        |> List.map p)

/// Every state the allocator passes through for that sequence, starting from nothing on screen.
let private statesFor (rounds: int list list) : Map<string, int> list =
    toggleRounds rounds
    |> List.scan (fun colors visible -> SeriesColors.allocate visible colors) SeriesColors.empty

[<Property>]
let ``INJECTIVITY: no two visible series ever share a slot`` (rounds: int list list) =
    statesFor rounds |> List.forall isInjective

[<Property>]
let ``STABILITY: a series that stays visible keeps its slot`` (rounds: int list list) =
    statesFor rounds
    |> List.pairwise
    |> List.forall (fun (before, after) ->
        before
        |> Map.forall (fun url slot ->
            match Map.tryFind url after with
            | None -> true // it left the chart; it holds no colour at all
            | Some current -> current = slot))

[<Property>]
let ``DOMAIN: the colour state is exactly the visible set`` (rounds: int list list) =
    List.zip (toggleRounds rounds) (statesFor rounds |> List.tail)
    |> List.forall (fun (visible, colors) ->
        let expected = visible |> List.map (fun k -> k.MasterUrl) |> Set.ofList
        Set.ofSeq (Map.keys colors) = expected)

[<Property>]
let ``DETERMINISM: the result depends on the visible SET, not on its order`` (rounds: int list list) =
    let forward = statesFor rounds |> List.last

    let reversed =
        toggleRounds rounds
        |> List.fold (fun colors visible -> SeriesColors.allocate (List.rev visible) colors) SeriesColors.empty

    forward = reversed

[<Property>]
let ``DEGENERATION: on a ≤10-project host a fresh allocation is slot == ordinal`` (picks: int list) =
    // Every home slot is uncontended there, so the allocator reproduces the old Slot(ordinal)
    // exactly — which is why the shipped ≤10-project chart baselines stay byte-identical.
    let visible =
        picks
        |> List.map (fun i -> ((i % SeriesColors.paletteSize) + SeriesColors.paletteSize) % SeriesColors.paletteSize)
        |> List.distinct
        |> List.truncate StatisticsChart.visibleCap
        |> List.map p

    let colors = SeriesColors.ofVisible visible
    visible |> List.forall (fun k -> SeriesColors.trySlot k.MasterUrl colors = Some k.Ordinal)
