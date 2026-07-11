namespace Lattice.App.Aggregation

open System.Collections.Generic

/// One imperative edit for the applier to perform on the bound collection.
/// Indices refer to the collection state at the moment the op applies.
type ReconcileOp<'Key, 'Row> =
    /// Holder at Index keeps its identity; only its Data changes.
    | Update of Index: int * Key: 'Key * Row: 'Row
    | Insert of Index: int * Key: 'Key * Row: 'Row
    | RemoveAt of Index: int * Key: 'Key
    | Move of FromIndex: int * ToIndex: int * Key: 'Key

module Reconcile =
    /// First element satisfying `pred`, its index, and the list without it.
    let private tryExtract pred xs =
        xs
        |> List.tryFindIndex pred
        |> Option.map (fun idx -> idx, List.item idx xs, List.removeAt idx xs)

    /// Pure keyed diff. Precondition: keys unique within each array.
    /// Applying the returned ops in order to `existing` yields `target` exactly.
    /// Surviving keys are never removed+reinserted — identity preservation
    /// is the point (issue #24). Removals are emitted back-to-front so every
    /// emitted index is live when its op applies.
    let diff (existing: struct ('Key * 'Row)[]) (target: struct ('Key * 'Row)[]) : ReconcileOp<'Key, 'Row> list =
        // Construction-only lookup state; never escapes this function.
        let targetKeys = HashSet(target |> Seq.map (fun struct (k, _) -> k))

        // Departed keys leave back-to-front, so each RemoveAt index is the
        // element's original position and is still live when the op applies.
        let removals =
            existing
            |> Array.indexed
            |> Array.rev
            |> Array.choose (fun (i, struct (k, _)) ->
                if targetKeys.Contains k then None else Some(RemoveAt(i, k)))
            |> Array.toList

        let survivors =
            existing
            |> Array.toList
            |> List.choose (fun struct (k, r) ->
                if targetKeys.Contains k then Some(k, r) else None)

        // Settle target slot i. `rest` is the post-removal working list from
        // index i onward — slots before i already match target, so an op for
        // slot i only ever touches `rest`. Emits the ops for this slot and
        // the remainder to thread into slot i + 1.
        let settleSlot rest (i, struct (key, row)) =
            match rest with
            | (k, r) :: settled when k = key ->
                (if r = row then [] else [ Update(i, key, row) ]), settled
            | blocker :: laterRows ->
                match tryExtract (fun (k, _) -> k = key) laterRows with
                | Some(offset, (_, movedRow), remaining) ->
                    let update = if movedRow = row then [] else [ Update(i, key, row) ]
                    Move(i + 1 + offset, i, key) :: update, blocker :: remaining
                | None -> [ Insert(i, key, row) ], rest
            | [] -> [ Insert(i, key, row) ], []

        let slotOps, _ =
            target
            |> Array.indexed
            |> Array.mapFold settleSlot survivors

        removals @ List.concat slotOps
