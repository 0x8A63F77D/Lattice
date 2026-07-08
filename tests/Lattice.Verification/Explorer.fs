/// Explicit-state BFS explorer. Small by design: reviewability IS the trust
/// argument; the red-first mutants in Properties.fs exercise the checker itself.
module Lattice.Verification.Explorer

open System.Collections.Generic
open Lattice.Verification.Model

[<NoComparison>]
type Reach = {
    states: HashSet<S>
    edges: Dictionary<S, (Action * S) list>
    parent: Dictionary<S, S * Action>
    initial: S
}

let explore (step: S -> Action -> S list) (init: S) : Reach =
    let states = HashSet<S>(HashIdentity.Structural)
    let edges = Dictionary<S, (Action * S) list>(HashIdentity.Structural)
    let parent = Dictionary<S, S * Action>(HashIdentity.Structural)
    let queue = Queue<S>()
    states.Add init |> ignore
    queue.Enqueue init
    while queue.Count > 0 do
        let s = queue.Dequeue()
        let outs =
            [ for a in enabled s do
                for s2 in step s a do
                    if s2 <> s then yield (a, s2) ]   // drop no-op self-loops
        edges[s] <- outs
        for (a, s2) in outs do
            if states.Add s2 then
                parent[s2] <- (s, a)
                queue.Enqueue s2
    { states = states; edges = edges; parent = parent; initial = init }

/// Reconstruct init → s as an action trace for counterexample messages.
let trace (r: Reach) (target: S) : string =
    let rec walk s acc =
        match r.parent.TryGetValue s with
        | true, (p, a) -> walk p ((sprintf "%A" a) :: acc)
        | false, _ -> acc
    let steps = walk target []
    sprintf "trace (%d steps):%s  %s%sfinal: %A"
        steps.Length System.Environment.NewLine
        (String.concat (System.Environment.NewLine + "  ") steps)
        System.Environment.NewLine target

let checkInvariant (r: Reach) (name: string) (ok: S -> bool) : unit =
    match r.states |> Seq.tryFind (ok >> not) with
    | Some bad -> failwithf "INVARIANT %s violated. %s" name (trace r bad)
    | None -> ()
