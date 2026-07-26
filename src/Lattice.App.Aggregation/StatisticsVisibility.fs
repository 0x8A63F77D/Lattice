namespace Lattice.App.Aggregation

open System

/// What the Statistics page has to chart right now: nothing, or one host's non-empty history.
/// The two cases are the page's whole data taxonomy — no host, an unreachable host with no cached
/// snapshot, and a connected host that reported no daily records all collapse into `NoChart`,
/// because the visibility state they imply is identical (nothing is on the chart). Built at the
/// C# boundary by `StatisticsVisibility.charted`, which is what keeps `Charted` non-empty.
type ChartedData =
    | NoChart
    | Charted of hostId: Guid * projects: ProjectHistory list

/// The only occurrences that move the visibility/colour state. Closed by construction:
///   * `Settle`          — the charted data as it now stands (a store poll, a host switch, a
///                         scope change, a metric/theme change: all re-derive from the same rule).
///   * `ToggleRequested` — the user checked or unchecked one legend chip / overflow row.
/// NOT events: the 1 s freshness tick (it advances a caption only) and a legend REBUILD (the rows
/// are derived from the state, never a source of it).
type VisibilityEvent =
    | Settle
    | ToggleRequested of masterUrl: string * shouldBeVisible: bool

/// The page's visibility/colour state — the whole of it.
///
/// There is deliberately no separate "visible set" field: the colour map's DOMAIN is the visible
/// set (design contract §2, issue #171 ruling). A series is on the chart exactly when it holds a
/// palette slot, so the membership question and the colour question cannot answer differently —
/// the two-sources-of-truth shape #171 removed from the chart layer is absent here by construction.
type VisibilityState =
    { /// The host these colours belong to; `None` when nothing is charted. Slots are per-chart, so
      /// a different host starts from a fresh allocation (two hosts can share a project URL at
      /// different ordinals).
      HostId: Guid option
      /// Whether the user has toggled visibility for THIS host. Until they do, the page mirrors the
      /// live default (all projects when ≤ 6, else the current top 6 by RAC); once they do, their
      /// set is authoritative and only vanished projects are dropped from it.
      UserOverrode: bool
      /// The palette slot each VISIBLE series holds, threaded across settles — that is what makes a
      /// colour stable while its line stays on screen (§2 rules 3-4).
      Colors: Map<string, int> }

/// `step`'s output: the next state, plus whether a requested toggle was REFUSED by the ≤ 6 cap
/// (the shell snaps the control back and leaves the page as it was).
type VisibilityDecision =
    { State: VisibilityState
      Refused: bool }

/// The Statistics page's visibility/colour state machine: state in, state out.
///
/// Every sibling surface keeps its decisions in a module like this one (ScopeMachine,
/// TasksOverlayPolicy, PartialBarPolicy, ColumnVisibilityPolicy); this page threaded them through
/// inline ViewModel fields instead and paid for it with four repair rounds in the two days after
/// #167 landed (#170, #171, #175). The transition table lives here so it can be read, and tested,
/// as a table (issue #191).
///
/// The ViewModel is the shell: it holds one `VisibilityState`, projects the store into a
/// `ChartedData`, calls `step`, and renders the result. It decides nothing.
module StatisticsVisibility =

    /// Nothing charted, nothing overridden, no colours held — the state a page starts in and the
    /// one it returns to whenever there is nothing to chart.
    let initial: VisibilityState =
        { HostId = None
          UserOverrode = false
          Colors = SeriesColors.empty }

    /// The series on the chart. Reads the colour map's domain — see `VisibilityState.Colors`.
    let visible (state: VisibilityState) : Set<string> = state.Colors |> Map.keys |> Set.ofSeq

    /// Whether one project's series is on the chart (equivalently: holds a colour).
    let isVisible (masterUrl: string) (state: VisibilityState) : bool =
        SeriesColors.isVisible masterUrl state.Colors

    /// The palette slot a project's series holds, or `None` when it is not on the chart. The chip's
    /// swatch reads this: no slot means no colour at all, never a dimmed one (§2).
    let slotOf (masterUrl: string) (state: VisibilityState) : int option =
        SeriesColors.trySlot masterUrl state.Colors

    /// How many series are on the chart — the ≤ 6 cap's counter.
    let visibleCount (state: VisibilityState) : int = Map.count state.Colors

    /// Whether another series may be toggled on (§4 cap).
    let canAdd (state: VisibilityState) : bool = StatisticsChart.canAddSeries (visibleCount state)

    /// Whether a row's checkbox may be checked: already shown, or the cap has room (§4).
    let canCheck (masterUrl: string) (state: VisibilityState) : bool =
        isVisible masterUrl state || canAdd state

    /// Pin the state to the charted host. A DIFFERENT host starts over: its own default set (so the
    /// override is dropped) and its own allocation (so it is never handed colours another host's
    /// ordinals asked for).
    let private onHost (hostId: Guid) (state: VisibilityState) : VisibilityState =
        if state.HostId = Some hostId then
            state
        else
            { HostId = Some hostId
              UserOverrode = false
              Colors = SeriesColors.empty }

    /// Re-derive the colours for a wanted visible set (§2, #171): series that stay on screen keep
    /// their slot, newcomers take their home slot or the lowest free one, departures free theirs.
    /// Filtering by `projects` is also what DROPS a vanished project from the user's set — a URL
    /// nothing reports any more cannot be allocated, so it cannot stay visible.
    let private reallocate
        (projects: ProjectHistory list)
        (wanted: Set<string>)
        (state: VisibilityState)
        : VisibilityState =
        let keys =
            projects
            |> List.filter (fun p -> Set.contains p.MasterUrl wanted)
            |> List.map (fun p -> { MasterUrl = p.MasterUrl; Ordinal = p.Ordinal })

        { state with Colors = SeriesColors.allocate keys state.Colors }

    /// The settle rule, in one place: mirror the LIVE default until the user overrides, then honour
    /// their set. Mirroring the live default is what makes a mid-session attach appear, and a RAC
    /// reorder re-rank, without waiting for a host switch (Codex P2 family, PR #167).
    let private settleOn (hostId: Guid) (projects: ProjectHistory list) (state: VisibilityState) =
        let carried = onHost hostId state

        let wanted =
            if carried.UserOverrode then
                visible carried
            else
                StatisticsChart.defaultVisible projects

        reallocate projects wanted carried

    /// The whole transition. Total over both DUs; no wildcard on domain cases (F# canon).
    ///
    /// A `ToggleRequested` settles FIRST, so the toggle always applies to the set the user is
    /// looking at. In the only order the event can occur in — a toggle follows the settle that
    /// rendered the row it came from — that settle is a no-op, which is what makes the shell's
    /// "apply the toggle, then re-settle" round trip idempotent (both pinned in the tests).
    let step (data: ChartedData) (event: VisibilityEvent) (state: VisibilityState) : VisibilityDecision =
        match data, event with
        | NoChart, Settle ->
            // Nothing to chart: no host, no override, no colours held.
            { State = initial; Refused = false }
        | NoChart, ToggleRequested _ ->
            // A row cannot exist without data; refuse rather than invent a visible series.
            { State = state; Refused = true }
        | Charted(hostId, projects), Settle ->
            { State = settleOn hostId projects state; Refused = false }
        | Charted(hostId, projects), ToggleRequested(masterUrl, shouldBeVisible) ->
            let settled = settleOn hostId projects state

            if shouldBeVisible && not (canCheck masterUrl settled) then
                // The ≤ 6 cap refuses the seventh series. Enforced on the ONE path both the chips
                // and the overflow rows take, so it cannot hold on one and be forgotten on the
                // other (Codex P2, PR #167).
                { State = settled; Refused = true }
            else
                let wanted =
                    if shouldBeVisible then
                        Set.add masterUrl (visible settled)
                    else
                        Set.remove masterUrl (visible settled)

                { State = reallocate projects wanted { settled with UserOverrode = true }
                  Refused = false }

    // ---- render gate ------------------------------------------------------
    //
    // The chart is reassigned only when a chart INPUT changed, so an idle page never re-runs the
    // 200 ms enter animation (Codex P2, PR #167). These are the two halves of that key the
    // visibility state owns; the shell combines them with its own inputs (host, metric, theme,
    // statistics reference).

    /// The colour assignment as a stable string. This SUBSUMES the visible set: a different set is
    /// a different assignment, and a series that changed slot must repaint even where the set
    /// happens to match. `Map` enumerates in ordinal key order, so the key is culture-independent.
    let colourKey (state: VisibilityState) : string =
        state.Colors
        |> Map.toList
        |> List.map (fun (url, slot) -> sprintf "%s=%d" url slot)
        |> String.concat ","

    /// The visible series' names in chart (ordinal) order, so a late-filled project name refreshes
    /// the series label and tooltip even though the history reference is unchanged.
    let nameKey (projects: ProjectHistory list) (state: VisibilityState) : string =
        projects
        |> List.filter (fun p -> isVisible p.MasterUrl state)
        |> List.sortBy (fun p -> p.Ordinal)
        |> List.map (fun p -> p.Name)
        // A control-character separator, so two different name lists can never collapse into one
        // key (an empty separator would let "ab" + "c" and "a" + "bc" agree).
        |> String.concat "\u001f"

    // ---- C# boundary ------------------------------------------------------

    /// Build the event's data from the shell's nullable host id and its projected histories
    /// (F# canon: convert nullable-style .NET shapes to the domain type AT the boundary). Absent
    /// host or empty history is `NoChart` — which is what keeps `Charted`'s history non-empty, so
    /// no consumer has to ask.
    let charted (hostId: Nullable<Guid>) (projects: ProjectHistory seq) : ChartedData =
        let projects = List.ofSeq projects

        if hostId.HasValue && not (List.isEmpty projects) then
            Charted(hostId.Value, projects)
        else
            NoChart

    /// C#-friendly constructor for a user toggle.
    let toggle (masterUrl: string) (shouldBeVisible: bool) : VisibilityEvent =
        ToggleRequested(masterUrl, shouldBeVisible)
