module Lattice.App.Aggregation.ScopeMachineTests

open System
open Xunit
open FsCheck.Xunit
open Lattice.App.Aggregation
open Lattice.App.Aggregation.ScopeMachine

let private h = Guid.NewGuid()
let private other = Guid.NewGuid()

// --- exhaustive transition table: every ScopeEvent × state class (was the I1–I6 prose) ---

[<Fact>]
let ``ExplicitSelect All hosts -> AllHosts, persist null`` () =
    Assert.Equal({ Scope = AllHosts; Persist = PersistExplicit None }, step (Host h) (ExplicitSelect AllHostsCarrier))
    Assert.Equal({ Scope = AllHosts; Persist = PersistExplicit None }, step AllHosts (ExplicitSelect AllHostsCarrier))

[<Fact>]
let ``ExplicitSelect a host -> that host, persist its id`` () =
    Assert.Equal({ Scope = Host h; Persist = PersistExplicit (Some h) }, step AllHosts (ExplicitSelect (HostCarrier h)))
    Assert.Equal({ Scope = Host h; Persist = PersistExplicit (Some h) }, step (Host other) (ExplicitSelect (HostCarrier h)))

[<Fact>]   // R11: the currently-scoped host is removed
let ``HostRemoved of the scoped host -> AllHosts + ClearPersisted`` () =
    Assert.Equal({ Scope = AllHosts; Persist = ClearPersisted }, step (Host h) (HostRemoved h))

[<Fact>]
let ``HostRemoved of a different host -> scope unchanged, no persist`` () =
    Assert.Equal({ Scope = Host h; Persist = NoPersistChange }, step (Host h) (HostRemoved other))
    Assert.Equal({ Scope = AllHosts; Persist = NoPersistChange }, step AllHosts (HostRemoved other))

[<Fact>]
let ``RestoreAtStartup None -> AllHosts, no persist`` () =
    Assert.Equal({ Scope = AllHosts; Persist = NoPersistChange }, step AllHosts (RestoreAtStartup(None, [| h |])))

[<Fact>]
let ``RestoreAtStartup a known id -> that host, no persist`` () =
    Assert.Equal({ Scope = Host h; Persist = NoPersistChange }, step AllHosts (RestoreAtStartup(Some h, [| h; other |])))

[<Fact>]   // R10: the saved id no longer exists
let ``RestoreAtStartup an unknown id -> AllHosts + ClearPersisted`` () =
    Assert.Equal({ Scope = AllHosts; Persist = ClearPersisted }, step AllHosts (RestoreAtStartup(Some h, [| other |])))

[<Fact>]
let ``restoreEvent bridges the C# nullable saved id`` () =
    Assert.Equal(RestoreAtStartup(None, [| h |]), restoreEvent (Nullable()) [| h |])
    Assert.Equal(RestoreAtStartup(Some h, [| h |]), restoreEvent (Nullable h) [| h |])

// --- highlightOf: pure derivation (never stored, never an event) ---

[<Fact>]
let ``highlightOf: SingleHost highlights the sole row regardless of scope`` () =
    Assert.Equal(HighlightHostRow h, highlightOf AllHosts (Nullable h) [||])

[<Fact>]
let ``highlightOf: scoped+visible -> that host; hidden -> none; AllHosts -> sentinel`` () =
    Assert.Equal(HighlightHostRow h, highlightOf (Host h) (Nullable()) [| h; other |])
    Assert.Equal(NoHighlight, highlightOf (Host h) (Nullable()) [| other |])
    Assert.Equal(HighlightAllHostsRow, highlightOf AllHosts (Nullable()) [| h |])

// --- FsCheck properties ---
// FsCheck supplies only primitive arbitraries (Guid list / int / bool); we derive a
// well-formed scenario from them, so there are no hand-written generators to drift.
// Encoded precondition: an ExplicitSelect(HostCarrier) always carries a KNOWN host id
// (you can only click a rendered row); the other events may reference any id.

let private nonneg (i: int) = i &&& Int32.MaxValue
let private pickFrom (arr: Guid[]) (i: int) = arr.[nonneg i % arr.Length]

let private scenario (knownRaw: Guid list) (stateKnown: bool) (evSel: int) (idKnown: bool) (idSel: int) =
    let known = knownRaw |> List.distinct |> List.toArray
    let state = if stateKnown && known.Length > 0 then Host (pickFrom known idSel) else AllHosts
    let anyId = if idKnown && known.Length > 0 then pickFrom known idSel else Guid.NewGuid()
    let ev =
        match (nonneg evSel) % (if known.Length = 0 then 4 else 5) with
        | 0 -> ExplicitSelect AllHostsCarrier
        | 1 -> HostRemoved anyId
        | 2 -> RestoreAtStartup(Some anyId, known)
        | 3 -> RestoreAtStartup(None, known)
        | _ -> ExplicitSelect (HostCarrier (pickFrom known idSel))   // known-only carrier
    known, state, ev

[<Property>]   // P1: result scope is always valid (AllHosts, or a host that still exists)
let ``P1 valid scope`` (knownRaw: Guid list) (sk: bool) (evSel: int) (idKnown: bool) (idSel: int) =
    let known, state, ev = scenario knownRaw sk evSel idKnown idSel
    // Named arms over the ScopeEvent DU (no wildcard, F# canon): only HostRemoved shrinks the
    // surviving-host set; a new event would force a decision here rather than silently reuse `known`.
    let postKnown =
        match ev with
        | HostRemoved id -> known |> Array.filter ((<>) id)
        | ExplicitSelect _ -> known
        | RestoreAtStartup _ -> known
    match (step state ev).Scope with
    | AllHosts -> true
    | Host id -> Array.contains id postKnown

[<Property>]   // P2: persistence changes only on ExplicitSelect or an invalidation path
let ``P2 persistence discipline`` (knownRaw: Guid list) (sk: bool) (evSel: int) (idKnown: bool) (idSel: int) =
    let _, state, ev = scenario knownRaw sk evSel idKnown idSel
    match (step state ev).Persist with
    | NoPersistChange -> true
    | PersistExplicit _ | ClearPersisted ->
        match ev with
        | ExplicitSelect _ -> true
        | HostRemoved id -> state = Host id
        | RestoreAtStartup(Some id, known) -> not (Array.contains id known)
        | RestoreAtStartup(None, _) -> false

[<Property>]   // P3: a Host scope arises ONLY from an explicit choice or a valid restore (never a spontaneous pin)
let ``P3 no spontaneous pin`` (knownRaw: Guid list) (sk: bool) (evSel: int) (idKnown: bool) (idSel: int) =
    let _, state, ev = scenario knownRaw sk evSel idKnown idSel
    match (step state ev).Scope with
    | AllHosts -> true
    | Host resid ->
        match ev with
        | ExplicitSelect (HostCarrier id) -> id = resid
        | ExplicitSelect AllHostsCarrier -> false
        | RestoreAtStartup(Some id, known) -> id = resid && Array.contains id known
        | RestoreAtStartup(None, _) -> false
        | HostRemoved _ -> state = Host resid        // unchanged, not newly pinned
