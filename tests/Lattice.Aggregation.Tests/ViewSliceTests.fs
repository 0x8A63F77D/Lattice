module Lattice.Aggregation.Tests.ViewSliceTests

open System
open Xunit
open FsCheck
open FsCheck.Xunit
open Lattice.App.Aggregation

let host id inScope rowSource unreachable stale ts rows =
    { Id = id
      InScope = inScope
      IsRowSource = rowSource
      IsUnreachableTier = unreachable
      IsStaleSignal = stale
      Timestamp = ts
      Rows = rows }

let idA = Guid.NewGuid()
let idB = Guid.NewGuid()
let t0 = DateTimeOffset(2026, 7, 11, 0, 0, 0, TimeSpan.Zero)

[<Fact>]
let ``rows merge from in-scope row sources only, in host order`` () =
    let slice =
        ViewSlice.compute
            [| host idA true true false false (Nullable t0) [| "a1"; "a2" |]
               host idB false true false false (Nullable t0) [| "b1" |] |]
    Assert.Equal<string[]>([| "a1"; "a2" |], slice.AllRows)
    Assert.Equal<Guid seq>([ idA ], Seq.sort slice.CoveredIds)

[<Fact>]
let ``unreachable tier spans out-of-scope hosts`` () =
    let slice =
        ViewSlice.compute
            [| host idA true true false false (Nullable t0) [||]
               host idB false false true false (Nullable()) [||] |]
    Assert.Contains(idB, slice.UnreachableIds)

[<Fact>]
let ``freshness is the oldest in-scope row-source timestamp`` () =
    let older = t0.AddSeconds -30.0
    let slice =
        ViewSlice.compute
            [| host idA true true false false (Nullable t0) [||]
               host idB true true false false (Nullable older) [||] |]
    Assert.Equal(Nullable older, slice.OldestTimestamp)

[<Fact>]
let ``stale iff any in-scope host signals stale`` () =
    let notStale =
        ViewSlice.compute [| host idA true true false false (Nullable t0) [||] |]
    Assert.False notStale.IsUpdateStale
    let stale =
        ViewSlice.compute
            [| host idA true true false false (Nullable t0) [||]
               host idB true false false true (Nullable()) [||] |]
    Assert.True stale.IsUpdateStale

[<Fact>]
let ``out-of-scope stale signal does not mark the view stale`` () =
    let slice =
        ViewSlice.compute
            [| host idA true true false false (Nullable t0) [||]
               host idB false false false true (Nullable()) [||] |]
    Assert.False slice.IsUpdateStale

let factsGen =
    gen {
        let! n = Gen.choose (0, 6)
        let! hosts =
            Gen.listOfLength n (gen {
                let! inScope = Arb.generate<bool>
                let! rowSource = Arb.generate<bool>
                let! unreachable = Arb.generate<bool>
                let! stale = Arb.generate<bool>
                let! seconds = Gen.choose (0, 1000)
                let! rowCount = Gen.choose (0, 3)
                let ts = if rowSource then Nullable(t0.AddSeconds(float seconds)) else Nullable()
                return host (Guid.NewGuid()) inScope rowSource unreachable stale ts
                           (Array.init rowCount (fun i -> $"r{i}"))
            })
        return Array.ofList hosts
    }

type SliceArbs =
    static member Facts() = Arb.fromGen factsGen

[<Property(Arbitrary = [| typeof<SliceArbs> |])>]
let ``row conservation: AllRows is exactly the in-scope row sources' rows`` (hosts: HostFacts<string>[]) =
    let expected = hosts |> Array.filter (fun h -> h.InScope && h.IsRowSource) |> Array.collect (fun h -> h.Rows)
    (ViewSlice.compute hosts).AllRows = expected

[<Property(Arbitrary = [| typeof<SliceArbs> |])>]
let ``covered equals row-source ids; unreachable ignores scope`` (hosts: HostFacts<string>[]) =
    let slice = ViewSlice.compute hosts
    let covered = hosts |> Seq.filter (fun h -> h.InScope && h.IsRowSource) |> Seq.map (fun h -> h.Id) |> Set.ofSeq
    let unreachable = hosts |> Seq.filter (fun h -> h.IsUnreachableTier) |> Seq.map (fun h -> h.Id) |> Set.ofSeq
    (Set.ofSeq slice.CoveredIds = covered) && (Set.ofSeq slice.UnreachableIds = unreachable)
