namespace Lattice.App.Aggregation

open System
open System.Globalization

/// Which of the four BOINC-Manager-parity credit metrics the Statistics chart plots
/// (design contract §1). Exactly one is charted at a time via the metric switcher.
type CreditMetric =
    | UserTotal
    | UserAverage
    | HostTotal
    | HostAverage

/// One project's daily credit sample, projected from GuiRpc DailyStatistics at the
/// VM boundary (no GuiRpc types in this layer, per the App.Aggregation module rule).
/// Day is the daemon's day bucket; the four fields are the cumulative totals and the
/// exponential running averages (RAC) for the user and this host.
type DailyCredit =
    { Day: DateTimeOffset
      UserTotal: float
      UserAverage: float
      HostTotal: float
      HostAverage: float }

/// One project's chartable history. Ordinal is the project's position in the daemon
/// project list (get_state order) — the chip/series ORDER, and the preferred palette slot
/// of a series that becomes visible (§2; the slot itself is allocated by SeriesColors, and
/// only while the series is on the chart). Rac is the current per-host RAC
/// (HostExpavgCredit on the live Project) used to rank the overflow
/// (§4: default-visible = top 6 by current RAC).
type ProjectHistory =
    { MasterUrl: string
      Name: string
      Ordinal: int
      Rac: float
      Daily: DailyCredit list }

/// A charted point: a real value, or None for a calendar day with no daemon record —
/// a GAP. The gap always stays in the series as a None (§2 / implementer warning #4:
/// filtering missing days out would silently join the line across them); how it RENDERS
/// is metric-split (#170) — see GapBridge.
type SeriesPoint = { Day: DateTimeOffset; Value: float option }

/// One dashed bridge across a run of unobserved days (#170): the two OBSERVED points that
/// straddle the gap. Nothing is interpolated — the bridge carries only real observations and
/// the straight dashed segment drawn between them IS the rendering, which is why it lives in
/// its own dash-stroked series instead of touching the real series' Nones.
/// The rule this type exists to encode: a total that was not observed is still a total
/// (bridge it, dashed); an average that was not observed is not an average (break it).
type GapBridge =
    { FromDay: DateTimeOffset
      FromValue: float
      ToDay: DateTimeOffset
      ToValue: float }

/// One project's line series for the chosen metric: daily points (with gap Nones)
/// plus the identity/colour facts the renderer needs. Slot is the palette slot the series
/// HOLDS while it is on the chart (allocated by SeriesColors, §2) — the renderer reads it
/// and never re-derives a colour from Ordinal, which only orders the series. Name labels
/// the legend chip and tooltip.
type SeriesSpec =
    { MasterUrl: string
      Name: string
      Ordinal: int
      Slot: int
      Points: SeriesPoint list }

/// The legend partition (§4). Chips are the ≤6 default-visible projects; Overflow
/// holds any beyond the cap, surfaced in the "+N more" flyout (empty when N ≤ 6).
type LegendPartition =
    { Chips: ProjectHistory list
      Overflow: ProjectHistory list }

/// Pure chart-shaping for the Statistics page (design contract §2/§4). No LiveCharts,
/// Avalonia or GuiRpc types: the C# renderer turns SeriesSpecs into LineSeries and the
/// palette into paints, and the ViewModel projects GuiRpc records into ProjectHistory.
/// Every decision the two-layer verification gate pins lives here so it is property-
/// testable in isolation.
module StatisticsChart =

    /// Hard cap on simultaneously visible series (§4).
    ///
    /// CONSTRAINT (§2, issue #171): this must never exceed `SeriesColors.paletteSize`. Every
    /// series on the chart holds its own slot, so a cap at or below the palette size is what
    /// makes two visible series sharing a colour structurally impossible. Raising the cap past
    /// ten needs new official palette colours FIRST, never the other way round.
    [<Literal>]
    let visibleCap = 6

    /// The metric's value on one day. Total over CreditMetric — a new metric is a
    /// compile error here, never a silent wrong column (no DU wildcard).
    let valueOf (metric: CreditMetric) (d: DailyCredit) : float =
        match metric with
        | UserTotal -> d.UserTotal
        | UserAverage -> d.UserAverage
        | HostTotal -> d.HostTotal
        | HostAverage -> d.HostAverage

    /// Projects ranked for the overflow split: current RAC descending, ties broken by
    /// daemon ordinal ascending so the order is deterministic (never RAC-jitter noise).
    let rankByRac (projects: ProjectHistory list) : ProjectHistory list =
        projects |> List.sortBy (fun p -> -p.Rac, p.Ordinal)

    /// Split the projects into legend chips (top min(6, N) by RAC) and the overflow
    /// tail. Chips are rendered in daemon-ordinal order for stable positions (RAC
    /// jitter must not reshuffle the chip row); the overflow keeps RAC order for the
    /// flyout. Only the SET membership is RAC-driven, matching §4's "top 6 by RAC".
    let partition (projects: ProjectHistory list) : LegendPartition =
        let ranked = rankByRac projects
        let chips = ranked |> List.truncate visibleCap
        let chipUrls = chips |> List.map (fun p -> p.MasterUrl) |> Set.ofList
        { Chips = chips |> List.sortBy (fun p -> p.Ordinal)
          Overflow = ranked |> List.filter (fun p -> not (chipUrls.Contains p.MasterUrl)) }

    /// Default-visible master URLs: the chip set (all when N ≤ 6, else the top 6 by RAC).
    let defaultVisible (projects: ProjectHistory list) : Set<string> =
        (partition projects).Chips |> List.map (fun p -> p.MasterUrl) |> Set.ofList

    /// The calendar-day bucket used for gap detection and history depth. The daemon
    /// writes one record per day as a Unix-time instant; the UTC date is its stable
    /// bucket, independent of the viewer's time zone (snapshot determinism).
    let private dayKey (d: DateTimeOffset) : DateTime = d.UtcDateTime.Date

    /// One project's line points for a metric: exactly one point per calendar day from
    /// its first to its last record, a real value where a record exists and None for a
    /// missing day (§2; the None is unconditional — whether it renders as a break or gets a
    /// dashed bridge is gapBridges' call, #170). Days that collapse to the same bucket keep the
    /// first record (lenient-parse guard, mirroring ProjectRows.compute). Empty history
    /// yields no points.
    let buildSeriesPoints (metric: CreditMetric) (daily: DailyCredit list) : SeriesPoint list =
        match daily with
        | [] -> []
        | _ ->
            let byDay =
                daily
                |> List.map (fun d -> dayKey d.Day, valueOf metric d)
                |> List.distinctBy fst
                |> Map.ofList
            let days = byDay |> Map.toList |> List.map fst
            let first = List.min days
            let last = List.max days
            let span = (last - first).Days
            [ for i in 0..span ->
                  let day = first.AddDays(float i)
                  { Day = DateTimeOffset(day, TimeSpan.Zero)
                    Value = Map.tryFind day byDay } ]

    /// The visible line series for a metric, in daemon-ordinal order. Visibility is READ OFF
    /// the colour state (§2, #171): a series is on the chart exactly when it holds a slot, so
    /// there is no second visibility set that could disagree with the colours. A project is
    /// charted when it holds a slot AND has at least one point; a fully-empty history
    /// contributes nothing (and its slot simply goes unused this frame).
    let seriesFor
        (metric: CreditMetric)
        (colors: Map<string, int>)
        (projects: ProjectHistory list)
        : SeriesSpec list =
        projects
        |> List.sortBy (fun p -> p.Ordinal)
        |> List.choose (fun p ->
            match SeriesColors.trySlot p.MasterUrl colors with
            | None -> None
            | Some slot ->
                match buildSeriesPoints metric p.Daily with
                | [] -> None
                | points ->
                    Some
                        { MasterUrl = p.MasterUrl
                          Name = p.Name
                          Ordinal = p.Ordinal
                          Slot = slot
                          Points = points })

    /// Whether a metric's gaps are BRIDGED with a dashed segment, or left as hard breaks
    /// (#170 ruling). Total over CreditMetric — a new metric must choose its own gap
    /// semantics here, never inherit one silently (no DU wildcard).
    ///
    /// The two CUMULATIVE metrics bridge: the quantity provably existed on the unobserved
    /// days and was monotonic, so a dashed segment honestly renders "value existed,
    /// observation missing". The two AVERAGE metrics (RAC) do not: any drawn slope across
    /// the gap fabricates a trend, dashed or not, so the break IS the honest rendering.
    let bridgesGaps (metric: CreditMetric) : bool =
        match metric with
        | UserTotal -> true
        | UserAverage -> false
        | HostTotal -> true
        | HostAverage -> false

    /// The dashed bridges for one series' points under a metric (#170). For a bridging
    /// metric: one bridge per RUN of missing days (a 3-day gap is one bridge, not three),
    /// spanning the two observed points on either side, in day order. For a non-bridging
    /// metric: none — the Nones stay visible as breaks.
    ///
    /// Derived from consecutive OBSERVED points more than one calendar day apart, so it needs
    /// no assumption about the grid beyond the day spacing the points themselves carry, and
    /// it can never emit a bridge over an observed day.
    let gapBridges (metric: CreditMetric) (points: SeriesPoint list) : GapBridge list =
        if not (bridgesGaps metric) then
            []
        else
            points
            |> List.choose (fun p -> p.Value |> Option.map (fun v -> p.Day, v))
            |> List.pairwise
            |> List.filter (fun ((fromDay, _), (toDay, _)) -> (toDay - fromDay).Days > 1)
            |> List.map (fun ((fromDay, fromValue), (toDay, toValue)) ->
                { FromDay = fromDay
                  FromValue = fromValue
                  ToDay = toDay
                  ToValue = toValue })

    /// Count of REAL (non-gap) points in a series — the marker rule's denominator.
    let realCount (s: SeriesSpec) : int =
        s.Points |> List.sumBy (fun p -> if p.Value.IsSome then 1 else 0)

    /// Marker geometry size (§2, warning #5): circles (8px) while the densest visible
    /// series has ≤30 real points, pure line (0) beyond — evaluated on the LONGEST
    /// visible series' real-point count, re-derived whenever data depth or filters change.
    let markerSize (visible: SeriesSpec list) : float =
        let densest = (0 :: (visible |> List.map realCount)) |> List.max
        if densest <= 30 then 8.0 else 0.0

    /// Compact axis label (§2): ≥1M → millions with one forced decimal ("6.0M", matching
    /// the reference renders — the contract's "#.#M" shorthand); ≥1k → thousands, decimal
    /// only when non-integral ("30k", "1.2k"); else the integer ("0", "820"). The k/M
    /// suffixes are fixed Latin literals (never localised); the mantissa/integer use
    /// CurrentCulture separators. Credit is dimensionless — no unit suffix. The sign is
    /// carried through for totality even though credit is never negative.
    let compactLabel (value: float) : string =
        let c = CultureInfo.CurrentCulture
        let magnitude = abs value
        if magnitude >= 1_000_000.0 then (value / 1_000_000.0).ToString("0.0", c) + "M"
        elif magnitude >= 1_000.0 then (value / 1_000.0).ToString("0.#", c) + "k"
        else value.ToString("0", c)

    /// Days of history for the status strip ("N days of history") — the depth of the
    /// deepest project (distinct calendar days). Zero when nothing has history.
    let historyDepthDays (projects: ProjectHistory list) : int =
        projects
        |> List.map (fun p -> p.Daily |> List.map (fun d -> dayKey d.Day) |> List.distinct |> List.length)
        |> fun depths -> (0 :: depths) |> List.max

    /// Whether another series may be toggled on: the ≤6 cap (§4). At the cap, the
    /// overflow flyout's remaining checkboxes disable until one is unchecked.
    let canAddSeries (visibleCount: int) : bool = visibleCount < visibleCap
