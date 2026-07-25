# Lattice — M4 Statistics charts — design contract

> Copied verbatim from the owner-approved design handoff (`Lattice-design-m4-handoff/README.md`)
> so the contract lives in-repo and is authoritative over any plan wording (docs/design/m2
> precedent). Reference renders under `img/`. The two large interactive `.html` demos stay out
> of git. Implementation deviations (no refresh button, font, metric-switcher control, nav
> icon) are recorded in the landing PR, not edited into this contract.

First batch of the Statistics feature (issue #148 ruling): BOINC Manager Statistics-tab parity
— per-project credit history, four metrics (user total / user average / host total / host
average), one data point per project per day, LiveCharts2 (`LiveChartsCore.SkiaSharpView.Avalonia`)
on a native FluentAvalonia page. This README **is** the contract. Everything in §2 (chart
content) is machine-gated pixel-exactly via headless PNG snapshots; §"interactive lane" items
ride one-time owner eyeball and must stay default-ish. If an instruction conflicts with a
[HARD] pin from #148, the pin wins.

Reference renders in `img/` (light + dark). Interactive spec: `M4 Statistics Spec.html`
(offline, pannable). Motion/hover feel: `M4 Motion Demo.html` (interactive — metric switching,
duration/easing A-B, tooltip reference). Exploration history stays in the design project
(`M4 Statistics Wireframes.dc.html`).

---

## 1. Layout ruling (§5 of the brief)

**Manager-style: one chart, metric switcher, all projects overlaid.** A FluentAvalonia
SegmentedControl selects ONE of the four metrics; every visible project renders as one colored
line series on a single cartesian chart.

Rationale: at today's 9–10-point histories a single large plot is the only layout where daily
steps stay readable; showing one metric at a time dissolves the millions-vs-thousands axis
conflict (no dual axis, no log scale); it is exact Manager parity; and it minimizes the
snapshot matrix (4 metrics × 2 themes). Small multiples and dual-Y were explored and rejected
(see wireframes, turn 1).

## 2. Chart content spec [machine-gated]

### Series
- Cartesian `LineSeries`, **stroke 2px solid**, no fill (`Fill = null`), `LineSmoothness = 0`
  (straight segments — never invent curvature between daily points).
- **Point markers:** visible point count ≤ 30 → circle geometry, `GeometrySize = 8`, solid
  series color, no geometry stroke; count > 30 → `GeometrySize = 0` (pure line).
- A project with **1 point renders marker only; 2+ points renders the line.**
- **Gaps are real:** days missing from the daemon history render as line breaks (nullable
  points), never interpolated. No data cap — render whatever depth the daemon returns.

### Palette (Fluent UI charting `DataVizPalette`, qualitative.1–6 — same hex both themes)
| # | Name | Hex |
|---|---|---|
| 1 | Cornflower | `#637CEF` |
| 2 | Hot pink | `#E3008C` |
| 3 | Teal | `#2AA0A4` |
| 4 | Orchid | `#9373C0` |
| 5 | Light green | `#13A10E` |
| 6 | Light blue | `#3A96DD` |

- Color assignment: **project ordinal in the daemon's project list**, independent of
  visibility — toggling legend chips never recolors a series.
- **Hard cap: ≤ 6 series visible simultaneously** (see §4 overflow rule), so colors never
  repeat. If a later batch raises the cap, continue with official qualitative.7–10
  (`#CA5010` `#57811B` `#B146C2` `#AE8C00`) — never invent colors.

### Axes
- Chart background **transparent** (page canvas shows through).
- **Y axis:** gridlines 1px solid — light `#E8E8E8`, dark `#383838`; auto-fit min/max to the
  currently *visible* series, always including the 0 baseline. Labels: Segoe UI 12px, light
  `#616161` / dark `#ADADAD`, tabular figures. Compact labeler: ≥1M → `#.#M`, ≥1k → `#.#k`,
  else integer. Credit is dimensionless — no unit suffix. `k`/`M` are fixed literals, not
  localized.
- **X axis:** no gridlines. Date labels same type style; ~9 points → every 2nd day,
  ~90 points → weekly; longer histories follow the library's automatic label spacing (left
  to default).
- Number separators / decimal point / date patterns follow **`CultureInfo.CurrentCulture`**
  — the UI never hardcodes a culture. Snapshot determinism is the harness's job: the
  snapshot runner pins `en-US` before rendering.

## 3. [HARD] Animation

```
AnimationsSpeed  = TimeSpan.FromMilliseconds(200)
EasingFunction   = EasingFunctions.BuildCubicBezier(0f, 0f, 0f, 1f)
```

Both are Fluent 2 motion token values verbatim: 200ms = `--durationNormal`; the bezier is
exactly `--curveDecelerateMid` (data changes are "enter" motion → decelerate). The library
default (slow + elastic overshoot) violates Fluent's no-bounce rule — overriding it is the
point. Applies to metric switches, series toggles, and polling updates. No looping/pulsing
animation anywhere on the page.

## 4. Chrome spec (all native FluentAvalonia — no hand-built controls)

- **NavigationView item:** `Statistics`, Fluent System Icon `data_trending` (regular at rest,
  filled when selected), placed **after Transfers, before Event log**.
- **Metric switcher:** `SegmentedControl` in the 52px command bar, after the page title.
  Items, exact order and wording (Manager parity): `User total` · `User average` ·
  `Host total` · `Host average`. Default selection: User total.
- **Legend = chrome, not chart:** one row of `ToggleButton` chips under the command bar —
  12px rounded-3px color swatch + project name; unchecked = grey swatch, struck-through
  secondary text. Chart-internal legend stays off.
- **Overflow (> 6 projects):** default-visible set = top 6 by current RAC; the rest live in a
  `DropDownButton` labeled `+N more` with a checkbox `MenuFlyout` (name + current RAC value
  slot). Checking adds the project's series; **when 6 are checked the remaining checkboxes
  disable** (caption: "6 of 6 series shown. Uncheck one to add another.").
- **Host scope:** the page reads the shell's host selection (rail). When the shell scope is
  "All hosts", the command bar's right side shows a host `ComboBox` (defaults to the first
  connected host). Cross-host overlay is out of scope ([HARD], #148).
- Command bar right side: `Updated Ns ago` caption + refresh `IconButton`
  (`arrow_clockwise`), same idiom as the Tasks view.
- Status strip (bottom, existing idiom): `N projects · N days of history` left,
  `Polling every 5s` right.

## 5. Empty / degraded states (issue #88 idiom — centered secondary text)

- **First fetch pending:** centered `ProgressRing` + `Loading statistics…` (13px, secondary).
- **No history at all:** centered — `data_trending` icon 28px `#C7C7C7`, `No statistics yet`
  (14px semibold), caption `BOINC records one point per project per day. Check back
  tomorrow.`
- **Host unreachable (data stale):** chart keeps rendering the last data; an `InfoBar`
  (severity=Warning, not dismissable) sits above it: `Host unreachable. Showing statistics
  from {yyyy-MM-dd HH:mm}.` + `Retry` hyperlink-button wired to the existing reconnect
  command.
- **Project with < 2 points:** no special state — marker-only rule from §2 covers it.

## 6. Interactive lane (owner eyeball — NOT snapshot-gated) + explicit defaults

- **Tooltip:** nearest-X strategy (`FindingStrategy.CompareOnlyX`-equivalent): hovering
  anywhere snaps to the nearest day column and lists ALL visible series. Recommended visuals
  (see Motion Demo): dashed vertical guide, hovered points enlarge +2px with white stroke,
  Fluent hover card (white/dark surface, 4px radius, shadow8) — but library-default tooltip
  visuals are acceptable; do not over-engineer.
- **Tooltip number format (binding even though not snapshot-gated):** exact values, never
  compact — Total metrics: integer + group separators; Average (RAC) metrics: fixed 2
  decimals + group separators; date header `yyyy-MM-dd` (culture-formatted). Axis compact
  vs tooltip exact — never mix the two.
- **Left to implementation defaults:** tooltip surface/typography details, hover transition
  timing, X-label auto-spacing at >90 points.
- **Off in first batch:** zoom/pan (`ZoomMode.None`), crosshair section, log axis, Growth%
  relative view, time-range selector, cross-host overlay, task timeline/throughput/
  notifications/host groups (#148 deferrals).

## ⚠ Implementer warnings

1. **LiveCharts2 paints are not DynamicResource-aware.** On live theme switch, rebuild the
   axis/separator `SolidColorPaint`s with the §2 hex values and reassign; do not expect
   brushes to follow the theme automatically.
2. Set `Fill = null` explicitly on every `LineSeries` — the default is a translucent area
   fill.
3. Gridlines: set `SeparatorsPaint` on the **Y axis only**; X axis `SeparatorsPaint = null`.
4. Gaps require **nullable values** (`double?` / null `ObservablePoint.Y`) — filtering the
   missing days out would silently join the line across the gap.
5. The marker rule (≤30 → 8, else 0) is evaluated on the **visible point count of the
   longest visible series**, re-evaluated when data depth or filters change.
6. RAC values from the daemon are doubles — do not round them before charting; rounding is
   a display concern (§6 tooltip / §2 axis labeler).
7. Legend chips and the +N-more flyout are chrome state, not chart state: rebuilding series
   on toggle is fine, but keep color-by-ordinal stable (§2) so no visual reshuffle occurs.

## Snapshot matrix (machine gate)

4 metrics × 2 themes × baseline data (9 points, 3 projects) = 8 content snapshots, plus:
Top-6 overflow state (light), >30-point pure-line state (light), the three §5 states.
Culture pinned `en-US` in the harness.

## Files
- `README.md` — this contract.
- `M4 Statistics Spec.html` — interactive spec (offline, pannable canvas).
- `M4 Motion Demo.html` — interactive motion/hover reference (offline).
- `img/stats-light.png`, `img/stats-dark.png` — full page, both themes.
- `img/top6-overflow.png` — 12-project host, Top-6 + "+6 more" flyout.
- `img/density-90d.png` — >30-point pure-line density.
- `img/states.png` — loading / empty / stale InfoBar (+ <2-point rule).
- `img/palette.png` — qualitative.1–6 swatch sheet on both surfaces.

_Source of truth in the design project: `M4 Statistics Hi-fi.dc.html` (hi-fi + contract
board), `M4 Motion Demo.dc.html` (motion/hover), `M4 Statistics Wireframes.dc.html`
(exploration, turns 1–3)._
