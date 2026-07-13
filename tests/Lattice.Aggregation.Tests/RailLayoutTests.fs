module Lattice.Aggregation.Tests.RailLayoutTests

open System
open Xunit
open Lattice.App.Aggregation

let private id () = Guid.NewGuid()
let private host tier = { Id = id (); Tier = tier }

let private input hosts height =
    { Hosts = hosts
      AvailableHeight = height
      RowHeight = 40.0
      Override = Auto
      HealthyExpanded = false }

[<Fact>]
let ``single host is degenerate: no All-hosts row, no toggle`` () =
    let h = host Healthy
    let layout = RailLayoutPolicy.compute (input [| h |] 1000.0)
    Assert.Equal(SingleHost, layout.Mode)
    Assert.False layout.ShowToggle
    Assert.Equal<RailRow list>([ HostRow h.Id ], layout.Rows)

[<Fact>]
let ``flat when the list fits: All-hosts leads, hosts in registry order`` () =
    let a, b = host Healthy, host Attention
    // (2 hosts + All-hosts) * 40 = 120 <= 200 => fits
    let layout = RailLayoutPolicy.compute (input [| a; b |] 200.0)
    Assert.Equal(Flat, layout.Mode)
    Assert.False layout.ShowToggle           // fits under Auto => no toggle
    Assert.Equal<RailRow list>([ AllHostsRow; HostRow a.Id; HostRow b.Id ], layout.Rows)

[<Fact>]
let ``fits at the exact boundary: height == (hosts + 1) * rowHeight stays flat`` () =
    let a, b = host Healthy, host Attention
    // exact boundary: (2 hosts + All-hosts) * 40 = 120 == 120 => fits (<=)
    let layout = RailLayoutPolicy.compute (input [| a; b |] 120.0)
    Assert.Equal(Flat, layout.Mode)
    Assert.False layout.ShowToggle

[<Fact>]
let ``auto + overflow flips to grouped and shows the toggle`` () =
    let a, b = host Healthy, host Attention
    // (2 + 1) * 40 = 120 > 100 => does not fit
    let layout = RailLayoutPolicy.compute (input [| a; b |] 100.0)
    Assert.Equal(Grouped, layout.Mode)
    Assert.True layout.ShowToggle

[<Fact>]
let ``force-flat keeps flat even when it overflows, and keeps the toggle`` () =
    let a, b = host Healthy, host Attention
    let layout = RailLayoutPolicy.compute { input [| a; b |] 100.0 with Override = ForceFlat }
    Assert.Equal(Flat, layout.Mode)
    Assert.True layout.ShowToggle            // manual override => toggle stays to undo

[<Fact>]
let ``force-grouped groups even when it fits, and keeps the toggle`` () =
    let a, b = host Healthy, host Attention
    let layout = RailLayoutPolicy.compute { input [| a; b |] 1000.0 with Override = ForceGrouped }
    Assert.Equal(Grouped, layout.Mode)
    Assert.True layout.ShowToggle

open FsCheck
open FsCheck.Xunit

let private grouped hosts healthyExp =
    RailLayoutPolicy.compute
        { input hosts 0.0 with   // height 0 => never fits => Grouped under Auto
            Override = Auto
            HealthyExpanded = healthyExp }

[<Fact>]
let ``grouped: attention always expands; healthy collapsed hides its hosts`` () =
    let att = host Attention
    let heal = host Healthy
    let layout = grouped [| att; heal |] false
    Assert.Equal<RailRow list>(
        [ AllHostsRow
          GroupHeaderRow(Attention, 1, true); HostRow att.Id
          GroupHeaderRow(Healthy, 1, false) ],
        layout.Rows)

[<Fact>]
let ``grouped: expanding healthy reveals its hosts in registry order`` () =
    let h1, h2 = host Healthy, host Healthy
    let layout = grouped [| h1; h2 |] true
    Assert.Equal<RailRow list>(
        [ AllHostsRow
          GroupHeaderRow(Healthy, 2, true); HostRow h1.Id; HostRow h2.Id ],
        layout.Rows)

[<Fact>]
let ``grouped: an empty tier is skipped`` () =
    let a, b = host Attention, host Attention
    let layout = grouped [| a; b |] false
    Assert.Equal<RailRow list>(
        [ AllHostsRow; GroupHeaderRow(Attention, 2, true); HostRow a.Id; HostRow b.Id ],
        layout.Rows)

// --- toggleOverride transition table (Auto-return path, decisions §4) ---
// 2 hosts => flat needs (2+1)*40 = 120px.
[<Fact>]
let ``toggle from Auto forces the opposite, then toggling back re-enters Auto (overflow)`` () =
    let overflow = input [| host Healthy; host Attention |] 100.0   // 120 > 100 => Auto groups
    let o1 = RailLayoutPolicy.toggleOverride Auto overflow
    Assert.Equal(ForceFlat, o1)                                     // opposite of Grouped
    let o2 = RailLayoutPolicy.toggleOverride o1 overflow
    Assert.Equal(Auto, o2)                                          // target (Grouped) == Auto's output => Auto

[<Fact>]
let ``toggle from Auto forces the opposite, then toggling back re-enters Auto (fits)`` () =
    let fitsIn = input [| host Healthy; host Attention |] 1000.0    // 120 <= 1000 => Auto flat
    let f1 = RailLayoutPolicy.toggleOverride Auto fitsIn
    Assert.Equal(ForceGrouped, f1)                                  // opposite of Flat
    let f2 = RailLayoutPolicy.toggleOverride f1 fitsIn
    Assert.Equal(Auto, f2)                                          // target (Flat) == Auto's output => Auto

[<Fact>]
let ``toggle keeps forcing (visible flip) when the target differs from Auto's output`` () =
    // ForceFlat while it fits: Auto would show Flat, so toggling to Grouped is a REAL
    // visible change => ForceGrouped (NOT Auto — Auto+fits would leave it Flat, a no-op).
    let fitsIn = input [| host Healthy; host Attention |] 1000.0
    Assert.Equal(ForceGrouped, RailLayoutPolicy.toggleOverride ForceFlat fitsIn)
    // ForceGrouped while it overflows: Auto would group, so toggling to Flat is real => ForceFlat.
    let overflow = input [| host Healthy; host Attention |] 100.0
    Assert.Equal(ForceFlat, RailLayoutPolicy.toggleOverride ForceGrouped overflow)

// --- generators for the property pass ---
let private tierGen = Gen.elements [ Attention; Healthy ]
let private railInputGen =
    gen {
        let! n = Gen.choose (0, 8)
        let! tiers = Gen.listOfLength n tierGen
        let hosts = [| for t in tiers -> { Id = Guid.NewGuid(); Tier = t } |]
        let! height = Gen.choose (0, 600) |> Gen.map float
        let! ov = Gen.elements [ Auto; ForceFlat; ForceGrouped ]
        let! he = Arb.generate<bool>
        return { Hosts = hosts; AvailableHeight = height; RowHeight = 40.0
                 Override = ov; HealthyExpanded = he }
    }
type RailArbs =
    static member Input() = Arb.fromGen railInputGen

let private hostIdsIn rows =
    rows |> List.choose (function HostRow id -> Some id | _ -> None)

[<Property(Arbitrary = [| typeof<RailArbs> |])>]
let ``every emitted HostRow id is a real input host`` (inp: RailLayoutInput) =
    let known = inp.Hosts |> Array.map (fun h -> h.Id) |> Set.ofArray
    (RailLayoutPolicy.compute inp).Rows |> hostIdsIn |> List.forall known.Contains

[<Property(Arbitrary = [| typeof<RailArbs> |])>]
let ``flat and expanded-grouped conserve hosts exactly once`` (inp: RailLayoutInput) =
    let layout = RailLayoutPolicy.compute inp
    // Force Healthy expanded so grouped emits every host, then compare as a set.
    let expanded = RailLayoutPolicy.compute { inp with HealthyExpanded = true }
    match layout.Mode with
    | SingleHost -> true   // degenerate single-host handled by its own unit test
    | Flat ->
        (hostIdsIn layout.Rows |> Set.ofList) = (inp.Hosts |> Array.map (fun h -> h.Id) |> Set.ofArray)
    | Grouped ->
        (hostIdsIn expanded.Rows |> Set.ofList) = (inp.Hosts |> Array.map (fun h -> h.Id) |> Set.ofArray)

[<Property(Arbitrary = [| typeof<RailArbs> |])>]
let ``grouped attention rows always follow their header (always expanded)`` (inp: RailLayoutInput) =
    let layout = RailLayoutPolicy.compute { inp with Override = ForceGrouped }
    let attentionCount = inp.Hosts |> Array.filter (fun h -> h.Tier = Attention) |> Array.length
    if attentionCount = 0 then
        layout.Rows |> List.forall (function GroupHeaderRow(Attention, _, _) -> false | _ -> true)
    else
        layout.Rows |> List.exists (function GroupHeaderRow(Attention, c, ex) -> c = attentionCount && ex | _ -> false)
