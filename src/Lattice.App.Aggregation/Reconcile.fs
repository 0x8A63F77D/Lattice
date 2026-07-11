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
    /// Pure keyed diff. Precondition: keys unique within each array.
    /// Surviving keys are never removed+reinserted — identity preservation
    /// is the point (issue #24). Removals are emitted back-to-front so every
    /// emitted index is live when its op applies.
    let diff (existing: struct ('Key * 'Row)[]) (target: struct ('Key * 'Row)[]) : ReconcileOp<'Key, 'Row> list =
        let targetKeys = HashSet(target |> Seq.map (fun struct (k, _) -> k))
        let working = ResizeArray(existing |> Seq.map (fun struct (k, r) -> (k, r)))
        let ops = ResizeArray()

        for i in working.Count - 1 .. -1 .. 0 do
            let key = fst working[i]
            if not (targetKeys.Contains key) then
                ops.Add(RemoveAt(i, key))
                working.RemoveAt i

        target
        |> Array.iteri (fun i struct (key, row) ->
            if i < working.Count && fst working[i] = key then
                if snd working[i] <> row then
                    ops.Add(Update(i, key, row))
                    working[i] <- (key, row)
            else
                let mutable j = -1
                for candidate in i + 1 .. working.Count - 1 do
                    if j < 0 && fst working[candidate] = key then j <- candidate
                if j >= 0 then
                    ops.Add(Move(j, i, key))
                    let item = working[j]
                    working.RemoveAt j
                    working.Insert(i, item)
                    if snd item <> row then
                        ops.Add(Update(i, key, row))
                        working[i] <- (key, row)
                else
                    ops.Add(Insert(i, key, row))
                    working.Insert(i, (key, row)))

        List.ofSeq ops
