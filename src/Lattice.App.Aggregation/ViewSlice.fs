namespace Lattice.App.Aggregation

open System
open System.Collections.Generic

/// Per-host facts the App layer projects from HostStore + RailStateProjection.
/// Flags, not RailState: this project stays free of UI-layer types.
type HostFacts<'Row> =
    { Id: Guid
      /// Host is inside the current view scope (single host or all).
      InScope: bool
      /// Connected AND snapshotted — this host's rows are in the grid.
      IsRowSource: bool
      /// Unreachable or AuthFailed — the partial-bar "missing" tier.
      /// Evaluated over ALL hosts, scope-independent (episode semantics).
      IsUnreachableTier: bool
      /// Retrying or Unreachable — drives the stale-update indicator.
      IsStaleSignal: bool
      /// Snapshot timestamp when a snapshot exists.
      Timestamp: DateTimeOffset Nullable
      Rows: 'Row[] }

/// Everything a data-view VM derives from the host set before its
/// view-specific filter/sort.
type Slice<'Row> =
    { /// In-scope row-source hosts' rows, concatenated in host order.
      AllRows: 'Row[]
      /// Exactly the hosts AllRows came from (feeds the partial-bar fingerprint).
      CoveredIds: HashSet<Guid>
      /// Unreachable-tier hosts across ALL hosts, scope-independent.
      UnreachableIds: HashSet<Guid>
      /// Oldest in-scope row-source snapshot: the pessimistic freshness reading.
      OldestTimestamp: DateTimeOffset Nullable
      IsUpdateStale: bool }

module ViewSlice =
    /// Total and pure over the facts array.
    let compute (hosts: HostFacts<'Row>[]) : Slice<'Row> =
        let rowSources = hosts |> Array.filter (fun h -> h.InScope && h.IsRowSource)
        let timestamps = rowSources |> Array.choose (fun h -> Option.ofNullable h.Timestamp)
        { AllRows = rowSources |> Array.collect (fun h -> h.Rows)
          CoveredIds = HashSet(rowSources |> Seq.map (fun h -> h.Id))
          UnreachableIds =
            HashSet(hosts |> Seq.filter (fun h -> h.IsUnreachableTier) |> Seq.map (fun h -> h.Id))
          OldestTimestamp =
            if timestamps.Length = 0 then Nullable() else Nullable(Array.min timestamps)
          IsUpdateStale = hosts |> Array.exists (fun h -> h.InScope && h.IsStaleSignal) }
