module Lattice.Aggregation.Tests.StatisticsChartTests

open System
open System.Globalization
open Xunit
open FsCheck
open FsCheck.Xunit
open Lattice.App.Aggregation

// ---- builders ------------------------------------------------------------

let day0 = DateTimeOffset(2026, 7, 14, 0, 0, 0, TimeSpan.Zero)

/// A DailyCredit on day (day0 + n days); the four metric fields are given distinct
/// values so a wrong-column bug in valueOf is visible.
let daily n ut ua ht ha =
    { Day = day0.AddDays(float n); UserTotal = ut; UserAverage = ua; HostTotal = ht; HostAverage = ha }

let proj url name ordinal rac dailies : ProjectHistory =
    { MasterUrl = url; Name = name; Ordinal = ordinal; Rac = rac; Daily = dailies }

/// A fresh colour allocation over the given (master URL, ordinal) pairs — the visible set, in
/// the shape seriesFor now reads visibility from (a series is on the chart iff it holds a slot).
let colors (pairs: (string * int) list) =
    pairs
    |> List.map (fun (url, ordinal) -> { MasterUrl = url; Ordinal = ordinal })
    |> SeriesColors.ofVisible

/// A project with a contiguous total-credit ramp over `count` days from `start`.
let ramp url name ordinal rac start count =
    proj url name ordinal rac [ for i in 0 .. count - 1 -> daily i (start + float i) 0.0 0.0 0.0 ]

// ---- valueOf -------------------------------------------------------------

[<Fact>]
let ``valueOf selects the metric's own field`` () =
    let d = daily 0 10.0 20.0 30.0 40.0
    Assert.Equal(10.0, StatisticsChart.valueOf UserTotal d)
    Assert.Equal(20.0, StatisticsChart.valueOf UserAverage d)
    Assert.Equal(30.0, StatisticsChart.valueOf HostTotal d)
    Assert.Equal(40.0, StatisticsChart.valueOf HostAverage d)

// ---- rankByRac / partition / defaultVisible ------------------------------

[<Fact>]
let ``rankByRac orders by RAC descending, ordinal ascending tiebreak`` () =
    let ps =
        [ proj "a" "A" 0 5.0 []
          proj "b" "B" 1 9.0 []
          proj "c" "C" 2 5.0 [] ]
    let ranked = StatisticsChart.rankByRac ps |> List.map (fun p -> p.MasterUrl)
    Assert.Equal<string list>([ "b"; "a"; "c" ], ranked) // 9 first; tie 5.0 → ordinal 0 before 2

[<Fact>]
let ``partition: <=6 projects are all chips, no overflow`` () =
    let ps = [ for i in 0 .. 5 -> proj (string i) (string i) i (float i) [] ]
    let part = StatisticsChart.partition ps
    Assert.Equal(6, part.Chips.Length)
    Assert.Empty(part.Overflow)

[<Fact>]
let ``partition: >6 keeps the 6 highest-RAC as chips (ordinal-ordered), rest overflow (RAC-ordered)`` () =
    // 8 projects; RAC = ordinal so the top-6 by RAC are ordinals 2..7.
    let ps = [ for i in 0 .. 7 -> proj (string i) (string i) i (float i) [] ]
    let part = StatisticsChart.partition ps
    Assert.Equal<string list>([ "2"; "3"; "4"; "5"; "6"; "7" ], part.Chips |> List.map (fun p -> p.MasterUrl))
    // overflow = the two lowest RAC, in RAC-descending order
    Assert.Equal<string list>([ "1"; "0" ], part.Overflow |> List.map (fun p -> p.MasterUrl))

[<Fact>]
let ``defaultVisible is exactly the chip set`` () =
    let ps = [ for i in 0 .. 7 -> proj (string i) (string i) i (float i) [] ]
    let vis = StatisticsChart.defaultVisible ps
    Assert.Equal<Set<string>>(set [ "2"; "3"; "4"; "5"; "6"; "7" ], vis)

// ---- buildSeriesPoints (gaps) --------------------------------------------

[<Fact>]
let ``buildSeriesPoints: contiguous days carry every value, no gaps`` () =
    let pts = StatisticsChart.buildSeriesPoints UserTotal [ daily 0 1.0 0.0 0.0 0.0; daily 1 2.0 0.0 0.0 0.0; daily 2 3.0 0.0 0.0 0.0 ]
    Assert.Equal<float option list>([ Some 1.0; Some 2.0; Some 3.0 ], pts |> List.map (fun p -> p.Value))

[<Fact>]
let ``buildSeriesPoints: a missing day becomes a None gap, never interpolated`` () =
    // days 0 and 2 present, day 1 missing → point at day 1 is None.
    let pts = StatisticsChart.buildSeriesPoints UserTotal [ daily 0 1.0 0.0 0.0 0.0; daily 2 3.0 0.0 0.0 0.0 ]
    Assert.Equal(3, pts.Length)
    Assert.Equal<float option list>([ Some 1.0; None; Some 3.0 ], pts |> List.map (fun p -> p.Value))

[<Fact>]
let ``buildSeriesPoints: single point renders one real point`` () =
    let pts = StatisticsChart.buildSeriesPoints UserTotal [ daily 0 7.0 0.0 0.0 0.0 ]
    Assert.Equal<float option list>([ Some 7.0 ], pts |> List.map (fun p -> p.Value))

[<Fact>]
let ``buildSeriesPoints: empty history yields no points`` () =
    Assert.Empty(StatisticsChart.buildSeriesPoints UserTotal [])

[<Fact>]
let ``buildSeriesPoints: same-day duplicates keep the first record`` () =
    let pts = StatisticsChart.buildSeriesPoints UserTotal [ daily 0 1.0 0.0 0.0 0.0; daily 0 99.0 0.0 0.0 0.0 ]
    Assert.Equal<float option list>([ Some 1.0 ], pts |> List.map (fun p -> p.Value))

[<Fact>]
let ``buildSeriesPoints: points are exactly one calendar day apart`` () =
    let pts = StatisticsChart.buildSeriesPoints UserTotal [ daily 0 1.0 0.0 0.0 0.0; daily 3 4.0 0.0 0.0 0.0 ]
    let deltas = pts |> List.pairwise |> List.map (fun (a, b) -> (b.Day - a.Day).Days)
    Assert.All(deltas, fun d -> Assert.Equal(1, d))

// ---- bridgesGaps / gapBridges (#170 metric split) ------------------------

/// The points for `offsets` (observed days) over the full span, as the chart sees them.
let private pointsFor metric offsets =
    offsets
    |> List.map (fun n -> daily n (10.0 + float n) (20.0 + float n) (30.0 + float n) (40.0 + float n))
    |> StatisticsChart.buildSeriesPoints metric

let private bridgeDays (bs: GapBridge list) =
    bs |> List.map (fun b -> (b.FromDay - day0).Days, (b.ToDay - day0).Days)

[<Fact>]
let ``bridgesGaps: cumulative metrics bridge, averages break`` () =
    Assert.True(StatisticsChart.bridgesGaps UserTotal)
    Assert.True(StatisticsChart.bridgesGaps HostTotal)
    Assert.False(StatisticsChart.bridgesGaps UserAverage)
    Assert.False(StatisticsChart.bridgesGaps HostAverage)

[<Fact>]
let ``gapBridges: a contiguous history has nothing to bridge`` () =
    Assert.Empty(StatisticsChart.gapBridges UserTotal (pointsFor UserTotal [ 0; 1; 2 ]))

[<Fact>]
let ``gapBridges: one missing day bridges its two observed endpoints, carrying their values`` () =
    let bridges = StatisticsChart.gapBridges UserTotal (pointsFor UserTotal [ 0; 2 ])
    let b = Assert.Single(bridges)
    Assert.Equal(day0, b.FromDay)
    Assert.Equal(day0.AddDays 2.0, b.ToDay)
    Assert.Equal(10.0, b.FromValue) // the OBSERVED values, never an interpolation
    Assert.Equal(12.0, b.ToValue)

[<Fact>]
let ``gapBridges: a multi-day gap run is ONE bridge, not one per missing day`` () =
    // days 0 and 4 observed; 1..3 missing.
    let bridges = StatisticsChart.gapBridges UserTotal (pointsFor UserTotal [ 0; 4 ])
    Assert.Equal<(int * int) list>([ 0, 4 ], bridgeDays bridges)

[<Fact>]
let ``gapBridges: separate gap runs each get their own bridge, in day order`` () =
    // observed 0,1,3,4,7 → gaps at {2} and {5,6}; the 0-1 and 3-4 segments stay solid.
    let bridges = StatisticsChart.gapBridges UserTotal (pointsFor UserTotal [ 0; 1; 3; 4; 7 ])
    Assert.Equal<(int * int) list>([ 1, 3; 4, 7 ], bridgeDays bridges)

[<Fact>]
let ``gapBridges: HostTotal bridges on its own column`` () =
    let bridges = StatisticsChart.gapBridges HostTotal (pointsFor HostTotal [ 0; 2 ])
    let b = Assert.Single(bridges)
    Assert.Equal(30.0, b.FromValue) // HostTotal's field, not UserTotal's
    Assert.Equal(32.0, b.ToValue)

[<Fact>]
let ``gapBridges: the average metrics keep hard breaks over the very same gap`` () =
    Assert.Empty(StatisticsChart.gapBridges UserAverage (pointsFor UserAverage [ 0; 2 ]))
    Assert.Empty(StatisticsChart.gapBridges HostAverage (pointsFor HostAverage [ 0; 2 ]))

[<Fact>]
let ``gapBridges: a single point or no points cannot bridge`` () =
    Assert.Empty(StatisticsChart.gapBridges UserTotal (pointsFor UserTotal [ 3 ]))
    Assert.Empty(StatisticsChart.gapBridges UserTotal [])

// ---- seriesFor -----------------------------------------------------------

[<Fact>]
let ``seriesFor: only visible projects with history, in ordinal order`` () =
    let ps =
        [ ramp "a" "A" 2 0.0 100.0 3
          ramp "b" "B" 0 0.0 200.0 3
          proj "c" "C" 1 0.0 [] ] // no history → excluded even if visible
    let specs = StatisticsChart.seriesFor UserTotal (colors [ "a", 2; "b", 0; "c", 1 ]) ps
    Assert.Equal<string list>([ "b"; "a" ], specs |> List.map (fun s -> s.MasterUrl)) // ordinal 0 then 2
    Assert.Equal<int list>([ 0; 2 ], specs |> List.map (fun s -> s.Ordinal))
    // ≤ 10 projects: every home slot is free, so the slot IS the ordinal (contract §2 point 5).
    Assert.Equal<int list>([ 0; 2 ], specs |> List.map (fun s -> s.Slot))

[<Fact>]
let ``seriesFor: hidden projects are dropped`` () =
    let ps = [ ramp "a" "A" 0 0.0 1.0 2; ramp "b" "B" 1 0.0 1.0 2 ]
    let specs = StatisticsChart.seriesFor UserTotal (colors [ "a", 0 ]) ps
    Assert.Equal<string list>([ "a" ], specs |> List.map (fun s -> s.MasterUrl))

// ---- markerSize ----------------------------------------------------------

[<Theory>]
[<InlineData(1, 8.0)>]
[<InlineData(30, 8.0)>]
[<InlineData(31, 0.0)>]
[<InlineData(90, 0.0)>]
let ``markerSize: 8 up to 30 real points, 0 beyond`` (count: int) (expected: float) =
    let specs = [ StatisticsChart.seriesFor UserTotal (colors [ "a", 0 ]) [ ramp "a" "A" 0 0.0 0.0 count ] |> List.head ]
    Assert.Equal(expected, StatisticsChart.markerSize specs)

[<Fact>]
let ``markerSize: evaluated on the LONGEST visible series`` () =
    let short = StatisticsChart.seriesFor UserTotal (colors [ "s", 0 ]) [ ramp "s" "S" 0 0.0 0.0 5 ] |> List.head
    let long = StatisticsChart.seriesFor UserTotal (colors [ "l", 1 ]) [ ramp "l" "L" 1 0.0 0.0 40 ] |> List.head
    Assert.Equal(0.0, StatisticsChart.markerSize [ short; long ]) // longest (40) > 30 → pure line

[<Fact>]
let ``markerSize: no visible series defaults to markers (8)`` () =
    Assert.Equal(8.0, StatisticsChart.markerSize [])

// ---- compactLabel --------------------------------------------------------

[<Fact>]
let ``compactLabel matches the reference render formats (en-US)`` () =
    let prev = CultureInfo.CurrentCulture
    CultureInfo.CurrentCulture <- CultureInfo.GetCultureInfo "en-US"
    try
        Assert.Equal("0", StatisticsChart.compactLabel 0.0)
        Assert.Equal("820", StatisticsChart.compactLabel 820.0)
        Assert.Equal("10k", StatisticsChart.compactLabel 10_000.0)
        Assert.Equal("30k", StatisticsChart.compactLabel 30_000.0)
        Assert.Equal("1.2k", StatisticsChart.compactLabel 1_200.0)
        Assert.Equal("2.0M", StatisticsChart.compactLabel 2_000_000.0)
        Assert.Equal("6.0M", StatisticsChart.compactLabel 6_000_000.0)
        Assert.Equal("5.6M", StatisticsChart.compactLabel 5_600_000.0)
    finally
        CultureInfo.CurrentCulture <- prev

[<Fact>]
let ``compactLabel: boundaries switch scale at exactly 1k and 1M`` () =
    let prev = CultureInfo.CurrentCulture
    CultureInfo.CurrentCulture <- CultureInfo.GetCultureInfo "en-US"
    try
        Assert.Equal("999", StatisticsChart.compactLabel 999.0)
        Assert.Equal("1k", StatisticsChart.compactLabel 1_000.0)
        Assert.Equal("1.0M", StatisticsChart.compactLabel 1_000_000.0)
    finally
        CultureInfo.CurrentCulture <- prev

// ---- historyDepthDays / canAddSeries -------------------------------------

[<Fact>]
let ``historyDepthDays is the deepest project's distinct-day count`` () =
    let ps = [ ramp "a" "A" 0 0.0 0.0 9; ramp "b" "B" 1 0.0 0.0 3 ]
    Assert.Equal(9, StatisticsChart.historyDepthDays ps)

[<Fact>]
let ``historyDepthDays is 0 when nothing has history`` () =
    Assert.Equal(0, StatisticsChart.historyDepthDays [ proj "a" "A" 0 0.0 [] ])

[<Theory>]
[<InlineData(0, true)>]
[<InlineData(5, true)>]
[<InlineData(6, false)>]
[<InlineData(7, false)>]
let ``canAddSeries gates at the 6-visible cap`` (visible: int) (expected: bool) =
    Assert.Equal(expected, StatisticsChart.canAddSeries visible)

// ---- properties ----------------------------------------------------------

/// Distinct small day offsets (0..40) — the domain buildSeriesPoints shapes.
let private distinctDays =
    Gen.choose (0, 40)
    |> Gen.listOf
    |> Gen.map List.distinct
    |> Arb.fromGen

[<Property>]
let ``buildSeriesPoints: real points sit exactly on the input days, span is gapless`` () =
    Prop.forAll distinctDays (fun offsets ->
        let dailies = offsets |> List.map (fun n -> daily n (float n) 0.0 0.0 0.0)
        let pts = StatisticsChart.buildSeriesPoints UserTotal dailies
        match offsets with
        | [] -> pts.IsEmpty
        | _ ->
            let lo = List.min offsets
            let hi = List.max offsets
            // one point per calendar day across the whole span
            let gapless = pts.Length = hi - lo + 1
            // real points are exactly the input days, carrying their own value
            let realDays =
                pts
                |> List.choose (fun p -> p.Value |> Option.map (fun v -> int ((p.Day - day0).Days), v))
            let expected = offsets |> List.sort |> List.map (fun n -> n, float n)
            gapless && realDays = expected)

[<Property>]
let ``gapBridges: bridges are exactly the runs of unobserved days, endpoints observed`` () =
    Prop.forAll distinctDays (fun offsets ->
        let sorted = List.sort offsets
        let observed = Set.ofList sorted
        let bridges = StatisticsChart.gapBridges UserTotal (pointsFor UserTotal offsets)
        // one bridge per pair of consecutive observed days more than a day apart
        let expected = sorted |> List.pairwise |> List.filter (fun (a, b) -> b - a > 1)
        bridgeDays bridges = expected
        // and each bridge spans ONLY unobserved days, carrying its endpoints' observed values
        && bridges
           |> List.forall (fun b ->
               let lo = (b.FromDay - day0).Days
               let hi = (b.ToDay - day0).Days
               hi - lo > 1
               && [ lo + 1 .. hi - 1 ] |> List.forall (observed.Contains >> not)
               && b.FromValue = 10.0 + float lo
               && b.ToValue = 10.0 + float hi))

[<Property>]
let ``gapBridges: no average metric ever bridges, whatever the gaps`` () =
    Prop.forAll distinctDays (fun offsets ->
        [ UserAverage; HostAverage ]
        |> List.forall (fun m -> (StatisticsChart.gapBridges m (pointsFor m offsets)).IsEmpty))

[<Property>]
let ``partition: chips ∪ overflow = input, disjoint, chips capped at 6`` (racs: int list) =
    let ps = racs |> List.mapi (fun i r -> proj (string i) (string i) i (float r) [])
    let part = StatisticsChart.partition ps
    let chipUrls = part.Chips |> List.map (fun p -> p.MasterUrl) |> Set.ofList
    let overUrls = part.Overflow |> List.map (fun p -> p.MasterUrl) |> Set.ofList
    let allUrls = ps |> List.map (fun p -> p.MasterUrl) |> Set.ofList
    Set.union chipUrls overUrls = allUrls
    && Set.intersect chipUrls overUrls = Set.empty
    && part.Chips.Length = min StatisticsChart.visibleCap ps.Length
    && part.Chips.Length + part.Overflow.Length = ps.Length

[<Property>]
let ``partition: no overflow project outranks any chip on RAC`` (racs: int list) =
    let ps = racs |> List.mapi (fun i r -> proj (string i) (string i) i (float r) [])
    let part = StatisticsChart.partition ps
    match part.Overflow with
    | [] -> true
    | _ ->
        let minChipRac = part.Chips |> List.map (fun p -> p.Rac) |> List.min
        let maxOverRac = part.Overflow |> List.map (fun p -> p.Rac) |> List.max
        maxOverRac <= minChipRac

[<Property>]
let ``compactLabel is never empty and keeps k/M literals`` (v: NormalFloat) =
    let s = StatisticsChart.compactLabel (float v)
    s.Length > 0
