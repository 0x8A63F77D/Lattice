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
`get_old_results` backfill at connect. Never render fabricated data: unobserved time is a
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

**Label ladder** — treatment = f(rendered hole width in **device px**), a pure function
(no wall clock, no DPI-dependent branches beyond the device-px input):

- `< 3` → exaggerate to 3 (detection floor; an invisible hole silently fabricates continuity)
- `≥ 48` → duration label, one decimal: `8.2 h`
- `≥ 170` → reason + duration: `Lattice not running · 8.2 h` / `Host unreachable · 2.5 h`

Label only on the **topmost lane** of an aligned column; other lanes rely on hover.

**Reason vocabulary — exactly two user-facing strings, no others:**
`Lattice not running` (the app was off — routine) and `Host unreachable` (Lattice was
watching but could not reach the host — exceptional; same phrase as the batch-1 InfoBar).

**Baseline = per-host coverage.** The 1px lane baseline is drawn **only over observed
spans** — line present + band at zero = observed idle; line absent = not watching. This is
the load-bearing three-way distinction: colour band = running · baseline at zero = idle ·
grey fill = no data.

**Accessibility.** Every hole region carries an accessible name:
`Lattice not running · 23:30 → 07:41 (8.2 h)`. WCAG 1.4.11 conformance rides the text
channel (labelled-graphic exemption) + tooltip/accessible name — see decision log.

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
  Statistics) + **one** non-interactive entry `No data` (swatch = hole fill with a chrome
  border `#D1D1D1` / `#525252`; the border is a chrome affordance, not chart grammar).
- Status strip: `N hosts · N intervals in view · N days retained` left,
  `Polling every 5s` right.
- Page reads the shell host scope; `All hosts` renders every connected host as a lane.

### 5. States (issue #88 idiom)

- **Cold start / first run:** axis starts at store's first observation; left of it is bare
  canvas with caption `no history before {time}`; dashed accent marker at connect time;
  status strip: `history starts 14:32 (1 h backfilled at connect)`. Backfilled intervals get
  no special visual; tooltip marks `backfilled`.
- **No hosts:** centered — `ServerMultiple` icon 28px `#C7C7C7`, `No hosts connected`,
  caption `Add a host to start observing task activity.`
- **Empty:** `No task activity yet` + `No tasks running since {time}, and none completed in
  the last hour.`
- **Loading:** batch-1 ProgressRing idiom, `Loading timeline…`

### 6. Tooltip (interactive lane; formats binding)

- Hole hover: reason · range · duration — `Lattice not running · 23:30 → 07:41 · 8.2 h`.
- Band hover: per-project concurrent counts at the hovered instant; task-level detail lists
  task name, project, host, elapsed, progress (live) or final runtime (historical).
- Durations one decimal (`8.2 h`); dates/times `CultureInfo.CurrentCulture`; harness pins
  `en-US`.

---

## Surface B — Daily output (Statistics extension)

- **Fifth `SegmentedControl` item** `Daily output`, after `Host average`. Batch-1 layout
  ruling (one chart, metric switcher) untouched; chips / Top-6 / `+N more` flyout / colour
  allocation / animation all inherited.
- `StackedColumnSeries`, one column per observed day; segment colour = project. Y axis
  compact labeler and axis styles inherited from batch-1 §2.
- **Increment definition [machine-gated]:** day N's value = `total(N) − total(N−1)`, valid
  only when **both endpoints are observed days**.
- **Gap rule [machine-gated]:** a run of unobserved days *and the first observed day after
  it* render **empty — no bars at all** (the bar analogue of the #170 "an average that was
  not observed is not an average — break it" ruling; a missing bar is naturally visible in a
  bar chart). The span total is never drawn, estimated, or amortized; it surfaces only on
  hover over the gap: `No daily values · 07-15 → 07-17 (2 days without data) · 104,500 over
  the span`. **Accepted cost:** this chart is not area-conserving — "how much in total" is
  answered by the User total metric, this chart answers "how much on which day", and days it
  cannot answer are empty.
- Grouped bars **rejected** (6 visible × 90 days = 540 sub-detection-floor slivers; loses
  the per-day total; per-project comparison is what chip filtering is for).
- Tooltip: exact integers + group separators (batch-1 §6 rules).

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

1. Density band = `StackedAreaSeries` (or stacked step) with **nullable points** — holes
   break the geometry naturally. Never drop missing rows (silent join); materialize nulls.
2. Lanes = one `CartesianChart` per host, X axes synchronized via shared Min/MaxLimit
   (shared pan/zoom).
3. Hole = `RectangularSection` — `Fill` is the grey, `Label` is the duration/reason text.
   Live edge = zero-width section (`Xi == Xj`) stroke. No custom Skia layer exists anywhere
   on the page.
4. Daily output gaps = missing/nullable values (no bar) — zero extra elements; the gap-hover
   span total goes through tooltip logic.
5. LiveCharts2 paints are not DynamicResource-aware: rebuild hole/axis/baseline paints on
   theme switch (batch-1 warning #1).
6. Ordinal/collapsed axis modes silently delete holes — the X axis must stay linear time.
7. The label ladder runs on **device pixels**; compute from the rendered scale, not DIPs,
   or the rules silently weaken at 2× DPI.

## Snapshot matrix (machine gate)

Timeline: 2 themes × {24 h baseline (overnight hole + historical unreachable + idle span),
7 d dense (10 hosts, 41px holes — no-label rung), cold start, no-hosts, empty} + a synthetic
**ladder fixture** (one hole per width rung) rendered at 1× and 2× DPI.
Daily output: 2 themes × {12-day baseline containing a 3-day gap}.
Culture pinned `en-US`; every fixture pins `end` and the dataset. ≈ 18 snapshots.

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

## Files

- `batch2-design-contract.md` — this contract (the handoff `README.md`, file renamed on
  landing; content unchanged apart from this Files section and the evidence-base pointer).
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
