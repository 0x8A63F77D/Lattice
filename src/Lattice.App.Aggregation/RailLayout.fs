namespace Lattice.App.Aggregation

open System

/// Status-group tier for the many-hosts rail. Two tiers only (owner decision,
/// decisions spec §2); M3 reopens this when a terminal paused/disabled state exists.
type RailTier =
    | Attention
    | Healthy

/// User's persisted list/group override; Auto lets the height fit test decide.
type RailOverride =
    | Auto
    | ForceFlat
    | ForceGrouped

/// Effective mode after fit test + override resolve.
type RailMode =
    | SingleHost   // exactly one host: no "All hosts", scope pinned to it
    | Flat         // "All hosts" + individual host rows
    | Grouped      // status groups

/// One host projected by the shell from HostStore (registry order preserved).
type RailHost = { Id: Guid; Tier: RailTier }

/// An ordered rail row the shell reconciles into a view-model.
type RailRow =
    | AllHostsRow
    | HostRow of Guid
    | GroupHeaderRow of tier: RailTier * count: int * expanded: bool

/// What the shell measures / persists and hands to the pure core.
type RailLayoutInput =
    { Hosts: RailHost[]
      AvailableHeight: float
      RowHeight: float
      Override: RailOverride
      HealthyExpanded: bool }

/// The layout the shell renders.
type RailLayout =
    { Mode: RailMode
      ShowToggle: bool
      Rows: RailRow list }

module RailLayoutPolicy =

    /// Flat list fits iff (host count + the All-hosts row) rows clear the budget.
    let private fits (input: RailLayoutInput) =
        float (input.Hosts.Length + 1) * input.RowHeight <= input.AvailableHeight

    /// Registry-order host rows for the flat list.
    let private flatRows (hosts: RailHost[]) =
        AllHostsRow :: [ for h in hosts -> HostRow h.Id ]

    /// Fixed render order of the status groups.
    let private tierOrder = [ Attention; Healthy ]

    /// Attention is always expanded; Healthy honors the persisted flag.
    let private tierExpanded (input: RailLayoutInput) tier =
        match tier with
        | Attention -> true
        | Healthy -> input.HealthyExpanded

    /// Rows for one non-empty tier: header, then its hosts iff expanded.
    let private groupRows (input: RailLayoutInput) tier (members: RailHost[]) =
        let expanded = tierExpanded input tier
        let header = GroupHeaderRow(tier, members.Length, expanded)
        if expanded then header :: [ for h in members -> HostRow h.Id ] else [ header ]

    let private groupedRows (input: RailLayoutInput) =
        AllHostsRow
        :: [ for tier in tierOrder do
                let members = input.Hosts |> Array.filter (fun h -> h.Tier = tier)
                if members.Length > 0 then yield! groupRows input tier members ]

    let compute (input: RailLayoutInput) : RailLayout =
        match input.Hosts.Length with
        | 0 ->
            { Mode = Flat; ShowToggle = false; Rows = [ AllHostsRow ] }
        | 1 ->
            { Mode = SingleHost; ShowToggle = false; Rows = [ HostRow input.Hosts.[0].Id ] }
        | _ ->
            let doesFit = fits input
            let mode =
                match input.Override with
                | ForceFlat -> Flat
                | ForceGrouped -> Grouped
                | Auto -> if doesFit then Flat else Grouped
            let showToggle = not doesFit || input.Override <> Auto
            let rows =
                match mode with
                | Flat | SingleHost -> flatRows input.Hosts
                | Grouped -> groupedRows input
            { Mode = mode; ShowToggle = showToggle; Rows = rows }

    /// Next override when the user clicks the list/group toggle. Targets the opposite of
    /// the CURRENT effective layout; if that target is exactly what Auto would produce
    /// right now, returns Auto (re-enter adaptive) so ShowToggle can hide once it fits.
    /// Total, no wildcard.
    let toggleOverride (current: RailOverride) (input: RailLayoutInput) : RailOverride =
        let autoGroups = not (fits input)
        let currentlyGrouped =
            match current with
            | ForceGrouped -> true
            | ForceFlat -> false
            | Auto -> autoGroups
        let wantGrouped = not currentlyGrouped
        if wantGrouped = autoGroups then Auto
        elif wantGrouped then ForceGrouped
        else ForceFlat
