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
                | Grouped -> flatRows input.Hosts   // TODO(Task 3): replace with grouped rows
            { Mode = mode; ShowToggle = showToggle; Rows = rows }
