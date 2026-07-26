namespace Lattice.App.Aggregation

/// The identity a series needs to be given a colour: its master URL (the stable key) and its
/// daemon-list ordinal (the PREFERENCE for a palette slot, no longer the slot itself).
type SeriesKey = { MasterUrl: string; Ordinal: int }

/// Palette-slot allocation for the Statistics chart (design contract §2, issue #171 ruling).
///
/// The model this module exists to enforce: <b>only VISIBLE series hold a colour.</b> A slot is
/// a property of a line on screen, not of a project — a hidden project holds no slot at all
/// (its legend chip renders grey). Because the visible cap is below the palette size, there are
/// always enough slots for everything on screen, so two visible series sharing a colour has no
/// conditions under which it can occur. That is structural, not a rule anyone has to uphold.
///
/// The old model keyed colour to `ordinal mod paletteSize` for every project for life, which
/// handed two projects the same colour past ten projects before any display decision was made;
/// no visibility cap could then prevent two same-coloured lines (#171, ~27% of 11-project hosts).
///
/// The allocation state is a map from master URL to slot, threaded across rebuilds by the caller.
/// Its domain IS the visible set — presence in the map is what "on the chart" means.
module SeriesColors =

    /// The official Fluent `DataVizPalette` qualitative.1–10 set (contract §2). Colours are
    /// never invented beyond it, so this is the hard ceiling on simultaneously coloured series.
    ///
    /// HARD CONSTRAINT (contract §2): `StatisticsChart.visibleCap` must never exceed this.
    /// Raising the cap past ten would reintroduce #171 — pinned by
    /// `the visible cap never exceeds the palette size` in the Aggregation tests.
    [<Literal>]
    let paletteSize = 10

    /// No series on the chart — no colours held. The identity `allocate` starts from.
    let empty: Map<string, int> = Map.empty

    /// A series' PREFERRED slot: its daemon ordinal folded into the palette. Preference only —
    /// an occupied home slot yields to the first free one. Negative ordinals never occur but are
    /// folded for totality.
    let homeSlot (ordinal: int) : int =
        ((ordinal % paletteSize) + paletteSize) % paletteSize

    /// The slot a series currently holds, or None when it is not on the chart.
    let trySlot (masterUrl: string) (colors: Map<string, int>) : int option = Map.tryFind masterUrl colors

    /// Whether a series is on the chart (holds a colour).
    let isVisible (masterUrl: string) (colors: Map<string, int>) : bool = Map.containsKey masterUrl colors

    /// The lowest free slot, preferring `home`. Total: with every slot taken (unreachable while
    /// the cap constraint above holds) it falls back to the home slot rather than inventing a
    /// colour or failing — the degenerate case is the OLD behaviour, never a worse one.
    let private pick (taken: Set<int>) (home: int) : int =
        if not (Set.contains home taken) then
            home
        else
            seq { 0 .. paletteSize - 1 }
            |> Seq.tryFind (fun slot -> not (Set.contains slot taken))
            |> Option.defaultValue home

    /// The colour state for a new visible set, given the previous one (contract §2 rules 3–4):
    ///
    /// * a series that was ALREADY visible keeps its slot — toggling other chips can never
    ///   recolour a line that stays on screen (stability);
    /// * a series becoming visible takes its home slot if free, else the lowest free slot;
    /// * a series that left the visible set holds no colour and frees its slot.
    ///
    /// Deterministic and order-independent: newcomers are claimed in (ordinal, url) order, so the
    /// result is a function of the visible SET and the previous state — never of enumeration order
    /// or wall-clock. Duplicate URLs collapse to their first key.
    ///
    /// Accepted cost of the ruling: on an 11+ project host a series hidden and later re-shown may
    /// return in a different colour, because its home slot can have been claimed meanwhile.
    let allocate (visible: SeriesKey seq) (previous: Map<string, int>) : Map<string, int> =
        let ordered =
            visible
            |> Seq.distinctBy (fun k -> k.MasterUrl)
            |> Seq.sortBy (fun k -> k.Ordinal, k.MasterUrl)
            |> Seq.toList

        let kept =
            ordered
            |> List.choose (fun k -> previous |> Map.tryFind k.MasterUrl |> Option.map (fun slot -> k.MasterUrl, slot))

        let held = Map.ofList kept

        // mapFold, not map: each newcomer's slot depends on the slots the earlier ones just
        // claimed, so the taken-set has to be threaded through the assignment.
        let claimed, _ =
            ordered
            |> List.filter (fun k -> not (Map.containsKey k.MasterUrl held))
            |> List.mapFold
                (fun taken k ->
                    let slot = pick taken (homeSlot k.Ordinal)
                    (k.MasterUrl, slot), Set.add slot taken)
                (kept |> List.map snd |> Set.ofList)

        Map.ofList (kept @ claimed)

    /// A fresh allocation with no history — the state a newly charted host starts from.
    /// On a host with ≤ 10 projects every home slot is uncontended, so this degenerates to
    /// `homeSlot ordinal`, which is what keeps those hosts' line colours exactly as shipped.
    let ofVisible (visible: SeriesKey seq) : Map<string, int> = allocate visible empty
