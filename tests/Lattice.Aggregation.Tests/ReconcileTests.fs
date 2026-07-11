module Lattice.Aggregation.Tests.ReconcileTests

open System.Collections.Generic
open Xunit
open FsCheck
open FsCheck.Xunit
open Lattice.App.Aggregation

// The union lives at namespace level (ReconcileOp), NOT inside module
// Reconcile — cases are used unqualified once the namespace is open.
let apply (existing: struct (int * string)[]) (ops: ReconcileOp<int, string> list) =
    let list = ResizeArray(existing |> Seq.map (fun struct (k, r) -> (k, r)))
    for op in ops do
        match op with
        | Update (i, _, row) -> list[i] <- (fst list[i], row)
        | Insert (i, key, row) -> list.Insert(i, (key, row))
        | RemoveAt (i, _) -> list.RemoveAt i
        | Move (fromIndex, toIndex, _) ->
            let item = list[fromIndex]
            list.RemoveAt fromIndex
            list.Insert(toIndex, item)
    list |> Seq.map (fun (k, r) -> struct (k, r)) |> Array.ofSeq

[<Fact>]
let ``identical input yields no ops`` () =
    let rows = [| struct (1, "a"); struct (2, "b") |]
    Assert.Empty(Reconcile.diff rows rows)

[<Fact>]
let ``value change on a surviving key is a single Update`` () =
    let ops = Reconcile.diff [| struct (1, "a"); struct (2, "b") |] [| struct (1, "a2"); struct (2, "b") |]
    Assert.Equal<ReconcileOp<int, string> list>([ Update(0, 1, "a2") ], ops)

[<Fact>]
let ``departed key is removed, new key inserted at position`` () =
    let ops = Reconcile.diff [| struct (1, "a") |] [| struct (2, "b") |]
    Assert.Equal<ReconcileOp<int, string> list>(
        [ RemoveAt(0, 1); Insert(0, 2, "b") ], ops)

[<Fact>]
let ``reorder uses Move, not remove plus insert`` () =
    let ops = Reconcile.diff [| struct (1, "a"); struct (2, "b") |] [| struct (2, "b"); struct (1, "a") |]
    Assert.Equal<ReconcileOp<int, string> list>([ Move(1, 0, 2) ], ops)

[<Fact>]
let ``empty to empty yields no ops`` () =
    Assert.Empty(Reconcile.diff Array.empty<struct (int * string)> Array.empty)

[<Fact>]
let ``empty to one row is a single Insert`` () =
    let ops = Reconcile.diff Array.empty [| struct (1, "a") |]
    Assert.Equal<ReconcileOp<int, string> list>([ Insert(0, 1, "a") ], ops)

[<Fact>]
let ``one row to empty is a single RemoveAt`` () =
    let ops = Reconcile.diff [| struct (1, "a") |] Array.empty
    Assert.Equal<ReconcileOp<int, string> list>([ RemoveAt(0, 1) ], ops)

// Generator: unique keys per array, small alphabet so key overlap is common.
let keyedRows =
    gen {
        let! keys = Gen.subListOf [ 0 .. 9 ]
        let! shuffled = Gen.shuffle keys
        let! rows = Gen.listOfLength shuffled.Length (Gen.elements [ "a"; "b"; "c" ])
        return Array.map2 (fun k r -> struct (k, r)) shuffled (Array.ofList rows)
    }

type ReconcileArbs =
    static member Array() =
        keyedRows |> Arb.fromGen

[<Property(Arbitrary = [| typeof<ReconcileArbs> |])>]
let ``applying the diff reproduces the target exactly`` (before: struct (int * string)[], after: struct (int * string)[]) =
    apply before (Reconcile.diff before after) = after

[<Property(Arbitrary = [| typeof<ReconcileArbs> |])>]
let ``a surviving key is never removed or inserted`` (before: struct (int * string)[], after: struct (int * string)[]) =
    let survivors =
        HashSet(Set.intersect
            (before |> Seq.map (fun struct (k, _) -> k) |> Set.ofSeq)
            (after |> Seq.map (fun struct (k, _) -> k) |> Set.ofSeq))
    Reconcile.diff before after
    |> List.forall (function
        | RemoveAt (_, k) | Insert (_, k, _) -> not (survivors.Contains k)
        | Update _ | Move _ -> true)

[<Property(Arbitrary = [| typeof<ReconcileArbs> |])>]
let ``no-op diff for equal inputs`` (arr: struct (int * string)[]) =
    Reconcile.diff arr arr |> List.isEmpty
