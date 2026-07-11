namespace Lattice.App.Aggregation

open System
open System.Collections.Generic

/// One daemon log line's identity. Seqno alone is NOT unique across daemon
/// restarts (it resets); the timestamp disambiguates reused seqnos while
/// staying stable under reconnect replay (the same line re-fetched carries
/// the same triple). Null message timestamps map to 0 ticks at the boundary.
[<Struct>]
type MessageKey =
    { HostId: Guid
      Seqno: int
      TimestampTicks: int64 }

type LogEntry<'Msg> =
    { Key: MessageKey
      Message: 'Msg }

/// Per-host retained log with set-ingest semantics. Treat as opaque outside
/// this module; consumers go through ingest/merged/prune.
type MessageLog<'Msg> =
    { Capacity: int
      ByHost: Map<Guid, LogEntry<'Msg>[]> }

module MessageLog =
    let empty (capacity: int) : MessageLog<'Msg> =
        { Capacity = capacity; ByHost = Map.empty }

    /// Set-semantics ingest of one host's batch: entries whose key is already
    /// retained are dropped (that is the reconnect-replay dedup), the rest
    /// merge in (timestamp, seqno) order, oldest evicted beyond capacity.
    /// Returns the new log and the entries that were actually new — the
    /// unread-badge delta. Idempotent: ingesting a batch twice equals once.
    /// Precondition: every key's HostId = hostId (the event is per-host).
    let ingest (hostId: Guid) (batch: LogEntry<'Msg>[]) (log: MessageLog<'Msg>) : MessageLog<'Msg> * LogEntry<'Msg>[] =
        let existing = log.ByHost |> Map.tryFind hostId |> Option.defaultValue [||]
        let known = HashSet(existing |> Seq.map (fun e -> e.Key))
        let fresh =
            batch
            |> Array.distinctBy (fun e -> e.Key)
            |> Array.filter (fun e -> not (known.Contains e.Key))
        if fresh.Length = 0 then
            log, [||]
        else
            let merged =
                Array.append existing fresh
                |> Array.sortBy (fun e -> e.Key.TimestampTicks, e.Key.Seqno)
            let retained =
                if merged.Length > log.Capacity then merged[merged.Length - log.Capacity ..]
                else merged
            { log with ByHost = log.ByHost |> Map.add hostId retained }, fresh

    /// Every retained line across hosts as one time-merged stream, oldest
    /// first; (host, seqno) break timestamp ties deterministically.
    let merged (log: MessageLog<'Msg>) : LogEntry<'Msg>[] =
        log.ByHost
        |> Map.toArray
        |> Array.collect snd
        |> Array.sortBy (fun e -> e.Key.TimestampTicks, e.Key.HostId, e.Key.Seqno)

    /// Drops hosts no longer in the registry.
    let prune (liveHosts: HashSet<Guid>) (log: MessageLog<'Msg>) : MessageLog<'Msg> =
        { log with ByHost = log.ByHost |> Map.filter (fun id _ -> liveHosts.Contains id) }
