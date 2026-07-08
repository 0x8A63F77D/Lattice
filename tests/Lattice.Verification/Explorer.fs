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

/// Weak fairness is scoped to the LOOP process (spec): env actions are
/// options, never obligations. Liveness must therefore hold on executions
/// where the environment goes silent — SCCs and bottomness are computed
/// over loop-action edges only. (Full-graph bottomness would treat an
/// always-available EnvDispose as an escape hatch and discharge every
/// obligation by "the user will eventually dispose".)
let private isLoopAction = function
    | LoopStep | LoopStepFail | DelayFires | WakeConsumed -> true
    | EnvStart | EnvUpdateConfig | EnvWake | EnvDispose -> false

/// Kosaraju SCC (two DFS passes; graph is ~10^4-10^5 states, instant). Iterative —
/// no recursion, stack-safe.
let private sccs (r: Reach) : S list list =
    let succs s = match r.edges.TryGetValue s with
                  | true, es -> es |> List.filter (fst >> isLoopAction) |> List.map snd
                  | _ -> []
    // pass 1: finish order
    let visited = HashSet<S>(HashIdentity.Structural)
    let order = ResizeArray<S>()
    for root in r.states do
        if visited.Add root then
            let st = Stack<S * bool>()
            st.Push(root, false)
            while st.Count > 0 do
                let (v, processed) = st.Pop()
                if processed then order.Add v
                else
                    st.Push(v, true)
                    for w in succs v do
                        if visited.Add w then st.Push(w, false)
    // reverse graph (loop edges only — see isLoopAction)
    let pred = Dictionary<S, ResizeArray<S>>(HashIdentity.Structural)
    for KeyValue(s, es) in r.edges do
        for (a, t) in es do
            if isLoopAction a then
                match pred.TryGetValue t with
                | true, l -> l.Add s
                | false, _ -> let l = ResizeArray<S>() in l.Add s; pred[t] <- l
    // pass 2: reverse DFS in reverse finish order
    let assigned = HashSet<S>(HashIdentity.Structural)
    let result = ResizeArray<S list>()
    for i in (order.Count - 1) .. -1 .. 0 do
        let root = order[i]
        if assigned.Add root then
            let comp = ResizeArray<S>()
            let st = Stack<S>()
            st.Push root
            comp.Add root
            while st.Count > 0 do
                let v = st.Pop()
                let ps = match pred.TryGetValue v with | true, l -> List.ofSeq l | false, _ -> []
                for w in ps do
                    if assigned.Add w then
                        comp.Add w
                        st.Push w
            result.Add(List.ofSeq comp)
    List.ofSeq result

let bottomSccs (r: Reach) : S list list =
    // bottomness = no LOOP edge leaves the component (env edges are not escapes)
    let succs s = match r.edges.TryGetValue s with
                  | true, es -> es |> List.filter (fst >> isLoopAction) |> List.map snd
                  | _ -> []
    let all = sccs r
    let sccOf = Dictionary<S, int>(HashIdentity.Structural)
    all |> List.iteri (fun i comp -> for s in comp do sccOf[s] <- i)
    all |> List.mapi (fun i comp -> (i, comp))
        |> List.filter (fun (i, comp) ->
            comp |> List.forall (fun s -> succs s |> List.forall (fun t -> sccOf[t] = i)))
        |> List.map snd

/// Liveness rule: finite graph + weak fairness ON THE LOOP means every
/// execution on which the environment is eventually silent settles into a
/// bottom loop-SCC and takes every internal loop edge infinitely often;
/// pending-and-never-goal inside such an SCC is exactly a liveness violation.
/// Env edges may not serve as escapes because nothing obliges them to fire.
let checkEventually (r: Reach) (name: string) (pending: S -> bool) (goal: S -> bool) : unit =
    for comp in bottomSccs r do
        match comp |> List.tryFind (fun s -> pending s && not (goal s)) with
        | Some bad ->
            failwithf "LIVENESS %s: bottom SCC where the obligation never discharges. %s"
                name (trace r bad)
        | None -> ()
