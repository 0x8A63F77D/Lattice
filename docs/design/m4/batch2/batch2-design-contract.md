# Lattice — M4 charts batch 2 — design contract

> Owner-approved design handoff for the **task timeline page** (new) and the **Daily output
> metric** (Statistics page extension). This README is the contract, mirroring
> `statistics-design-contract.md` (batch 1): everything marked **[machine-gated]** is pinned
> pixel-exactly via deterministic headless PNG snapshots; the **interactive lane** rides
> one-time owner eyeball and stays default-ish. Where this contract conflicts with batch-1
> pins, batch 1 wins. Reference renders under `img/`; interactive spec `M4-Batch2-Spec.html`
> (offline). Evidence base: `hole-rendering-research.md` (landed beside this contract; also
> posted on the issue #202 bundle) — decision log at the end of this file records where owner
> rulings diverged from it and why.

Data rulings fixed by #202: SQLite persists **task observation events only** (retention
user-configurable); credit history stays daemon-side (`get_statistics` on demand); ≤1 h
`get_old_results` backfill at connect (**completion observations** — point events, not
intervals; see §5 and the decision log). Never render fabricated data: unobserved time is a
hole, shown as such; nothing is ever interpolated, bridged, or connected across a hole.

---

## Surface A — Task timeline

### 1. Layout ruling

Per-host swimlanes, each a **stacked concurrency density band**: height = concurrently
running tasks at that instant, colour = project (batch-1 palette rules inherited verbatim —
`DataVizPalette` qualitative.1–10, same hex both themes, colours belong to visible series,
cap ≤ palette size). One shared time axis. 3-host fleets get tall lanes (~80px); dense
fleets collapse to 28px bands — 10 hosts fit one screen without scrolling
(`img/timeline-dense-10hosts.png`).

**Project overflow (> 10 distinct projects in window) [machine-gated]:** ranking = each
project's total observed **concurrency-seconds within the current rendered window**; the
top 10 take palette colours, the rest aggregate into one **Other** band segment —
height-conserving: the rendered concurrency total always equals the observed total
(dropping series would fabricate concurrency by omission). Ties break by project display
name, then by `MasterUrl` — the project's identity key in the GuiRpc model; two distinct
URLs can share a display name, and the ordering must be total for the gated output to be
stable. The ranking is recomputed when the window changes (same family as the Statistics
visible-set colour behaviour). The `cap ≤ palette size` invariant holds: coloured series
never exceed 10 — Other is a neutral aggregate segment and takes no qualitative slot. Its
concrete colour is deliberately not pinned here; the constraints are: a neutral, pairwise
distinguishable from the hole grey and from all ten qualitative colours, holding in both
themes — the token is the implementation's choice, final call at the owner eyeball gate.
Hover/tooltip enumerates Other's member projects and their counts (existing channel
style); the accessible name carries the same enumeration.

Band geometry is **stacked-step with left-hold semantics [machine-gated]**: an observation
at tick T ("N running") holds forward until the next observation tick or the start of a
hole; nothing is drawn after the last observation (the window is already anchored on it).
Left-hold states what was known at T. Right-hold would project a later observation
backwards into time before it happened — fabricating history, the same violation as a
linear ramp, differing only in degree; interpolating (direct-line) area geometry is banned
for the same reason.

Host expansion into per-task rows (wireframe 3d shape: expand one host at a time) is a
**follow-up batch** — not part of this contract's gate.

### 2. Hole rendering [machine-gated]

**Definition.** A hole is an unobserved interval **bounded by observations on both sides**,
rendered **per-host** (an aligned column across all lanes is emergent, not an element).
Outside the store's coverage (before first-ever observation / after last) is **bare canvas,
not a hole**. A hole is a typed, materialized record in the store — an absent row is
indistinguishable from "no such time" at render time and must never be the representation.

**Visual — one treatment for both hole classes.**

| role | token | light | dark |
|---|---|---|---|
| hole fill | `SystemFillColorSolidNeutralBackground` | `#F3F3F3` | `#2E2E2E` |
| in-hole label | `TextFillColorSecondary` | `#616161` | `#ADADAD` |
| lane baseline | — | `#E0E0E0` | `#4A4A4A` |

Pure fill only: **no border/hairline, no hatch or dash pattern, no red frame, no second
grey.** Banned tokens: `Stencil*` (semantics = "content is loading"), all alpha `*Disabled`
fills (invisible over a chart canvas). The neutral is reserved — never assigned to a project.

**Minimum rendered width + label ladder** — treatment = f(hole width in **DIPs**), a
pure function (no wall clock, no DPI-dependent branches — a DIP threshold is
scale-invariant by construction, because label glyphs scale with the same DIP-to-device
transform):

- **Minimum rendered width = 48 DIPs (the label threshold).** An **isolated** hole
  whose true width is `< 48` is exaggerated to 48, anchored on the centre of its true
  interval — exaggeration exists to make room for the label — so the hole carries a
  duration label, with the fill, no-hairline and no-pattern rulings untouched. Accepted,
  explicit cost: **scale distortion** — a short hole renders wider than its true share and
  may cover pixels of adjacent observed data; the tooltip and accessible name always carry
  the true values.
- **Grouping.** Holes whose exaggerated render intervals would overlap or touch (transitive
  closure) form a **group**, dispatched by the group's **true span** (first member's true
  start → last member's true end, in DIPs):
  - **Compact merge (true span `< 48`):** the group merges into one 48-DIP rendered hole,
    **anchored on the centre of the group's true bounding span** (the same centre-anchor
    rule as an isolated hole, applied to the group's true interval — the fixture geometry
    is thereby unique); its label shows the **sum of the member holes' true durations**;
    at `≥ 170` the reason is shown only when all members share one (mixed reasons →
    duration only).
  - **Dense regime (true span `≥ 48`):** per-hole exaggeration is **abandoned** — members
    render at their **true geometry**, so observed data is never swallowed and nothing is
    overpainted — and the group shares one summary label riding **the same width ladder as
    any rendered hole** (referenced, not a second ladder): at `≥ 48` the summed duration
    (`24.0 h`); at `≥ 170` the full `3 holes · 24.0 h total` (reason appended when uniform
    across the group); below 170 the count is witnessed by the hover detail and the
    accessible enumeration. Per-hole labels are geometrically impossible where they would
    collide, so the visibility carrier shifts from per-hole label to per-group label. The
    group label's glyphs float over observed pixels within the group's bounding span — an
    inherent property of group-as-carrier (floating text is not surface overpainting; the
    member rectangles remain the only grey).
- **Member detail (both group forms).** Hover lists each member hole's true range and
  reason (same style as the Daily output gap-span tooltip). The group's **accessible
  description enumerates every member's true range · duration · reason** — hover is not an
  equivalent channel for assistive technology, and the accessibility tree is not a visual
  element, so this sits outside the no-new-visual-elements ruling. Visual channel = group
  summary + hover detail; accessible channel = full detail: two channels of the same
  true-values principle.
- `≥ 48` → duration label
- `≥ 170` → reason + duration: `Lattice not running · 8.2 h` / `Host unreachable · 2.5 h`

**Duration format — one definition, referenced by label, tooltip and accessible name (a
single formatting function in code, never three copies; the intervals are mutually
exclusive):** `≥ 0.1 h` → hours, one decimal (`8.2 h`); `1 m ≤ duration < 0.1 h` → whole
minutes (`4 m`); `< 1 m` → whole seconds (`30 s`). **Every numeric output of this
formatter — the one-decimal hours value included — rounds half away from zero**
(`8.25 h` → `8.3 h`; the same declared rule as the delta formatter). Boundaries land in the larger
unit — exactly `1 m` formats as minutes, exactly `0.1 h` as hours — and a **rounded**
value that reaches the next unit's boundary is represented in that larger unit too:
`59.6 s` → `1 m`; `5 m 58 s` → `6 m` = `0.1 h` → `0.1 h`.

Every rendered hole (single hole or dense group) carries its own label, regardless of lane
position — no aligned-column deduplication. A fleet-wide outage column therefore repeats
the label once per lane; that repetition is semantically truthful (each host was
independently unobserved).

**Label fit and clamping [machine-gated].** A label never overflows the rendered span of
the hole or group it belongs to — content degrades down the ladder until it fits — which
makes cross-group label collision geometrically impossible (no collision-handling rule
exists or is needed). When a rendered hole (single or dense group) intersects the viewport
edge during panning, its label slides to stay within the visible intersection (sticky);
the exaggeration geometry and time anchoring never move, only the label, and the content
degrades down the ladder as the intersection narrows. An intersection `< 48` renders
unlabelled — an explicit, accepted residual: panning is transient, the label returns as
the hole scrolls into view, and the accessible channel stays complete throughout.

**Reason vocabulary — exactly two user-facing strings, no others:**
`Lattice not running` (the app was off — routine) and `Host unreachable` (Lattice was
watching but could not reach the host — exceptional; same phrase as the batch-1 InfoBar).

**Baseline = per-host coverage.** The 1px lane baseline is drawn **only over observed
spans** — line present + band at zero = observed idle; line absent = not watching. This is
the load-bearing three-way distinction: colour band = running · baseline at zero = idle ·
grey fill = no data.

**Accessibility.** Every hole region carries an accessible name:
`Lattice not running · 23:30 → 07:41 (8.2 h)`. WCAG 1.4.11 conformance rides the text
channel: every hole belongs to a **labelled graphic** — an isolated or compact-merged hole
carries its own label, and a dense group is the labelled graphic for its members (none
unlabelled) — + tooltip/accessible name — see decision log.

**Live unreachable host:** batch-1 §5 InfoBar idiom (severity=Warning, not dismissable,
`Retry` wired to reconnect): `rack-02 unreachable since 15:02.` The historical record of
that outage renders as an ordinary hole (reason via label/hover).

### 3. Time navigation [machine-gated window model; feel = interactive lane]

- `SegmentedControl` presets: `6 h · 24 h · 7 d · 30 d`, default **24 h**.
  Window = `[end − preset, end]`.
- **Follow now** `ToggleButton`: on ⇒ `end` = latest observation tick (**not wall clock**);
  any manual pan switches it off. Drag/horizontal-wheel pans.
- Continuous wheel zoom **rejected** (window would be unenumerable → snapshot matrix
  undefined). Optional compromise if ever wanted: wheel steps between the four presets.
- `Jump to date` CalendarDatePicker flyout: optional, not gated, days beyond retention
  disabled.
- Snapshot determinism: fixtures pin `end` and the dataset.

### 4. Chrome (all native FluentAvalonia)

- **NavigationView item:** `Timeline`, Fluent System Icon `GanttChart` (regular at rest,
  filled when selected), **after Statistics, before Event log** (batch-1 position pins
  undisturbed).
- Command bar: title + zoom `SegmentedControl` + spacer + `Follow now` + `Updated Ns ago` +
  refresh `IconButton` (batch-1 idiom).
- Legend row: project chips (display-only on this page — the filter semantics stay on
  Statistics; an `Other` chip appears when the §1 overflow aggregation is active) +
  **one** non-interactive entry `No data` (swatch = hole fill with a chrome
  border `#D1D1D1` / `#525252`; the border is a chrome affordance, not chart grammar).
- Status strip: `N hosts · N intervals in view · N days retained` left,
  `Polling every 5s` right.
- Page reads the shell host scope; `All hosts` renders every **configured host in scope**
  as a lane. An unreachable host keeps its lane and is never dropped from the timeline
  while its outage is in progress: the live outage surfaces via the §2 InfoBar, and on the
  lane the time after that host's last observation is **bare canvas** — per the §2
  definition a live outage has no right-bound observation, so the interval becomes an
  ordinary hole only once observations resume.

### 5. States (issue #88 idiom)

- **Cold start / first run:** the window is §3's `[end − preset, end]` as always; **data
  starts at the store's first task-state observation** — backfilled completion
  observations are excluded from every coverage anchor, or backfill would move the
  coverage start and contradict backfill-never-changes-hole-geometry — and the window's
  pre-observation portion renders as bare canvas with caption `no history before {time}`; dashed accent marker at
  connect time; status strip: `history starts 14:32`. The ≤1 h `get_old_results` backfill at connect
  yields **completion observations** (task finished at T with final elapsed E — point
  events, not intervals); they are written to the observation store, but the concurrency
  density band does **not** render them: the pre-connect window stays unobserved (a hole
  where bounded by observations, bare canvas otherwise), and backfill never changes hole
  geometry. This prohibition is scoped to chart geometry — no chart element renders
  backfill data and no new visual element may be added to consume it — but non-geometry
  predicates may read completion observations: the Empty state's `none observed
  completing in the last hour` claim is established from them.
- **Pre-observation (configured hosts, zero task-state observations in the store —
  backfilled completion events alone do not leave this state):** the time axis
  is **not rendered** — no data, no axis, so §3's observation-anchored `end` needs no
  substitute and the no-wall-clock pin is untouched. The lane area shows each
  unreachable host's §2 InfoBar and a centered caption
  `Waiting for the first observation…`; the first observation to arrive forms the axis
  per §3. Not the No-hosts state — that is reserved for zero configured hosts.
- **No hosts:** shown only when **zero hosts are configured** (an all-unreachable fleet
  renders its lanes — InfoBars plus retained history — never this state) — centered — `ServerMultiple`
  icon 28px `#C7C7C7`, `No hosts connected`, caption `Add a host to start observing task
  activity.`
- **Empty:** predicate — no observed task activity within the observed spans ∧ no
  completion observation within the most recent observed hour (anchored to the latest
  observation tick, not wall clock). Copy: `No observed task activity` +
  `No tasks observed running since {time}, and none observed completing in the last
  hour.` Both clauses explicitly quantify over observations — holes and pre-observation
  canvas can never make this copy assert inactivity across unobserved time (missing data
  is not a negative observation).
- **Loading:** batch-1 ProgressRing idiom, `Loading timeline…`

### 6. Tooltip (interactive lane; formats binding)

- Hole hover: reason · range · duration — `Lattice not running · 23:30 → 07:41 · 8.2 h`.
- Band hover: per-project concurrent counts at the hovered instant; task-level detail lists
  task name, project, host, elapsed, progress (live) or final runtime (historical).
- Durations use the §2 duration format (the single shared formatting function); dates/times
  `CultureInfo.CurrentCulture`; harness pins `en-US`.

---

## Surface B — Daily output (Statistics extension)

- **Fifth `SegmentedControl` item** `Daily output`, after `Host average`. Batch-1 layout
  ruling (one chart, metric switcher) untouched; chips / Top-6 / `+N more` flyout / colour
  allocation / animation all inherited.
- `StackedColumnSeries`, **one column per day in the axis span `[min, max]`** (the gap
  predicates' N domain — a day unobserved by every visible project is a bare column, never
  an omitted one; collapsing all-project gaps is exactly what implementer note 6 bans);
  segment colour = project. Y axis compact labeler and axis styles inherited from
  batch-1 §2.
- **Data source [machine-gated]:** `total(P, N)` = project P's **`HostTotalCredit`** on
  day N for the host selected by the batch-1 host-scope rule (the Statistics page charts
  one host; on `All hosts` the command-bar ComboBox picks it) — never the account-wide
  `UserTotalCredit`: this chart shows the selected host's output, and the account-wide
  total silently includes every other host on the account, possibly never observed by
  Lattice at all. A rendered segment's value is `total(P, N) − total(P, N−1)`.
- **Gap semantics [machine-gated] — normative predicates, the sole source of truth.**
  **Domain: P ranges over the current visible project set** (the batch-1 chip filtering; a
  hidden project does not exist for this chart — it contributes no segment and does not
  block bareness; toggling chips recomputes, same family as the batch-1 visible-set
  behaviour; the partial-day disclosure quantifies over the same domain). **An empty
  domain evaluates nothing:** with zero visible projects the predicates are not evaluated
  and the page falls into the batch-1 nothing-to-chart state — a vacuous quantification
  would make every day bare with an empty-sum "total" of 0, a fabricated value, not an
  observation. **N ranges over the axis span `[min, max]` of the visible projects'
  observed days** — days outside it are not columns (the same family as the timeline's
  bare-canvas-outside-coverage semantics). `observed(P, N)` ⟺ a `daily_statistics`
  record for day N exists for P on the selected host (anchored directly to the GuiRpc
  data model).
  1. `segment(P, N)` renders ⟺ `observed(P, N) ∧ observed(P, N−1)` — visible project P
     contributes a segment to day N exactly when both endpoints are observed.
  2. Day N is a **bare column** (no bars at all) ⟺ no visible P satisfies predicate 1
     for N. The bare-run hover covers each **maximal run of bare columns**; its day count
     is the **inclusive number of bare columns** in the run.
  3. For a bare run `[start..end]`, the hover shows the span total ⟺
     `∀ visible P: observed(P, start−1) ∧ observed(P, end)` — then
     `spanTotal = Σ_P (total(P, end) − total(P, start−1))`, covering exactly
     `[start..end]` and overlapping no rendered segment (the first one after the run is
     `end+1`, whose increment starts at `end`):
     `No daily values · 07-15 → 07-17 (3 days) · 104,500 over the span`.
     Otherwise (staggered observations) the hover omits the total:
     `No daily values · 07-15 → 07-17 (3 days)`. The span total is never drawn,
     estimated, or amortized — shown only when exact, hover only. `spanTotal` is a
     delta-valued quantity: it formats with the **delta formatter** defined at the
     tooltip bullet below (declared rounding, `< 0.01` for positive sub-cent values) —
     not the inherited integer cumulative format. Each maximal bare run
     also carries an **accessible node** whose name equals its hover content (range ·
     inclusive day count · span total when exact) — with no bar to focus, the run itself
     is the focusable node; hover is not an equivalent channel for assistive technology,
     and the accessibility tree is not a visual element (the same principle as the hole
     groups' accessible enumeration).

  Everything below follows from the predicates and is **non-normative illustration**:
  - *Per-project gap* (from 1): a project's run of unobserved days and its first observed
    day after it contribute no segment for that project — the bar analogue of the #170
    "an average that was not observed is not an average — break it" ruling.
  - *Partial day* (from 1 ∧ ¬2): a day computable for some projects renders exactly their
    segments — suppressing the whole column would discard valid observations. The
    column's tooltip and accessible name disclose the shortfall (`partial day` + which
    projects have **no daily value** that day — a missing endpoint, not necessarily an
    unobserved day: a project observed on N but lacking N−1 is observed, its increment is
    merely unavailable; existing channel style); the residual reads-as-complete risk
    rides the text channel.
  - *Synchronized recovery* (from 2): when every project shares a gap and resumes on day
    N, no segment exists on N, so N is bare and sits inside the hovered span — the
    original single-project "gap plus its first observed day" semantics reappear as a
    special case, needing no rule of their own.
  - *First column* (from 1 + the N domain): `min − 1` is unobserved by the definition of
    `min`, so day `min` never has a segment — the axis's first column is always bare:
    the same "first observed day after a gap has no bar" semantics reappearing at the
    axis origin.

  **Accepted cost:** this chart is not area-conserving — "how much in total" is answered
  by the **Host total** metric (the cumulative counterpart of this chart's
  `HostTotalCredit` source; User total is account-wide and answers a different scope),
  this chart answers "how much on which day", and days it cannot answer are empty.
- Grouped bars **rejected** (6 visible × 90 days = 540 sub-detection-floor slivers; loses
  the per-day total; per-project comparison is what chip filtering is for).
- Tooltip: **daily deltas** format with group separators at **declared display
  precision**: a non-integral delta shows two decimals (`1,204.50`), rounded **half away
  from zero** — pinned explicitly because .NET's `Math.Round` defaults to banker's
  rounding, which an implementation must not inherit by accident — and an integral delta
  shows as an integer. A stated rounding rule is not fabrication; presenting invented
  values as observations is. A **positive** delta that would round to `0.00` displays
  `< 0.01` — real output is never asserted to be zero (the same family as
  missing-data-is-not-a-negative-observation). One delta formatter, shared by tooltip,
  accessible name, **and every other delta-valued display — including the bare-run
  `spanTotal`** (the §2 single-definition principle). The batch-1 §6 exact-integer rule
  continues to govern the shipped cumulative metrics — not these deltas.

---

## Animation [HARD]

Batch-1 §3 verbatim: `AnimationsSpeed = 200ms`, `EasingFunction = BuildCubicBezier(0,0,0,1)`
(`--durationNormal` + `--curveDecelerateMid`). Applies to zoom-preset switches, live-edge
updates, metric switches. No looping/pulsing anywhere.

## Retention Settings row

`FASettingsExpander`: `History` icon · `Task history retention` · caption `Keep observed
task intervals for this many days` · `SpinButton` (default 90) + `days`. Expanded sub-row:
`On disk: 12.4 MB · 38,412 intervals · oldest 05-04` + `Clear history…` `Button` with a
confirmation flyout (interactive lane). Nothing beyond this is needed.

## ⚠ Implementer notes (LiveCharts2 mapping — every element is native)

1. Density band = **stacked step series** with **nullable points** (left-hold, §1 — the
   only conforming geometry) — holes break the geometry naturally. A direct-line
   `StackedAreaSeries` interpolates between poll ticks (fractional concurrency, invented
   ramps) and is banned. If LiveCharts2 offers no native step variant, building the step
   from duplicated endpoints is implementation freedom. Never drop missing rows (silent
   join); materialize nulls.
2. Lanes = one `CartesianChart` per host, X axes synchronized via shared Min/MaxLimit
   (shared pan/zoom).
3. Hole = `RectangularSection` — `Fill` is the grey, `Label` is the duration/reason text.
   Live edge = zero-width section (`Xi == Xj`) stroke. No custom Skia layer exists anywhere
   on the page. A dense group's summary label rides a second, **label-only**
   `RectangularSection` spanning the group's bounding interval — no fill, no stroke, zero
   surface rendering (the members stay the only grey; the no-overflow span for the group
   label is this bounding interval). If an empty-Fill section turns out to produce any
   surface rendering or hit-test artifacts, that is an implementation-time escalation
   item — restoring a fill as a workaround is banned (it silently restores overpainting).
   Legibility of the floating glyphs over coloured bands is not pinned here; it belongs to
   the owner eyeball gate.
4. Daily output gaps = missing/nullable values (no bar) — zero extra **visual** elements;
   the gap-hover span total goes through tooltip logic. The bare-run accessible node is an
   accessibility-tree entry, not a visual element (see the gap semantics).
5. LiveCharts2 paints are not DynamicResource-aware: rebuild hole/axis/baseline paints on
   theme switch (batch-1 warning #1).
6. Ordinal/collapsed axis modes silently delete holes — the X axis must stay linear time.
7. The label ladder runs on **DIPs** — glyphs scale with the DIP-to-device transform, so
   a DIP threshold yields the same glyph fit at every render scale; a device-pixel
   threshold would halve the available label room at 2× — the exact silent weakening this
   note used to guard against, mirrored (the unit followed the threshold's semantics when
   the perception floor gave way to the label threshold; see the decision log).

## Snapshot matrix (machine gate)

Timeline: 2 themes × {24 h baseline (overnight hole as an aligned fleet-wide column —
every lane labelled — + historical unreachable + idle span),
7 d dense (10 hosts, 41-DIP true-width holes — exaggerated to the 48-DIP minimum,
labelled), cold start, no-hosts, empty} + a synthetic **ladder fixture** (one hole per
width rung, including a sub-48-DIP hole exaggerated to the minimum width, a compact
merge — group true span < 48 DIPs — and a mixed-reason merge) rendered at 1× and 2× DPI —
the pair now verifies **DIP-threshold consistency across render scales** + a **dense-regime
fixture** (30 d of nightly holes: true-geometry members under a `N holes · X h total`
group label) + a **narrow-window equivalence pair** (the same dataset at 6 h / 24 h,
where no chaining occurs, renders identically to the isolated-hole rules) + a
**pre-observation fixture** (configured hosts, zero observations: no axis, waiting
caption).
Daily output: 2 themes × {12-day baseline containing a 3-day gap} + a **user ≠ host
fixture** (account-wide and host totals diverge; the chart must follow `HostTotalCredit` —
fixture values shaped from the BOINC reference implementation's `get_statistics` output,
not invented) + a **partial-day fixture** (one project computable, another missing an
endpoint on the same day — observed segments render, the column is disclosed partial)
+ a **zero-visible fixture** (all chips unchecked → the nothing-to-chart state, no chart
content).
Timeline additionally pins one **project-overflow fixture** (> 10 projects in window:
top-10 coloured + `Other` aggregate, height-conserving).
Culture pinned `en-US`; every fixture pins `end` and the dataset. ≈ 26 snapshots.
The gate has **three tracks**: (1) chart-surface pixels ride the snapshots above;
(2) predicate paths that leave no mark on chart pixels are machine-gated by unit tests
over the **extracted gap-semantics pure function** — the exact vs omitted span-total
paths of predicate 3 — **including a fractional exact-total case**, and an explicit
**delta-formatter midpoint case** whose lower hundredth is even (`1.125` → `1.13`, the
input that distinguishes the declared rule from banker's rounding for credit deltas) —
**and** the partial-day disclosure of the `1 ∧ ¬2` path (the
`partial day` tooltip/accessible-name content, equally pixel-invisible) must each have
cases; the **shared duration formatter** likewise has unit-boundary cases (a sub-minute
value, exactly `1 m`, exactly `0.1 h`, a value that rounds up to a unit boundary, and an
**exact-halfway tie whose lower value is even** — `58.5 s` → `59 s`, the case that
distinguishes the declared half-away-from-zero rule from .NET's banker's rounding),
and the ladder fixture's sub-minute hole uses the **halfway-tie duration itself**
(`58.5 s`, rendered label `59 s`) so the chart label path is pixel-gated end to end —
a label path that bypasses the shared helper for midpoint-to-even formatting fails the
snapshot, not just the policy test; (3) UI wiring that neither pixels nor the pure function witness — **every
accessibility-tree carrier this contract mandates** — is machine-gated by **headless
accessibility assertions**. Currently that is: the bare-run node (presence, name,
focusability; both the exact-total and omitted-total forms), the partial-day column's
disclosure name, the timeline hole/group accessible **names** plus the group's accessible
**description** carrying the member enumeration (distinct automation properties — each is
asserted on the property §2 assigns it, no duplication), and the `Other` overflow band's
accessible name with its member/count enumeration (§1); any accessibility carrier a
future revision mandates joins this track by rule, not by a new listing.
Track 3 must assert against the **built accessibility tree itself** (the automation peer
queried after a headless settle), never against a ViewModel exposing the right string —
the classic false-green shape for wiring assertions (correct wiring code, absent end
state). Tracks 2 and 3 are existing canons made explicit in the gate wording
(decision-logic-as-policy-module; the headless settle family); substrates stay with the
canons, not this contract.

## Decision log (owner rulings; where they diverge from the research report, recorded)

- **Hole = calm light grey, per-host** — research 5c/5d fusion family, HA/Statuspage
  precedent line.
- **Rejected:** hatching (4a), inverse observed-tint (4b), axis-break collapse (4c, may
  revisit for long ranges), any gap-spanning connector incl. dashed (5e-i — dash means
  "estimate" industry-wide), dashed baseline (5e-iii — two dash vocabularies + degradation
  contradiction).
- **Dark solid block for unreachable (9a): superseded.** Two grey tiers read wrong in one
  theme or the other → one treatment, reason via text/hover.
- **3:1 boundary hairlines: removed by owner aesthetic ruling** (they fuse into a dark mark
  at narrow widths — same disease as the dark block). The research report's 1.4.11 carry
  moves to the label (labelled-graphic exemption) + tooltip/accessible name. Recorded as a
  conscious deviation from the report's recommendation.
- **Coverage tracks (5b, global + per-host mini): removed.** Semantically mushy under mixed
  coverage ("any host observed" ≈ only "Lattice running", which the per-host baselines
  already carry unambiguously) and the only custom-Skia element on the page. Fallback if
  30 d holes prove illegible in practice: thicken baselines to 2px — do not resurrect the
  track.
- **Daily-output span-bin (average-rate wide bar): superseded** by empty columns + hover
  span total (#170 hard-break analogue). Ugly + "height = average rate" needed explaining.
- **Terminology:** exactly two reason strings, both pre-existing plain language
  (`Lattice not running`, `Host unreachable`); "not observed" and all invented glyphs (⊘)
  removed.
- **Backfill = completion observations, not intervals (round-1 review narrowing).**
  `get_old_results` carries only terminal records (completion time + final elapsed); no
  sequence of running/suspended/preempted states exists to reconstruct, so expanding them
  into running intervals would fabricate execution geometry (a task preempted several times
  would render as continuously running) — violating this contract's own no-fabricated-data
  rule. Ruling: backfill writes completion observations to the store; the density band
  renders nothing from them and hole geometry is unchanged by backfill (non-geometry
  predicates, e.g. the Empty state's completed-in-last-hour claim, may consume completion
  observations). This also clarifies
  the #202 ruling's "backfills observations at connect": observations = completion events,
  not intervals. (Raised by Codex review round 1 on the landing PR.)
- **Narrow-hole visibility: minimum rendered width, not contrast (round-1 review; owner
  ruling).** A sub-48px hole with the muted fill and no label is effectively invisible
  (≈1.14–1.20:1 vs the canvas per the research report), collapsing the three-way
  running/idle/no-data distinction exactly where holes are common (7 d / 30 d windows).
  Fix: minimum rendered width = the 48px label threshold, so an unlabelled hole cannot
  exist and WCAG 1.4.11 rides the labelled-graphic path for **every** hole. Explicit
  accepted cost: scale distortion (a short hole renders wider than its true share and may
  cover adjacent observed pixels); tooltip/accessible name carry the true values. Rejected
  alternatives: restoring 3:1 boundary hairlines (violates the standing aesthetic ruling
  above) and raising fill contrast (changes the reserved neutral). The former
  `< 3 px → 3 px` detection-floor rung is superseded by this rule. (Raised by Codex review
  round 1 on the landing PR.)
- **Aligned columns: every hole labels itself (rounds 2–3 review; owner ruling).** The
  minimum-width rule's "no unlabelled hole exists" argument conflicted with the original
  topmost-lane-only label rule for aligned columns, leaving lower-lane holes unlabelled.
  Ruling: every rendered hole carries its own duration label (reason at ≥ 170 px),
  regardless of lane position. Explicit accepted cost: a fleet-wide outage column repeats
  the label once per lane — semantically truthful, since each host was independently
  unobserved. Rejected alternatives: topmost-lane deduplication (lanes are one chart per
  host and the page can scroll, so the top label can leave the viewport while lower holes
  stay visible and unidentified) and a shared cross-column label (a new visual element,
  against the standing no-new-elements ruling). (Raised by Codex review round 3.)
- **Duration format made adaptive (round-4 review).** The every-hole-label ruling turned
  the `0.0 h` contradiction from latent to inevitable: a bounded hole shorter than ~3 min
  is realistic at the 5 s polling cadence, and a mandatory label must carry true values.
  The one-decimal-hours pin is therefore revised to the §2 adaptive format (`8.2 h` /
  `4 m` / `30 s`) — one formatting definition shared by label, tooltip and accessible
  name. (Raised by Codex review round 4.)
- **Dense-regime rendering for chained holes (round-5 review; owner ruling).** At wide
  windows the 48px exaggeration is transitive: in a 30 d window (~1,000 device px) a
  nightly hole exaggerates to ~1.4 days, consecutive nights chain-overlap, and the round-1
  merge rule would fuse a whole month into one near-full-width grey — swallowing observed
  concurrency, categorically worse than the ruled adjacent-pixel distortion. Ruling: two
  regimes dispatched by the group's true span. `< 48px` → compact merge (one 48px hole,
  summed label). `≥ 48px` → members render at true geometry (nothing observed is
  swallowed, nothing overpainted) under one group summary label (`N holes · X h total`,
  reason appended when uniform); sub-pixel members are witnessed by the count, hover and
  the accessible enumeration. Explicit accepted cost: individual holes in a dense group
  are visually faint and narrow (their true share); the owner retains the right to rework
  after seeing the implementation (the implementation PR carries the owner eyeball gate
  regardless). Rejected alternatives: unmerged overpainting (same-colour rectangles fuse
  visually anyway, with colliding labels); rendering the group at `max(48px, true span)`
  (a nightly chain's true span is the whole month — still swallows observed data);
  window-scaled minimum widths (violates the labelling ruling); accepting month-wide
  fusion as a known cost (swallows observed facts — unacceptable). (Raised by Codex
  review round 5.)
- **Group labels hook into the existing ladder; labels never overflow; edge clamping
  (round-6 review).** Round 5 mandated the `N holes · X h total` summary without hooking
  it to the width ladder, so a 48–100px group could not fit its own label, and an
  overflowing label could collide with a neighbouring group's. Ruling: the group label
  rides the same ladder as any rendered hole (referenced, not copied — `≥ 48` summed
  duration, `≥ 170` full summary with count), and a label never overflows its hole/group's
  rendered span — making cross-group collision geometrically impossible instead of adding
  a collision-handling rule. At viewport edges the label slides to stay within the visible
  intersection (sticky) and degrades down the ladder as the intersection narrows; an
  intersection `< 48` renders unlabelled, recorded as a **controller-accepted residual**
  (panning is transient, the label returns naturally, the accessible channel stays
  complete; the implementation PR's owner eyeball gate is the final veto). (Raised by
  Codex review round 6.)
- **Dense-group label carrier: a label-only section (round-7 review).** The per-hole
  `RectangularSection` mapping left the group summary without a native carrier: attached
  to a member, the ladder could not be measured on the group's span (and the no-overflow
  pin would trap the summary inside that member's width), while a filled section across
  the group would paint grey over the intervening observed spans. Ruling: the carrier is
  a `RectangularSection` spanning the group's bounding interval with **no fill and no
  stroke** — zero surface rendering, so the no-overpaint promise holds and the members
  remain the only grey; the no-overflow measurement span is the bounding interval. The
  label's glyphs floating over observed pixels is an inherent property of the round-5
  group-as-carrier ruling (floating text ≠ surface overpainting); glyph legibility over
  coloured bands is the owner eyeball gate's jurisdiction, not a contract pin. (Raised by
  Codex review round 7.)
- **Project overflow: Other aggregation (round-8 review).** The inherited palette cap
  (coloured series ≤ 10) was unsatisfiable on a timeline with > 10 distinct projects in
  window: the chips are pinned display-only (filter semantics stay on Statistics), so the
  visible set cannot be shrunk, and dropping series would lower the rendered concurrency
  total — fabrication by omission. Ruling: deterministic ranking by observed
  concurrency-seconds in the rendered window (ties by project name), top 10 coloured,
  rest into one neutral height-conserving `Other` segment that takes no qualitative slot;
  member detail via hover/tooltip + accessible enumeration. The Other colour is
  constrained (neutral, pairwise distinguishable from hole grey and all ten qualitative
  colours, both themes) but the concrete token is the implementation's choice under the
  owner eyeball gate — picking hex values in contract prose is design in the wrong
  substrate. Rejected alternatives: interactive chips (violates the filter-semantics pin)
  and per-lane top-N (still drops height). (Raised by Codex review round 8.)
- **Daily output total source = `HostTotalCredit` (round-8 review).** The increment
  definition never selected between the account-wide `UserTotalCredit` and the per-host
  `HostTotalCredit` that `get_statistics` both exposes, so two conforming implementations
  could show incompatible throughput. Ruling: `HostTotalCredit`, summed over the shell
  host scope — Lattice monitors a fleet, so throughput measures the monitored hosts; the
  account-wide total counts hosts outside the fleet, and one account on several hosts
  would double-count across lanes. The user ≠ host fixture pins the distinction. (Raised
  by Codex review round 8.)
- **Daily output scope: the batch-1 single-host rule wins (round-9 review; correction).**
  Round 8 pinned `HostTotalCredit` "summed over the current shell host scope", which
  cannot coexist with the batch-1 [HARD] host-scope rule (the Statistics page charts one
  host via the command-bar selector; cross-host overlay is out of scope, #148) — and this
  contract's own header says batch 1 wins conflicts. Correction: Daily output is defined
  against the host selected by that selector; the summing clause is removed; the round-8
  substance (HostTotalCredit, never UserTotalCredit, with its rationale) is unchanged.
  Recorded fairly, two misses: the summing clause entered via the landing session's
  recommendation, and the adopting ruling did not check it against the superior contract —
  one lesson: check every new pin against the batch-1 HARD set before ruling. (Raised by
  Codex review round 9.)
- **Partial-day columns render their observed segments (round-9 review).** Daily
  histories are per project and gap positions differ across projects, so a day can be
  computable for one project and not another. Ruling: the increment rule stays per
  project; observed segments render as usual; the tooltip and accessible name disclose
  the unobserved projects (`partial day`). Whole-column suppression is rejected — it
  discards valid observations, the round-5 violation class. Visual partial-day markers
  are rejected — they collide with the no-pattern and no-new-elements rulings. The
  residual reads-as-complete risk rides the text channel, same family as the stated
  not-area-conserving cost. (Raised by Codex review round 9.)
- **Gap rule scoped per project (round-10 review; conforming rewrite).** The original
  column-level wording ("the first observed day after a gap renders no bars at all")
  contradicted the round-9 partial-day ruling: with project A observed on 07-16/07-17
  and project B resuming 07-17 after a gap, the gate had no single expected output —
  A's 07-17 segment was simultaneously required and forbidden. The column-level text was
  the single-project projection of a per-project rule, and round-9 (owner-informed) had
  already fixed the scope at project level — so this is a conforming rewrite of the
  original text, not new design: per project, a gap run and its first observed day
  contribute no segment; only an all-project-unobserved day is a bare column, where the
  span-total hover lives; project-level shortfalls ride the partial-day disclosure.
  (Raised by Codex review round 10.)
- **Ranking totality: `MasterUrl` closes the tie-break chain (round-10 review).** "Ties
  break by project name" is not a total order — two distinct project URLs can share a
  display name, so enumeration order could decide who takes the last palette slot vs
  Other, destabilising the gated output across equivalent inputs. The chain is now
  concurrency-seconds → display name → `MasterUrl` (the project's identity key in the
  GuiRpc model — identity lives in the data model, which is exactly where a tie-break
  should end). Recorded fairly: the name-only tie-break entered via the landing session's
  round-8 recommendation and was adopted unchecked — a shared miss. (Raised by Codex
  review round 10.)
- **Gap semantics normalized to two predicates (round-11 review; structural).** Rounds
  8–11 produced four consecutive same-class findings — each prose restatement of the gap
  rules leaked a corner (column-level projection, partial days, synchronized recovery):
  the repeated-patches signal that one predicate was being paraphrased four times instead
  of stated once. Restructure: two normative predicates are now the sole source of truth
  (`segment(P, N)` renders ⟺ both endpoints observed; a day is bare ⟺ no segment
  renders, span-total hover on maximal bare runs), and every prose gap rule is demoted to
  a non-normative corollary. The round-11 synchronized-recovery counterexample (every
  project resuming on day N leaves N bare while the round-10 wording claimed bare =
  unobserved-by-all) is a direct consequence of the predicates, needing no rule of its
  own. Same family as the artifact-precedence rule: one formal statement holds authority,
  prose illustrates. This finding class should now be extinct — a future finding against
  the predicates themselves would be a genuine semantic gap, adjudicated separately. The
  stale User-total cross-reference in the accepted-cost sentence was conformed to the
  round-9 `HostTotalCredit` pin in the same round (points to Host total). (Raised by
  Codex review round 11.)
- **Predicates quantified over the visible project set (round-12 review).** The
  predicates left P's domain open, colliding with the inherited Top-6/chip filtering: a
  hidden project's segment would "render", and a day computable only for a hidden project
  would be classified non-bare while showing no bars and receiving no hover. Ruling: the
  domain declaration is part of the predicate definitions — P ranges over the visible
  set; hidden projects do not exist for this chart; chip toggles recompute (batch-1
  visible-set family); the partial-day disclosure shares the domain. (Raised by Codex
  review round 12.)
- **Span total shown only when exact (round-12 review; controller formula corrected at
  landing).** Staggered observations can make a run bare with no common observed
  endpoints, leaving no exact `HostTotalCredit` derivation for the advertised span — and
  estimating or amortizing is banned. Ruling: the hover shows the span total iff every
  visible project is observed on `start−1` and on `end` (the run's own last day), where
  `spanTotal = Σ_P (total(P, end) − total(P, start−1))` covers exactly the run and
  overlaps no rendered segment; otherwise the total line is omitted (range + day count
  only) — not exact ⇒ not shown, the no-fabrication rule in hover form. Process note,
  recorded fairly: the controller's initial ruling set the right edge at `end+1`, which
  double-counts that day's rendered bar; the landing session caught it against the
  synchronized-recovery example before landing — the mirror image of the round-9
  check-before-adopting lesson. (Raised by Codex review round 12.)
- **Predicate group closed: empty domain, inclusive counts, axis span, observed()
  (round-13 review + proactive free-variable sweep).** Two findings and two proactively
  closed variables. (a) An empty visible set made predicate 2 vacuously true for every
  day and predicate 3's empty sum advertise `0 over the span` — a fabricated value, not
  an observation; ruling: an empty domain evaluates nothing, the page falls into the
  batch-1 nothing-to-chart state. (b) The hover's day count is the inclusive number of
  bare columns; the owner example string changes from `(2 days without data)` to
  `(3 days)` (controller-confirmed, owner-notified): after formalization, "days without
  data" is a false description for a recovery day (observed, no daily value) and for
  staggered runs — under the `No daily values` prefix the inclusive count is the only
  self-consistent reading. (c–d, proactive) N's domain = the visible projects' observed-
  day span `[min, max]` (outside is not a column — timeline bare-canvas family), and
  `observed(P, N)` ⟺ a `daily_statistics` record exists for that day/project/host —
  anchoring the last free variables to the GuiRpc data model. A first-column-always-bare
  corollary is recorded as illustration. (Raised by Codex review round 13.)
- **Gate coverage for the new predicates; dual-track gate stated (round-14 review).** The
  snapshot matrix had not been extended for the round-12/13 semantics: an implementation
  could show an empty-sum total in the zero-visible state, or a span total on a staggered
  run where predicate 3 requires omission, without failing any listed gate. Additions: a
  zero-visible PNG fixture (nothing-to-chart), and an explicit dual-track gate statement —
  chart-surface pixels ride snapshots, while predicate paths that leave no pixel mark
  (exact vs omitted span-total) are machine-gated by unit tests over the extracted
  gap-semantics pure function, both paths with cases. Not a new mechanism: the existing
  decision-logic-as-policy-module canon made explicit in the gate wording (the substrate
  choice stays with the canon, not the contract). The partial-day disclosure wording was
  conformed in the same round — `no daily value that day`: a project with a record on N
  but no N−1 endpoint is observed, its increment merely unavailable, and calling it
  "unobserved" falsely reported a data outage. (Raised by Codex review round 14.)
- **Dual-track enumeration completed (round-15 review).** The round-14 dual-track
  statement listed only predicate 3's span-total paths; the partial-day disclosure
  (the `1 ∧ ¬2` path — tooltip + accessible name) is equally pixel-invisible, so an
  implementation could render the available segments while omitting the disclosure
  without failing any gate. The policy-test track now requires cases for the partial-day
  disclosure alongside both span-total paths — a conforming completion of the ruled
  dual-track principle, not a new mechanism. (Raised by Codex review round 15.)
- **Cold-start window clarified; pre-observation state defined (round-16 review).**
  (a) "Axis starts at the store's first observation" contradicted §3's fixed
  `[end − preset, end]` window whenever history is shorter than the preset; the owner
  text's own "left of it is bare canvas" already implied the reconciling reading, now
  stated: the window is §3's unconditionally, data starts at the first observation, and
  the pre-observation portion is the captioned bare canvas (conforming clarification;
  controller-confirmed wording change to owner text). (b) A configured-but-never-observed
  fleet had no valid window: `end` is observation-anchored (wall clock explicitly
  rejected) and the No-hosts state is reserved for zero configured hosts. Ruling: the
  **pre-observation state** — zero observations render no time axis (no data, no axis;
  the no-wall-clock pin untouched), the lane area carries the §2 unreachable InfoBars and
  a centered `Waiting for the first observation…` caption, and the first observation
  forms the axis per §3. Rejected: a wall-clock anchor special case (collides with the §3
  pin) and reusing No-hosts (collides with the round-2 ruling). A new state, but its
  shape is fully forced by existing pins — the only freedom is the caption presentation,
  covered by the implementation PR's owner eyeball gate. (Raised by Codex review
  round 16.)
- **Empty-state copy quantified over observations; fractional deltas preserved
  (round-17 review).** (a) `No tasks running since {time}` asserted inactivity across
  time Lattice never observed — missing data turned into a negative observation, against
  the no-fabrication rule. Ruling: the Empty predicate and both copy clauses quantify
  explicitly over observations (`No tasks observed running… none observed completing…`),
  with the "last hour" anchored to the latest observation tick, not wall clock — the
  controller tightened the initial proposal, whose second clause still carried the
  wall-clock hole (owner copy change, controller-confirmed). (b) The tooltip pin
  inherited batch-1's exact-integer formatter while `HostTotalCredit` is a `double`:
  a non-integral daily delta would be silently truncated by a tooltip claiming
  exactness. Ruling: daily deltas get their own formatter — group separators, two
  decimals when non-integral, integer otherwise — defined once and shared by tooltip and
  accessible name; the batch-1 integer rule keeps governing the shipped cumulative
  metrics only. (Raised by Codex review round 17.)
- **Delta precision: declared rounding, not exactness (round-18 review).** The round-17
  wording bound "two decimals" to an exactness claim — impossible for a `double` source:
  a positive delta below `0.005` would display as `0.00` (real output asserted as zero)
  and longer fractions were truncated while still called exact. Ruling: the delta
  formatter is a **declared display rounding** — two decimals, round half away from zero
  (pinned against .NET's banker's-rounding `Math.Round` default), integers unchanged —
  and a positive delta rounding to zero displays `< 0.01`, never `0.00` (same family as
  missing-data-is-not-a-negative-observation). A stated rounding rule is not fabrication;
  presenting invented values as observations is. Recorded fairly, two misses again: the
  exactness-precision binding entered via the landing session's round-17 recommendation
  and was adopted unexamined. (Raised by Codex review round 18.)
- **Bare runs get an accessible carrier (round-19 review).** The bare-run range, day
  count and span total were hover-only: with no bar to focus and the no-extra-elements
  mapping, keyboard and screen-reader users had no channel at all — while this contract
  itself holds that hover is not equivalent for assistive technology. Conforming
  application of the round-4 principle: each maximal bare run carries an accessible node
  whose name equals its hover content; the accessibility tree is not a visual element,
  so the zero-extra-elements mapping (now stated as zero extra *visual* elements) is
  untouched. (Raised by Codex review round 19.)
- **Gate track 3: headless accessibility assertions (round-20 review).** The round-19
  bare-run accessible node is UI wiring — a focusable accessibility-tree node — which
  neither snapshots (no pixels) nor the pure-function track (content only) witness: an
  implementation could compute the correct name yet never attach the node, passing every
  listed gate while preserving the exact failure round 19 addressed. Ruling: a third
  gate track — headless accessibility assertions on node presence, name and focusability,
  for both span-total forms — asserting against the built accessibility tree itself
  after a headless settle, never against a ViewModel exposing the right string (the
  classic wiring false-green: correct code, absent end state). Same shape as the
  round-14 dual-track ruling: an existing canon (the headless settle family) made
  explicit, not a new mechanism. (Raised by Codex review round 20.)
- **Ladder units follow the threshold's semantics: DIPs, not device pixels (round-21
  review).** At 2× the fixed 48-device-px minimum is only 24 DIPs while label glyphs
  scale with the DIP transform — a normal duration label no longer fits, contradicting
  every-hole-labelled and no-overflow in the explicitly gated 2× fixtures. The original
  device-pixel pin served the superseded 3px perception floor (perception is a
  physical-pixel question); round 1 replaced the threshold's semantics with glyph fit,
  which is DIP-scaled — so the unit migrates with the semantics: **48/170 DIPs**. The
  owner's underlying intent (rules must not silently weaken at 2×) is preserved, not
  overturned — the device-pixel unit had come to produce exactly the weakening it
  guarded against. 1× behaviour is verbatim unchanged (48 DIPs = 48 device px at 1×);
  2× turns from contradictory to consistent; the 1×/2× fixture pair now verifies
  DIP-threshold consistency across render scales. (Semantics-preserving inversion of an
  owner pin; controller-confirmed, owner-notified with veto reserved.) (Raised by Codex
  review round 21.)
- **Track 3 covers every mandated accessibility carrier (round-22 review).** Track 3 was
  written against the bare-run node alone, while the partial-day column's disclosure name
  and the timeline hole/group accessible names (including the group member enumeration)
  are equally mandatory wiring that neither pixels nor the pure function witness. The
  track now states the rule generically — every accessibility-tree carrier this contract
  mandates rides it — with the current carriers enumerated; future carriers join by rule,
  not by a new listing (the same shape as the artifact-precedence rule: state the class
  once instead of patching per instance). A conforming completion of the round-20 track,
  not a new mechanism. (Raised by Codex review round 22.)
- **Track 3 carrier list: the `Other` band added (round-23 review).** The round-22
  "current carriers" enumeration itself missed the §1-mandated `Other` accessible name
  and member/count enumeration — a list that claims completeness misleads even under a
  generic rule, so the omission is corrected in the listing rather than waved at the
  generic sentence. (Raised by Codex review round 23.)
- **Coverage anchors exclude backfilled completions; track 3 asserts the property §2
  assigns (round-24 review).** (a) "Data starts at the store's first observation" would
  let a backfilled completion predating the first live observation move the coverage
  start — contradicting backfill-never-changes-hole-geometry and making unobserved
  pre-connect time appear covered. Coverage anchors (cold-start data origin, the
  pre-observation state's trigger) now ride **task-state observations** only, conforming
  to the round-1 backfill ruling. (b) Track 3 required the group member enumeration on
  the accessible *name* while §2 assigns it to the accessible *description* — distinct
  automation properties; the assertion now targets each property as §2 assigns it, no
  duplication forced. Both conforming fixes to ruled semantics. (Raised by Codex review
  round 24.)
- **Column domain conformed to the predicates (round-25 review).** The original "one
  column per observed day" wording predated the predicate normalization and permitted
  omitting a day unobserved by every visible project — silently collapsing all-project
  gaps, which the bare-column predicate, the maximal-run hover and implementer note 6
  all forbid. Conforming rewrite: one column per day in the axis span `[min, max]`, the
  predicates' N domain. (Raised by Codex review round 25.)
- **Span totals ride the delta formatter (round-26 review).** `spanTotal` is a sum of
  `HostTotalCredit` deltas and can be fractional, but the delta formatter was scoped to
  daily deltas and the span-total example read as integer-only — a fractional exact
  total would have lost real output through the inherited integer format. Conforming
  extension of the round-18 ruling: the single delta formatter covers every delta-valued
  display, span totals included (with the `< 0.01` rule), and track 2 gains a fractional
  exact-total case. (Raised by Codex review round 26.)
- **Compact-merge anchor pinned; duration intervals made exclusive (round-27 review).**
  (a) The compact merge fixed the 48-DIP width but never placed the interval — three
  different anchors all satisfied the stated width while swallowing different observed
  pixels, leaving the gated fixture without unique geometry. Pinned: centre-anchored on
  the group's true bounding span, the same rule as an isolated hole applied to the
  group's true interval. (b) The duration format's rungs overlapped (`30 s` satisfied
  both `< 0.1 h` and `< 1 m`, so ordered evaluation never reached seconds); the middle
  interval is now `1 m ≤ duration < 0.1 h` with boundaries landing in the larger unit.
  Both formal completions of ruled rules. (Raised by Codex review round 27.)
- **Duration rounding at unit edges pinned and gated (round-28 review).** "Whole
  seconds/minutes" left `59.6 s` ambiguous between `59 s` and `60 s`. Pinned: whole
  values round half away from zero (the delta formatter's declared rule), and a rounded
  value reaching the next unit's boundary is represented in the larger unit (`59.6 s` →
  `1 m`; `5 m 58 s` → `6 m` = `0.1 h` → `0.1 h`) — consistent with boundaries-land-in-
  the-larger-unit. The gates gain the missing cases: the policy track covers sub-minute,
  exact-boundary and rounds-up-to-boundary values, and the ladder fixture includes a
  sub-minute-duration hole so the rendered label is pixel-gated — the formerly
  overlapping implementation can no longer pass every named gate. (Raised by Codex
  review round 28.)
- **Rounding rule closed over all outputs; tie case gated (round-29 review).** The
  round-28 rule governed only whole minutes/seconds, leaving the one-decimal hours
  branch ambiguous at midpoints (`8.25 h`); it now covers every numeric output of the
  formatter. And none of the existing cases could distinguish half-away-from-zero from
  .NET's default banker's rounding — an implementation on default `Math.Round` passed
  every named gate while formatting `58.5 s` as `58 s`; the policy track now requires an
  exact-halfway tie whose lower value is even (`58.5 s` → `59 s`). (Raised by Codex
  review round 29.)
- **Tie coverage completed: delta midpoint case; rendered tie in the ladder (round-30
  review).** The round-29 tie case exercised only the duration formatter — the delta
  formatter had no midpoint case (`1.125` → `1.12` under default rounding passed every
  gate), and the ladder's rendered label was the non-tie `30 s`, so a chart label path
  bypassing the shared helper also passed. The policy track gains a delta midpoint case
  (`1.125` → `1.13`), and the ladder fixture's sub-minute hole now renders the tie
  itself (`58.5 s` → `59 s`) so the end state is pixel-gated. (Raised by Codex review
  round 30.)

## Files

> **Artifact precedence rule.** Every reference render under `img/` and the interactive
> spec `M4-Batch2-Spec.html` predate the review rulings recorded in the decision log
> (minimum hole width + universal labels, adaptive duration format, stacked-step/left-hold
> geometry, backfill and lane semantics). Wherever an artifact disagrees with the contract
> text — sloped band edges, unlabelled or sub-48px holes, topmost-lane-only labels,
> `0.0 h`-style durations, `StackedAreaSeries` wording — the contract text and the
> snapshot matrix are authoritative and the artifact is obsolete on that point. Artifacts
> are illustration, not gate: snapshot fixtures are pinned by the contract text and cannot
> encode superseded behavior.

- `batch2-design-contract.md` — this contract (the handoff `README.md`, file renamed on
  landing; landing edits: this Files section, the evidence-base pointer, and the review
  rulings recorded in the decision log).
- `M4-Batch2-Spec.html` — offline interactive spec (full hi-fi board, pannable).
- `hole-rendering-research.md` — evidence base for the hole-rendering decisions.
- `img/timeline-light.png`, `img/timeline-dark.png` — Timeline full page, both themes.
- `img/timeline-dense-10hosts.png` — 10-host × 7 d density state.
- `img/timeline-cold-start.png`, `img/timeline-empty-states.png` — states.
- `img/degradation-ladder.png` — label-ladder reference (device-px rungs).
- `img/settings-retention.png` — retention Settings row.
- `img/daily-output-light.png`, `img/daily-output-dark.png` — Statistics fifth metric with
  the 3-day gap rendering.

_Source of truth in the design project: `M4 Batch2 Hi-fi.dc.html` (hi-fi + contract board),
`M4 Batch2 Wireframes.dc.html` (t6–t9 + decision ledger; t1–t5 archived),
`hole-rendering-research.md` (evidence base)._
