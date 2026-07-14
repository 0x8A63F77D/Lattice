namespace Lattice.App.Aggregation

open System

/// The global host scope: which host every data view filters to. Mirrors the C#
/// `ScopeSelection` (AllHosts | one host) but lives in the pure core so the shell
/// owns none of the transition logic.
type Scope =
    | AllHosts
    | Host of Guid

/// A rail row the user can click to CHOOSE a scope. Group headers and the transient
/// `null` selection are deliberately NOT carriers (see `step`'s doc comment).
type ScopeCarrier =
    | AllHostsCarrier
    | HostCarrier of Guid

/// The only occurrences that move scope. Closed by construction — this DU IS the
/// former "rail scope invariants" prose, now compiler-checked.
/// NOT events (the shell must never fabricate one for them):
///   * rail REBUILD  — re-selecting a row during a rebuild is not a user choice (R5);
///                     the new highlight is derived by `highlightOf`, not `step`.
///   * host ADDED    — never pins a lone host; single-host is presentation only,
///                     owned by `RailLayout` (Scope stays AllHosts — data-identical).
///   * MODE change   — flat↔grouped is a `RailLayout` concern; it moves no scope.
type ScopeEvent =
    | ExplicitSelect of ScopeCarrier
    | HostRemoved of Guid
    | RestoreAtStartup of savedHostId: Guid option * knownHostIds: Guid[]

/// The persistence side effect the shell applies after a transition.
/// `PersistExplicit`/`ClearPersisted` both write `ScopeHostId` (an id / null);
/// `NoPersistChange` leaves it untouched. Kept distinct so the transition table
/// reads intent (a user choice vs. discarding a stale id), not just the byte written.
type PersistAction =
    | PersistExplicit of Guid option
    | ClearPersisted
    | NoPersistChange

/// `step`'s output: the next scope plus the persistence action.
type ScopeDecision =
    { Scope: Scope
      Persist: PersistAction }

/// Which rail row the shell should highlight. Derived from scope + rail contents by
/// `highlightOf`; never stored, never produced by an event.
type RailHighlight =
    | HighlightAllHostsRow
    | HighlightHostRow of Guid
    | NoHighlight

module ScopeMachine =

    /// The whole scope/selection/persistence state machine: state in, decision out.
    /// Total over the event DU; no wildcard on domain cases (CLAUDE.md F# canon).
    let step (state: Scope) (event: ScopeEvent) : ScopeDecision =
        match event with
        | ExplicitSelect AllHostsCarrier ->
            { Scope = AllHosts; Persist = PersistExplicit None }
        | ExplicitSelect (HostCarrier id) ->
            { Scope = Host id; Persist = PersistExplicit (Some id) }
        | HostRemoved id ->
            match state with
            | Host scoped when scoped = id ->
                // R11: the scoped host is gone — fall back to All hosts, wipe the stale id.
                { Scope = AllHosts; Persist = ClearPersisted }
            | AllHosts
            | Host _ ->
                { Scope = state; Persist = NoPersistChange }
        | RestoreAtStartup (savedHostId, knownHostIds) ->
            match savedHostId with
            | Some id when Array.contains id knownHostIds ->
                { Scope = Host id; Persist = NoPersistChange }
            | Some _ ->
                // R10: the saved host no longer exists — fall back, wipe the stale id.
                { Scope = AllHosts; Persist = ClearPersisted }
            | None ->
                { Scope = AllHosts; Persist = NoPersistChange }

    /// Which row RebuildRail should select — a pure function of scope + what the rail
    /// currently shows. CONSUMER-SHAPED (Nullable / array) for the C# shell (F# canon
    /// pt.: C#-consumed signatures may stay consumer-shaped):
    ///   * soleHost      = the lone host id in SingleHost presentation, else null. The
    ///                     sole row is highlighted regardless of scope (scope stays
    ///                     AllHosts — data-identical for one host).
    ///   * visibleHostIds = host ids that actually have a rendered row (a scoped host
    ///                     hidden inside a collapsed group has none ⇒ NoHighlight).
    let highlightOf (scope: Scope) (soleHost: Nullable<Guid>) (visibleHostIds: Guid[]) : RailHighlight =
        if soleHost.HasValue then HighlightHostRow soleHost.Value
        else
            match scope with
            | Host id when Array.contains id visibleHostIds -> HighlightHostRow id
            | Host _ -> NoHighlight
            | AllHosts -> HighlightAllHostsRow

    /// C#-friendly constructor for the startup restore event: converts the nullable
    /// persisted id at the boundary so the shell never touches `FSharpOption` (F# canon:
    /// convert exception/nullable-style .NET APIs to Option at the boundary).
    let restoreEvent (savedHostId: Nullable<Guid>) (knownHostIds: Guid[]) : ScopeEvent =
        RestoreAtStartup((if savedHostId.HasValue then Some savedHostId.Value else None), knownHostIds)
