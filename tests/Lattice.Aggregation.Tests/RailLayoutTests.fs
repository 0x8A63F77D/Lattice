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
