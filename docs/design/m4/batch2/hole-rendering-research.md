# Rendering "unobserved time" in timeline/chart UIs — design-system precedents

Research report for Lattice issue #202 (M4 charts batch 2, task timeline page).
Spec: `hole-rendering-research-prompt.md` (owner-provided). Status: **COMPLETE** (2026-08-02). Research executed by four parallel Opus subagents (one per question group), synthesis by the controlling agent.

Candidate treatment ids referenced throughout (from the spec):

- **4a** hatched/striped band over the hole (warning-flavored)
- **4b** inverse: light background tint on *observed* time; holes = bare canvas
- **4c** collapse long holes into a fixed-width axis-break glyph (non-linear axis)
- **5a** pure blank + per-host baseline breaks across the hole (information removal)
- **5b** dedicated thin "coverage track" under the axis; plot stays clean
- **5c** solid muted grey band (no pattern), duration label when wide enough — current front-runner
- **5d** blank hole + dashed edge hairlines + "⌇ 8.2h" duration annotation
- Unreachable-host holes additionally get a red dashed frame in all variants.

Evidence classes: **[official guidance]** / **[product behavior]** / **[anecdote]**. "Not found" and "unverifiable" are recorded as findings.

---

## 1. Fluent / Microsoft ecosystem

### 1.1 Does Fluent UI charting publish a rule for null / missing / no-data segments?

**Yes — one rule, and it is "break the line, optionally dash it". No hatching, no filled placeholder.**

**A. The only written guidance** lives in the FluentUI Charting Contrib docsite (Microsoft-owned org `microsoft/fluentui-charting-contrib`), not on fluent2.microsoft.design. Verbatim, from `docs/Charting-Concepts/LineChart.md`:

> "A line chart can have gaps/breaks in between. This is to represent missing data. The gaps can also be replaced with dashed or dotted lines for specific scenarios, say to represent low confidence predictions for a time series forecast graph."

and the implementation rule:

> "Gaps can be added by using gaps prop. A gap is denoted by startIndex and endIndex datapoints in the line. A line will be drawn uptil the startIndex and skipped for endIndex - startIndex number of datapoints. A line can have as many gaps as possible."

Link: https://microsoft.github.io/fluentui-charting-contrib/docs/Charting-Concepts/LineChart — **[official guidance]** (project-level, not design-system-level). → supports **5a / 5d**; no Fluent precedent anywhere for **4a** (hatching).

**B. Source confirmation of what a gap actually renders as.** `packages/charts/react-charting/src/components/LineChart/LineChart.base.tsx` — the gap is implemented purely as *suppression of the `<line>` element*, with the comment `// don't draw line if it is in a gap`, guarded by `if (!isInGap)`. Nothing is drawn in its place — no fill, no marker, no annotation. The public type is minimal:

```ts
export interface ILineChartGap {
  /** Starting index of the gap. */
  startIndex: number;
  /** Ending index of the gap. */
  endIndex: number;
}
```
(`packages/charts/react-charting/src/types/IDataPoint.ts`; the series prop is documented as `gaps?: ILineChartGap[]` — "gaps in the line chart where a line is not drawn".)
Link: https://github.com/microsoft/fluentui/blob/master/packages/charts/react-charting/src/components/LineChart/LineChart.base.tsx — **[product behavior, source-verified]**

The same logic is carried forward verbatim into the v9 package `@fluentui/react-charts` (`packages/charts/react-charts/library/src/components/LineChart/LineChart.tsx`, `_checkInGap`), so this is the current, not legacy, Fluent answer.

**C. Fluent's own canonical sample for "data we are less sure about" is a second dashed series plus a footnote — not a fill.** `packages/charts/react-charts/stories/src/LineChart/LineChartGaps.stories.tsx` builds exactly the shape Lattice needs: a "Normal Data" series carrying `gaps: [{startIndex,endIndex}…]`, and a *second overlay series* named `'Low Confidence Data*'` with `legendShape: 'dottedLine'`, `lineOptions: { strokeDasharray: '2', strokeDashoffset: '-1', strokeLinecap: 'butt', lineBorderWidth: '4' }`, spanning precisely the gap intervals — plus a callout description function returning:

> `'* This data was below our confidence threshold.'`

Link: https://github.com/microsoft/fluentui/blob/master/packages/charts/react-charts/stories/src/LineChart/LineChartGaps.stories.tsx — **[official guidance]** (first-party reference sample). → strongly supports **5d** (blank + dashed treatment + explicit textual annotation), and supplies a legend precedent: the degraded state gets **its own legend entry with a distinct shape**, not just an in-canvas mark.

**D. Critical limitation for Lattice's chart type: Fluent's AreaChart has NO gap support at all.** `grep -i gap` over `AreaChart.base.tsx` (v8) and `AreaChart.tsx` (v9) returns only `gapSpace: 15` (a callout-positioning value). `AreaChart.md` in the contrib docs has no Gaps section. So for a **stacked area / density band** — Lattice's actual form — the Fluent ecosystem has no shipped precedent; the line-chart rule is the nearest analogue and must be adapted. **[product behavior, source-verified]** — a genuine hole in the precedent.

**E. Fluent's "no data at all" state is *not* a visual — it is an invisible ARIA alert.** Both AreaChart and LineChart render, when `_isChartEmpty()`:

```tsx
<div id={this._emptyChartId} role={'alert'} style={{ opacity: '0' }} aria-label={'Graph has no data to display'} />
```
**[product behavior, source-verified]**. Two takeaways: (i) Fluent draws *nothing* for absent data, and (ii) it still guarantees a screen-reader-reachable statement of absence. → for Lattice, any blank-based treatment (**5a/5d**) must carry an equivalent accessible name on the hole region, or the hole is literally undetectable non-visually.

**F. fluent2.microsoft.design has no data-visualization page.** A domain-scoped search returned only Shapes, Layout, Color, Iconography, Material, Design principles, component overviews. **Not found / unverifiable** that Fluent 2 the design system publishes any missing-data charting rule. The only transferable line is its color-accessibility rule — do not use color as the only carrier of meaning; pair it with text and other indicators (https://fluent2.microsoft.design/color) — **[official guidance]**, which argues against a fill-color-only hole (pure **5c**) and for a fill **+ label** (5c as specified) or 5d.

**G. Adjacent and much stronger official Microsoft guidance: Azure Monitor metric charts.** The most explicit statement anywhere in the Microsoft ecosystem of the exact failure mode Lattice is trying to prevent.

From *Azure Monitor metrics aggregation and display explained*:
> "When the system expects metric data from a resource but doesn't receive it, it records a NULL value. NULL is different than a zero value, which becomes important in the calculation of aggregations and charting. NULL values aren't counted as valid measurements."
> "NULLs show up differently on different charts. Scatter plots skip showing a dot on the chart. Bar charts skip showing the bar. On line charts, NULL can show up as dotted or dashed lines…"

Link: https://learn.microsoft.com/en-us/azure/azure-monitor/metrics/metrics-aggregation-explained — **[official guidance]**

From *Troubleshooting Azure Monitor metric charts*, section "Chart shows dashed line":
> "Azure metrics charts use dashed line style to indicate that there's a missing value (also known as 'null value') between two known time grain data points."
> "The dashed line drops down to zero when the metric uses count and sum aggregation. For the avg, min or max aggregations, the dashed line connects two nearest known data points. Also, when the data is missing on the rightmost or leftmost side of the chart, the dashed line expands to the direction of the missing data point."
> "Solution: This behavior is by design. It's useful for identifying missing data points."

Link: https://learn.microsoft.com/en-us/azure/azure-monitor/metrics/metrics-troubleshoot — **[official guidance]**

Two consequences for Lattice. (1) Microsoft's telemetry product treats the null/zero distinction as first-class and encodes it as **dash vs solid** — directly **5d**. (2) The documented Azure behavior *for sum/count aggregation* is "the dashed line **drops down to zero**" — which for a stacked concurrency density band (a sum) is precisely Lattice's stated failure mode: a hole visually indistinguishable from idle. Azure ships that behavior and users complain about it. → an explicit argument for **5c**'s positive mark over a bare drop-to-zero, and against any variant where the hole silhouette equals the idle silhouette.

### 1.2 Task Manager / Dev Home / perfmon — "not monitoring" vs "value was zero"

**Windows Task Manager — [official guidance / product behavior], documented, no backfill, no fabricated zeros.** Microsoft's AskPerf engineering blog, on the Performance tab:

> "CPU Usage History - Indicates how busy the processor has been. The graph only shows values since the time the Task Manager was opened."
> "Physical Memory Usage History - Indicates how much physical memory is being utilized. It also shows values since Task Manager was opened."

Link: https://learn.microsoft.com/en-us/archive/blogs/supportingwindows/finally-a-windows-task-manager-performance-tab-blog — **[official guidance]** (Microsoft-authored, archived).

So Task Manager's own answer to "the app wasn't running" is: **the plot area exists and is framed, but is empty on the unobserved side; history is never reconstructed.** No documented evidence found of Task Manager marking mid-stream unobserved intervals — its model is a rolling live buffer, so mid-stream holes cannot arise. Claims about *how* the empty region looks (right-aligned fill-in) are corroborated by the Dev Home source below rather than by any verifiable Task Manager screenshot; treat "Task Manager per-CPU graph draws a hatch/label" as **not found — no evidence, do not assert**.

**Dev Home — [product behavior, source-verified], and the closest first-party analogue to Lattice's chart.** `extensions/CoreWidgetProvider/Helpers/ChartHelper.cs` generates the CPU/GPU/Memory/Network widget charts as SVG: an area-filled polyline under a gradient, inside an always-drawn frame. The unobserved-time behavior is explicit in the source comments:

```csharp
// When the chart doesn't have all points yet, move the chart over to the right by increasing the starting X coordinate.
// For a chart with only 1 point, the svg will not render a polyline.
startX = 2 + ((MaxChartValues - chartValues.Count) * pxBetweenPoints);
```

with `private const int MaxChartValues = 34;` and `AddNextChartValue` maintaining a 34-sample rolling window. The frame is drawn unconditionally at full width: `LightGrayBoxStyle = "fill:none;stroke:lightgrey;stroke-width:1"`.

Link: https://github.com/microsoft/devhome/blob/main/extensions/CoreWidgetProvider/Helpers/ChartHelper.cs

Reading: Dev Home **right-aligns the observed series and leaves the unobserved left portion as bare canvas inside a drawn frame** — no zero-padding, no interpolation, no mark on the empty region beyond the always-visible frame that makes the missing span legible. → direct first-party support for **5a** and for the "frame/hairline delimits the hole" half of **5d**; no first-party support for **4a/5c**'s filled band. Dev Home also has no mid-stream gap concept (rolling buffer only), and its error path is a whole-widget error state (`{ "errorMessage", e.Message }` in `SystemCPUUsageWidget.cs`), not an in-chart mark — weak analogue for the unreachable-host case, which suggests the exceptional state may belong partly *outside* the canvas (InfoBar) rather than solely as a red dashed frame.

**Performance Monitor / System Monitor — [official guidance], gaps are literal blanks and are accompanied by an explanatory message.** From the Windows Server 2003 *Troubleshooting Performance monitoring* page, the entry "System Monitor shows gaps in its line graphs.":

> "Cause: This could be because data collection was subordinated to a processing activity with a higher priority on a system with a heavy load. When the system has adequate resources to continue with data collection, the graphing will resume as usual. **A message appears describing this.** Solution: Reduce the performance overhead of system monitoring."

Link: https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-server-2003/cc779303(v=ws.10) — **[official guidance]**

Two extractable rules: perfmon renders a collection outage as an actual **gap in the line, not a zero**; and it **pairs the visual gap with a textual explanation** rather than relying on the gap to speak for itself. The current-era troubleshooting article (https://learn.microsoft.com/en-us/troubleshoot/windows-server/support-tools/troubleshoot-issues-performance-monitor) shows the same gap symptom under CPU saturation. → supports **5d** and, more broadly, the principle that a hole needs an accompanying *words* channel — which is exactly what 5c's duration label and 5d's duration annotation provide.

### 1.3 Fluent 2 neutral token for an "unavailable / no data" region fill

All values below curl'd from `microsoft/fluentui@master` and quoted verbatim from the generated token files (`packages/tokens/src/alias/lightColor.ts`, `darkColor.ts`, `global/colors.ts`) — **[official guidance]**, these are the shipped token values.

| Token | Light | Dark | Semantics as used in-product |
|---|---|---|---|
| `colorNeutralBackground1` (canvas) | `#ffffff` | `#292929` | base surface |
| `colorNeutralBackground2` | `#fafafa` | `#1f1f1f` | |
| `colorNeutralBackground3` | `#f5f5f5` | `#141414` | |
| `colorNeutralBackground4` | `#f0f0f0` | `#0a0a0a` | |
| `colorNeutralBackground5` | `#ebebeb` | `#000000` | |
| `colorNeutralBackground6` | `#e6e6e6` | `#333333` | |
| **`colorNeutralBackgroundDisabled`** | **`#f0f0f0`** | **`#141414`** | disabled control surfaces |
| `colorNeutralBackgroundDisabled2` | `#ffffff` | `#292929` | |
| `colorNeutralCardBackgroundDisabled` | `#f0f0f0` | `#141414` | disabled card |
| `colorNeutralStencil1` | `#e6e6e6` | `#575757` | **skeleton/loading placeholder** |
| `colorNeutralStencil2` | `#fafafa` | `#333333` | skeleton shimmer highlight |
| `colorNeutralStencil1Alpha` | `rgba(0,0,0,0.1)` | `rgba(255,255,255,0.1)` | |
| `colorNeutralStencil2Alpha` | `rgba(0,0,0,0.05)` | `rgba(255,255,255,0.05)` | |
| `colorNeutralStroke1` | `#d1d1d1` | `#666666` | |
| `colorNeutralStroke2` | `#e0e0e0` | `#525252` | |
| `colorNeutralStrokeDisabled` | `#e0e0e0` | `#5c5c5c` | disabled outline |
| `colorNeutralForeground3` | `#616161` | `#adadad` | secondary label |
| `colorNeutralForeground4` | `#707070` | `#999999` | tertiary label |
| `colorNeutralForegroundDisabled` | `#bdbdbd` | `#5c5c5c` | disabled text |

**Which token is idiomatic — and a warning.** `colorNeutralStencil1/2` are **wrong here despite the tempting name**: source-verified, they are the *Skeleton* component's fill, used with an animated shimmer gradient — `backgroundColor: tokens.colorNeutralStencil1` and `backgroundImage: linear-gradient(… ${tokens.colorNeutralStencil1} 0%, ${tokens.colorNeutralStencil2} 50%, ${tokens.colorNeutralStencil1} 100%)` in `useSkeletonItemStyles.styles.ts` (https://github.com/microsoft/fluentui/blob/master/packages/react-components/react-skeleton/library/src/components/SkeletonItem/useSkeletonItemStyles.styles.ts) — **[product behavior, source-verified]**. Stencil semantically means *"content is coming"*. Lattice's holes are **permanent and unbackfillable**; borrowing the loading token would assert the opposite of the truth.

The semantically correct family is the **disabled** one: `colorNeutralBackgroundDisabled` (`#f0f0f0` light / `#141414` dark) — "this region exists but is not operative", which is exactly the hole's meaning. → this is the token for **5c**'s band and for **4b**'s inverse.

**Contrast caveat that materially affects 5c.** Computed WCAG relative-luminance ratios against the plausible chart canvas: `colorNeutralBackgroundDisabled #f0f0f0` on `#ffffff` ≈ **1.14:1**; `#2E2E2E` on `#202020` ≈ **1.20:1**. Every neutral *background*-family token lands in the 1.1–1.2:1 range — below any perceptibility threshold, i.e. **a fill-only grey band is effectively invisible in at least one theme.** The only neutrals in either palette that clear 3:1 against the canvas in **both** themes are the WinUI solid neutrals below (~3.45:1 light, ~6.0:1 dark). → **5c must carry a stroke and/or the duration label; a bare fill will not read.**

**FluentAvalonia equivalents** — what Lattice actually has at runtime. FluentAvalonia ships the WinUI (Windows Fluent) color set, not the Fluent 2 web tokens; keys from `src/FluentAvalonia/Styling/StylesV2/Fluentv2Colors.axaml` (https://github.com/amwx/FluentAvalonia/blob/master/src/FluentAvalonia/Styling/StylesV2/Fluentv2Colors.axaml) — **[product behavior, source-verified]**:

| Key | Light | Dark |
|---|---|---|
| **`SystemFillColorSolidNeutral`** | **`#8A8A8A`** | **`#9D9D9D`** |
| `SystemFillColorNeutral` | `#72000000` (45% black) | `#8BFFFFFF` (55% white) |
| `SystemFillColorSolidNeutralBackground` | `#F3F3F3` | `#2E2E2E` |
| `SystemFillColorNeutralBackground` | `#06000000` | `#08FFFFFF` |
| `ControlFillColorDisabled` | `#4DF9F9F9` | `#0BFFFFFF` |
| `ControlStrongFillColorDisabled` | `#51000000` | `#3FFFFFFF` |
| `ControlStrongStrokeColorDisabled` | `#37000000` | `#28FFFFFF` |
| `TextFillColorDisabled` | `#5C000000` | `#5DFFFFFF` |
| `SolidBackgroundFillColorBase` | `#F3F3F3` | `#202020` |
| `SolidBackgroundFillColorTertiary` | `#F9F9F9` | `#282828` |
| `SolidBackgroundFillColorQuarternary` | `#FFFFFF` | `#2C2C2C` |
| `CardStrokeColorDefaultSolid` | `#EBEBEB` | `#1C1C1C` |

Recommended concrete pairing for a Lattice hole treatment: fill `SystemFillColorSolidNeutralBackground` (`#F3F3F3` / `#2E2E2E`), stroke/hatch `SystemFillColorSolidNeutral` (`#8A8A8A` / `#9D9D9D`, the one neutral clearing 3:1 in both themes), label `TextFillColorSecondary`/Fluent-2 `colorNeutralForeground3` equivalent. Note `SystemFillColorNeutral` is WinUI's *Informational* InfoBar severity color — semantically "neutral status", the closest named match to "we have nothing to report here", and it keeps the hole out of the categorical data palette entirely. Note also that all the `ControlFillColorDisabled` variants are **alpha** (`#4DF9F9F9`, `#0BFFFFFF`) and will composite almost invisibly over a chart canvas — use the `Solid*` keys, not those.

### 1.4 WinUI Community Toolkit / Windows Community Toolkit / FluentAvalonia samples handling data gaps

- **Windows Community Toolkit (CommunityToolkit/Windows) — not found.** The toolkit ships controls, helpers and extensions for WinUI 2 / WinUI 3 / Uno; **no first-party chart control** found and therefore no gap guidance. The old UWP `Microsoft.Toolkit.Uwp.UI.Controls.DataVisualization` chart (a Silverlight port) did not carry forward. Repo: https://github.com/CommunityToolkit/Windows — **[product behavior]**, absence-of-feature. `CommunityToolkit/Labs-Windows/components` could not be enumerated (API returned empty, likely rate-limiting) — **unverified** whether a Labs charting experiment exists; no retry per the no-loop rule.
- **FluentAvalonia — not found.** FluentAvalonia ships no chart control and no data-viz samples; its repo contains only theming (`Styling/`) and WinUI-ported controls (`ControlThemes/`). No gap precedent exists there. **[product behavior, source-verified]** via repo tree listing.
- **LiveCharts2 (Lattice's actual charting engine) — [product behavior], documented gap support.** The idiomatic form is a nullable coordinate: `new ObservablePoint(x, double.IsNaN(y) ? null : y)` — an empty coordinate produces a gap rather than a zero. This is documented as the library's "Gaps/Null Points" sample and is confirmed working under WinUI 3 in a widely-cited walkthrough (https://xamlbrewer.wordpress.com/2023/12/04/displaying-charts-in-winui3-with-livecharts2/ — **[anecdote]**, third-party blog, but the API shape is checkable in LiveCharts2's own samples). Practical note: this gives Lattice a native way to express **5a** in the series data itself; **5c/5d**'s band, hairlines and labels are *additional* draw-layer work (custom visual / section), not a series feature.
- **Cross-ecosystem convention worth naming:** several stacks expose an explicit three-way null mode — connect / zero / gap — with **gap as the default** (e.g. Splunk `nullValueMode`), and Syncfusion's WinUI `LineSeries` has empty-point modes (gap / zero / average). **[anecdote]** (vendor docs, not Microsoft). The consistent industry default is *gap*, never *zero*.

### Group 1 key takeaways

- **The Microsoft ecosystem's single, consistent rule is: never synthesize a value for missing data — break the mark.** Fluent LineChart suppresses the `<line>` (`// don't draw line if it is in a gap`); Azure Monitor states "NULL is different than a zero value"; perfmon leaves a literal gap; Dev Home right-aligns the series and leaves bare canvas. There is **zero precedent for hatching (4a)** anywhere in Fluent. Candidates **5a** and **5d** sit squarely on the ecosystem's established line; **5c** is a deliberate extension beyond it.
- **But every Microsoft precedent that renders a *sum* pairs the gap with words, because the gap alone is ambiguous.** Azure Monitor explicitly documents that for count/sum aggregation "the dashed line drops down to zero" — i.e. Microsoft ships Lattice's exact failure mode in its own telemetry product. perfmon compensates with "A message appears describing this"; Fluent's reference gaps sample compensates with a dedicated dashed legend entry `'Low Confidence Data*'` and the footnote "* This data was below our confidence threshold." → for a stacked density band, blank-alone is *not* sufficient; **5c**'s duration label or **5d**'s annotation is the load-bearing part, and the hole deserves **its own legend entry**, not just an in-canvas mark.
- **`colorNeutralStencil1/2` is a trap.** Its name reads "placeholder" but it is the animated Skeleton fill, meaning *content is loading*. Lattice's holes are permanent. The semantically honest family is the disabled one: `colorNeutralBackgroundDisabled` = `#f0f0f0` (light) / `#141414` (dark).
- **No neutral *background* token is visible enough to carry 5c on its own.** All of them compute to ~1.1–1.2:1 against the chart canvas. The only neutrals clearing 3:1 in both themes are FluentAvalonia's `SystemFillColorSolidNeutral` — `#8A8A8A` light / `#9D9D9D` dark (WinUI's "Informational" neutral, and safely outside any categorical data palette). Spec 5c as fill `#F3F3F3`/`#2E2E2E` **plus** a `#8A8A8A`/`#9D9D9D` stroke, or it will vanish in one theme.
- **Fluent's AreaChart has no gap support in either v8 or v9 — only LineChart does.** For a stacked area/density band there is no first-party Fluent precedent to copy; Lattice is extending the line rule to a new form, which raises the bar on owner-eye visual verification and means the design cannot be defended as "just what Fluent does".
- **Fluent draws nothing for absent data but always states it accessibly** — `role="alert"` `aria-label="Graph has no data to display"` at `opacity: 0`. Whatever visual treatment wins, the hole region needs an equivalent accessible name, otherwise the "hole ≠ idle" distinction is carried by pixels alone.

## 2. Monitoring / observability products

### 2.1 Grafana — State Timeline & Status History panels

**The null segment has no color. Nothing is drawn at all — the panel background shows through.** This is the single most load-bearing finding in this group, and it is verified at the renderer level, not inferred from screenshots.

- The timeline renderer decides per-sample whether to emit a box via `shouldDrawYValue(yValue, mappedNull, mappedNaN)`. Verbatim from source ([`public/app/core/components/TimelineChart/timeline.ts` L62–79](https://github.com/grafana/grafana/blob/main/public/app/core/components/TimelineChart/timeline.ts)) **[product behavior — source]**:
  ```ts
  export function shouldDrawYValue(yValue: unknown, mappedNull?: boolean, mappedNaN?: boolean): boolean {
    if (typeof yValue === 'boolean') { return true; }
    if (typeof yValue === 'string') { return true; }
    if (typeof yValue === 'number' && !Number.isNaN(yValue)) { return true; }
    if (yValue === null && mappedNull) { return true; }
    if (Number.isNaN(yValue) && mappedNaN) { return true; }
    return !!yValue;
  }
  ```
  In the `TimelineMode.Changes` draw loop, a sample failing this predicate is skipped entirely — no `putBox`, no fill path, no stroke path, no quadtree hover rect. There is no "null color", no "no-data" theme token, no hatch, no muted grey. **This is candidate 5a in its purest form.** → **supports 5a**, and is evidence *against* 5c/4a being industry-default.
- The only escape hatch is a user-authored **value mapping** on the special value `Null`/`NullAndNaN` (`hasMappedNull` / `hasSpecialMappedValue(field, SpecialValueMatch.Null)`). If the user explicitly maps null to a display text + color, the box is drawn like any other state. So Grafana's position is: *blank by default, and a labelled colored band only if you deliberately declare "no data" to be a state*. → **directly relevant to 5c**: Grafana treats "hole as a named state" as an opt-in modelling decision, not a default rendering.
- Panel option **"Connect null values"** (`spanNulls`, default `false`) and **"Disconnect values"** (`insertNulls`, default `false`), registered in [`state-timeline/module.tsx`](https://github.com/grafana/grafana/blob/main/public/app/plugins/panel/state-timeline/module.tsx) **[official guidance]**. Doc text verbatim ([`docs/sources/shared/visualizations/connect-null-values.md`](https://github.com/grafana/grafana/blob/main/docs/sources/shared/visualizations/connect-null-values.md)):
  > "Choose how null values, which are gaps in the data, appear on the graph. Null values can be connected to form a continuous line or set to a threshold above which gaps in the data are no longer connected."
  > - **Never** — "Time series data points with gaps in the data are never connected."
  > - **Always** — "Time series data points with gaps in the data are always connected."
  > - **Threshold** — "Specify a threshold above which gaps in the data are no longer connected. This can be useful when the connected gaps in the data are of a known size and/or within a known range, and gaps outside this range should no longer be connected."
- **Disconnect values** (the inverse — synthesizes a hole when samples are too far apart) verbatim ([`docs/sources/shared/visualizations/disconnect-values.md`](https://github.com/grafana/grafana/blob/main/docs/sources/shared/visualizations/disconnect-values.md)):
  > "Choose whether to set a threshold above which values in the data should be disconnected. … **Threshold** - Specify a threshold above which values in the data are disconnected."
  The UI's default threshold when you switch either control off "Never" is **3600000 ms (1 h)**, hardcoded in `SpanNullsEditor.tsx` / `InsertNullsEditor.tsx` (`value: 3600000, // 1h`). Editor prefixes are asymmetric and worth copying: span-nulls uses `InputPrefix.LessThan` ("connect gaps **<** X"), insert-nulls uses `InputPrefix.GreaterThan` ("disconnect **>** X"). → **relevant to 4c/5c sizing**: a 1 h hole is Grafana's out-of-the-box "long enough to be suspicious" boundary.
- The mechanism that manufactures the hole is `applyNullInsertThreshold` in [`packages/grafana-data/src/transformations/transformers/nulls/nullInsertThreshold.ts`](https://github.com/grafana/grafana/blob/main/packages/grafana-data/src/transformations/transformers/nulls/nullInsertThreshold.ts) **[product behavior — source]**. Three insert modes exist; the default is `threshold: (prev, next, threshold) => prev + threshold`. There is a mode specifically for this panel family, with a comment that is directly on point for Lattice's failure mode:
  ```ts
  // previous time + 1ms to prevent StateTimeline from forward-interpolating prior state
  plusone: (prev, next, threshold) => prev + 1,
  ```
  The threshold per field is `field.config.custom?.insertNulls || refField.config.interval || null` — i.e. Grafana falls back to the *declared collection interval* to decide what counts as a hole. → **supports the general principle behind 5a/5d**: the anti-interpolation guard is applied in the data layer, before rendering, rather than by drawing something on top.
- **"No value" standard option** ([`configure-standard-options`](https://github.com/grafana/grafana/blob/main/docs/sources/visualizations/panels-visualizations/configure-standard-options/index.md) L274–276) **[official guidance]**:
  > "Enter what Grafana should display if the field value is empty or null. The default value is a hyphen (-)."
  In the timeline path this is consumed by `nullToValue(frame)`, which only substitutes when `Number(field.config.noValue)` is numeric — i.e. a *textual* "No value" string does not become a timeline band. It is a table/stat-panel affordance, not a timeline one.
- Docs wording for the panels themselves **[official guidance]**:
  - State timeline ([index.md](https://github.com/grafana/grafana/blob/main/docs/sources/visualizations/panels-visualizations/visualizations/state-timeline/index.md)): "Each state ends when the next state begins or when there is a `null` value." and "The data is converted as follows, with the null and empty values visualized as gaps in the state timeline".
  - Status History ([index.md](https://github.com/grafana/grafana/blob/main/docs/sources/visualizations/panels-visualizations/visualizations/status-history/index.md)): "The data is converted as follows, with the null and empty values visualized as gaps in the status history".
- **Exact default color/token for null segments: does not exist.** Verified by exhaustion of the render path (no `putBox` call is reached). A confirmed negative, not an unverified claim.

### 2.2 Home Assistant — history timeline

- The timeline's color resolution is a three-tier fallback in [`src/components/chart/timeline-color.ts`](https://github.com/home-assistant/frontend/blob/dev/src/components/chart/timeline-color.ts) **[product behavior — source]**. The first two branches are the special ones:
  ```ts
  if (!stateObj || state === UNAVAILABLE) {
    return computeCssValue("--history-unavailable-color", computedStyles);
  }
  if (state === UNKNOWN) {
    return computeCssValue("--history-unknown-color", computedStyles);
  }
  ```
- **Exact token values**, from [`src/resources/theme/color/color.globals.ts`](https://github.com/home-assistant/frontend/blob/dev/src/resources/theme/color/color.globals.ts) **[product behavior — source]**:
  - `--history-unavailable-color: transparent;` (L152)
  - `--history-unknown-color: var(--dark-grey-color);` (L262) → `--dark-grey-color: #606060;` (L90)
  - `--dark-grey-color` is **not** overridden in `darkColorStyles` (L340+), so `#606060` applies in both light and dark themes. Neither history token is overridden in the dark block either.
  - (For contrast, the *icon/entity* unavailable color is a separate token: `--state-unavailable-color: var(--state-icon-unavailable-color, var(--disabled-text-color))`, with `--disabled-text-color: #bdbdbd` light / `#6f6f6f` dark. Do not confuse it with the timeline one.)
- **So Home Assistant splits exactly the distinction Lattice needs**, and picks opposite treatments for the two halves: **`unavailable` → transparent (a literal hole in the band)**, **`unknown` → an opaque mid-dark grey `#606060` band**. Not striped, not hatched, not outlined. → **`unknown` supports 5c** (solid muted grey band as a *drawn* state); **`unavailable` supports 5a/5d** (blank). Note the semantic inversion vs. Lattice: HA's `unavailable` (source is gone) is the blank one, and `unknown` (source present, value not determinable) is the grey one.
- The segment is still a real ECharts custom-series item with `itemStyle: { color }` and a tooltip, so a transparent segment remains hoverable and reports its localized state name — a blank region that is still *interrogable*. Text contrast is chosen by `luminosity(hex2rgb(color)) > 0.5 ? "#000" : "#fff"` (which is why a `transparent` fill is a slightly degenerate case in their own code). → **supports 5d's "blank but annotated" shape**: blank pixels do not have to mean an inert region.
- **[anecdote]** Open frontend issue [home-assistant/frontend#15866](https://github.com/home-assistant/frontend/issues/15866) "Wrong & inconsistent history for `unavailable` value" reports that the *state timeline* and the *line/history-graph* disagree about unavailable segments (band vs. gap), with the reporter arguing a gap must be shown in the graph. Treat as user-reported inconsistency, not documented behavior.
- **Not found:** any HA documentation page that states the rendering rule for unavailable/unknown in prose. The rule exists only in code.

### 2.3 Atlassian Statuspage — 90-day uptime bars

- **Exact grey: `#EAEAEA`.** Verified from the shipped production bundle (`packs/status-*.chunk.js` served from `dka575ofm4ao0.cloudfront.net`, fetched 2026-08-02) **[product behavior — shipped code]**. The day component declares:
  ```js
  H.defaultProps = { color: "#EAEAEA" }
  ```
  and its own render treats that hex as the sentinel for "not a real day":
  ```js
  var i = o || "#EAEAEA" === n ? "" : " active";
  ```
  i.e. a `#EAEAEA` day gets no `active` class, so it is excluded from the hover/highlight affordance the colored days get. **A no-data day is rendered as a flat light-grey rect with no texture, no border, no annotation, and reduced interactivity.**
- **Verbatim tooltip strings**, extracted from the same bundle **[product behavior — shipped code]**:
  - `"No data exists for this day."` (rendered into `<div className="no-data-msg">`)
  - `"No downtime recorded on this day."` (rendered into `<div className="no-outages-msg">`)
  - `"No incidents or maintenance related to this downtime."`
  These two are the crucial pair: Statuspage keeps **"no data"** and **"no downtime"** as *distinct tooltip copy on visually distinct bars* — exactly the "hole vs. idle" separation Lattice needs. → **supports 5c** (muted grey + explicit label), and specifically supports pairing the grey with *wording that names the absence*, not just a color.
- **Hatching is reserved for severity, not for absence.** Behind the `renderUptimePatternFF` feature flag, Statuspage overlays a texture on *outage* days only: a major-outage day gets a diagonal-stripe `<path>` group at `opacity: 0.5` in white over the base fill; a partial-outage day gets horizontal stripes (`M32 1.33203H0V3.9987H32V1.33203Z` etc.) at the same opacity. No-data days get a plain `<rect>` with no overlay. **[product behavior — shipped code]** → **evidence against 4a**: the one mainstream product in this group that ships hatching spends it on the *severe* state, and would collide with 4a's semantics if Lattice used stripes for holes.
- Supporting CSS (`status_manifest-*.css`): `.shared-partial.uptime-90-days-wrapper svg rect:hover { fill: #5e6c84 }`; legend items `color: #aaa; opacity: 0.8` (and `.legend-item.light` at `opacity: 0.5`); the legend's connector `.spacer { background: #aaa; opacity: 0.3 }`. The older non-showcase uptime widget uses discrete classes `.unit-container.green{#2ecc71}`, `.yellow{#f1c40f}`, `.red{#e74c3c}` with **no grey class** — an uncolored unit falls through to the container background. **[product behavior — shipped code]**
- **[official guidance]** [Display historical uptime of components](https://support.atlassian.com/statuspage/docs/display-historical-uptime-of-components/) explains *when* grey appears: days predating the component's creation or predating uptime tracking have no records. Third-party component integrations "do not pull historical data" — only forward. Missing days can be backfilled manually via the pencil icon on the uptime showcase. Note the trap Statuspage falls into and Lattice must not: Statuspage otherwise **assumes operational unless downtime minutes are recorded**, so an unmonitored-but-existing component shows solid green, not grey — the exact "hole read as healthy" failure mode.

### 2.4 Datadog / New Relic / Netdata

**Datadog — connects across gaps by default; making holes visible is opt-in.** **[official guidance]**
- Line graphs join points, so an Agent outage renders as a long straight line rather than a hole. Datadog's own remedies are to *change the mark type or the data*, never to draw a hole annotation: switch to **Bars** ("with the bar graph display you can visualize the gaps between datapoints more clearly" — bars are discrete and are not interpolated), or apply `default_zero()` to "help reveal the gaps in data". See [Interpolation and the Fill Modifier](https://docs.datadoghq.com/metrics/guide/interpolation-the-fill-modifier-explained/), [Interpolation functions](https://docs.datadoghq.com/dashboards/functions/interpolation/), [Monitoring Sparse Metrics](https://docs.datadoghq.com/monitors/guide/monitoring-sparse-metrics/).
- Interpolation is linear and bounded: performed up to **five minutes** after real samples; `.fill(null)` suppresses it; explicit `LIMIT` defaults to **300 s**, max **600 s**. Interpolation is on by default for GAUGE metrics.
- Datadog's own anti-pattern post warns that line graphs are the wrong mark for sparse metrics because the system "tries to interpolate between points that aren't smoothly continuous" ([Graphing anti-patterns](https://www.datadoghq.com/blog/anti-patterns-metric-graphs-101/)).
- **`default_zero()` is precisely the anti-pattern Lattice is trying to avoid** — it makes a hole look like a measured zero. Worth citing as the negative exemplar. → **evidence against any treatment that renders a hole at the zero baseline without a distinguishing mark**, i.e. an argument that 5a alone (blank + baseline break) is only safe if the baseline itself visibly breaks.

**New Relic — gap filling is an *alerting* concept; charts have no gap-marking feature.** **[official guidance]**
- `signal.fillOption` ∈ `NONE` (default) | `LAST_VALUE` | `STATIC` (+ `fillValue`); UI path *Condition settings → Advanced signal settings → "fill data gaps with"*. Gaps are filled only after a subsequent data point arrives; signal history is retained for a minimum of 2 h (or the threshold duration if longer), after which the signal is "expired" and the gap is no longer filled. See [NerdGraph: Loss of signal and gap filling](https://docs.newrelic.com/docs/apis/nerdgraph/examples/nerdgraph-api-loss-signal-gap-filling/), [Create NRQL alert conditions](https://docs.newrelic.com/docs/alerts-applied-intelligence/new-relic-alerts/alert-conditions/create-nrql-alert-conditions/).
- **There is no `FILL` clause in NRQL and no documented chart-level gap rendering option.** Confirmed absence, not an unsearched area — the visualization layer simply shows nothing. **Unverifiable:** any exact color/token for a New Relic chart gap; none is documented.
- Conceptually useful: New Relic separates *loss of signal* (the source stopped) from *gap* (a hole in an otherwise live signal), and expires the distinction after a timeout. → **maps onto Lattice's routine-hole vs. unreachable-hole split**.

**Netdata — the strongest philosophical match to Lattice's requirement.** **[official guidance]**
- Gaps are a first-class persisted value, not a rendering artifact. From the DBENGINE docs: each data point holds a value and "there is a special value to indicate that the collector failed to collect a valid value", making that point a gap; if a collection is missed, "a gap point is inserted into the page so the data points in a page remain contiguous." Query code confirms the same distinction at the C level — `storage_point_is_gap(sp)` is checked separately from `storage_point_is_unset(sp)` in [`src/web/api/queries/query-execute.c`](https://github.com/netdata/netdata/blob/master/src/web/api/queries/query-execute.c) and [`weights.c`](https://github.com/netdata/netdata/blob/master/src/web/api/queries/weights.c), and one comment in `query-group-by-finalize.c` reads: `// all series collected zeros (or cancel out): report 0%, not a gap` — an explicit refusal to conflate measured-zero with unobserved. **[product behavior — source]**
- **Netdata ships a dedicated coverage strip.** From [`docs/NIDL-Framework.md`](https://github.com/netdata/netdata/blob/master/docs/NIDL-Framework.md), the canonical chart anatomy, verbatim ASCII:
  ```
  ┌───────────────────────────────────────────────────────────────────────────┐
  │ ▒▒▒▒▒░░░▒▒▒▒▒▒░░░░▒▒▒  Anomaly ribbon (anomaly rates over time)           │
  ├───────────────────────────────────────────────────────────────────────────┤
  │     ╱╲    ╱╲                                                              │
  │    ╱  ╲  ╱  ╲                   GRAPH                                     │
  │   ╱    ╲╱    ╲                                                            │
  │  ╱            ╲_______________                                            │
  ├───────────────────────────────────────────────────────────────────────────┤
  │ ░░░░█░░░░░  Info ribbon (gaps, resets, partial data)                      │
  └───────────────────────────────────────────────────────────────────────────┘
                                      X-axis (time)
  ```
  **A thin, time-aligned strip directly under the plot area whose sole job is to mark gaps.** → **this is candidate 5b, shipping in production, from the most gap-conscious product in the group.** Strong support for 5b as a *complement* to a blank/grey band rather than a competitor to it — Netdata pairs the ribbon with an otherwise unannotated graph.
- Hover detail is a table, not a color code ([`docs/dashboards-and-charts/netdata-charts.md`](https://github.com/netdata/netdata/blob/master/docs/dashboards-and-charts/netdata-charts.md) L338–346) **[official guidance]**:
  | Indicator | Description |
  |---|---|
  | Partial Data | "At least one dimension has partial data (not all instances contributed fully)" |
  | Overflown | "At least one data source has a counter that overflowed" |
  | Empty Data | "At least one dimension has no data for the selected points" |
- Netdata's stated design principle, quoted in its own docs: **"Gaps Are Failures: Missing data is a symptom of system distress, not an acceptable network condition."** Note this is the *opposite* of Lattice's situation — Lattice's holes are routine (app closed nightly), so Netdata's alarming framing should not be copied wholesale; only its *representational* separation should be.
- **Not found:** an exact hex/token for the Info ribbon's gap marking. The dashboard is a closed-source React app (`netdata/dashboard` bundles); the ASCII diagram shows `░` for gap regions vs `█` for a marked event, but no color value is published.

### 2.5 BOINCTasks (eFMer) — the direct competitor

**BOINCTasks does not represent unobserved periods at all. There is nothing to copy, and a clear differentiator to claim.**

- **boinctasks-js (Electron rewrite) has no charts whatsoever.** Verified by downloading the full source tree from [efmer/boinctasks-js @ main](https://github.com/efmer/boinctasks-js) and searching it **[product behavior — source]**:
  - `grep -ril "canvas"` over the repo (excluding `node_modules`/`dist`) returns **zero matches**; there is no charting library dependency.
  - The History feature is `boinctasks/rpc/history/{history.js, process_history.js, table_history.js}` — a **table**, backed by a single `<get_old_results/>` RPC (`functions.sendRequest(con.client_socket, "<get_old_results/>")` in `history.js`), plus a locally persisted CSV.
  - `boinctasks/rpc/statistics/` (`statistics_boinc.js`, `statistics_tasks.js`, `statistics_transfer_boinc.js`) and its `index_statistics_*.html` views are likewise tabular.
  - **A timeline/density visualization is an open field in the direct competitor.**
- **BoincTasks classic (Windows) has graphs but no gap semantics.** The official [BoincTasks Graphs](https://efmer.com/boinctasks/boinctasks-graphs/) page documents the Statistics/Credit graph, Tasks graph, and Temperature graph. Its options are enumerated exhaustively (Enlarge, Multiple selections, Combine projects, Average, block mode, CPU/GPU, "Over a period of", Line thickness, Expanded selection box, Update, Colors) — **there is no option, legend entry, or prose anywhere on the page concerning missing data, gaps, downtime, or periods when BoincTasks was not running.** **[official guidance — confirmed absence]**
- The one adjacent setting is a *scaling* correction, not a coverage indicator. Verbatim from [BoincTasks Settings](https://efmer.com/boinctasks/boinctasks_settings/) **[official guidance]**:
  > "**Adjust time to BOINC client run time** — When checked, times in the project tab, deadline graph and gadget, will be adjusted to the computer run time. … E.g. if the computer runs 50 percent, the time changes from 1 to 2 days."
  This normalizes *the BOINC client's* duty cycle, not *BoincTasks'* observation coverage.
- History collection is poll-driven and file-backed (history CSVs under `%AppData%\eFMer\BoincTasks\history`), with a "Smart mode" cycle that "can take up to 120 seconds", so even while running it under-samples: "if the checkpoint interval is set to e.g. 60 seconds, some will be missed." **[official guidance]** Periods with BoincTasks closed simply produce no samples.
- **[anecdote]** Community/changelog material describes a known "missed tasks" problem (tasks that start and finish entirely while BoincTasks is shut down are never recorded) and a mitigation in which the history fetch's time-left calculation also uses elapsed × fraction-left, taking the lower value. Also anecdotal: running BoincTasks under two simultaneously logged-in users corrupts the history files. Not verified against a primary changelog.
- → **Implication for Lattice**: every candidate 4a–5d is a net improvement over the competitor's baseline of "silence." The choice should be made on Lattice's own honesty requirement, not on parity pressure.

### 2.6 Chrome tracing (catapult) / Perfetto

- **Perfetto has no "tracing not active" concept. A period with no data is empty track space, full stop.** Verbatim from [`docs/concepts/buffers.md`](https://github.com/google/perfetto/blob/master/docs/concepts/buffers.md) (also at [perfetto.dev/docs/concepts/buffers](https://perfetto.dev/docs/concepts/buffers)) **[official guidance]**:
  > "The UI can show an abnormally long timeline with a huge gap in the middle. The packet ordering of events doesn't matter for the UI because events are sorted by timestamp at import time. The trace in this case will contain very recent events plus a handful of stale events that happened hours before. The UI, for correctness, will try to display all events, showing a handful of early events, followed by a huge temporal gap when nothing happened, followed by the stream of recent events."
  Note the phrase **"when nothing happened"** — the doc itself slides from "no events recorded" to "nothing happened", which is exactly Lattice's failure mode appearing in a mature product's own documentation. The named cause is a per-CPU ftrace buffer for a CPU that was idle or hot-unplugged.
- **The distinction Perfetto does make is out-of-band, not on the timeline.** Data loss is recoverable only from counters, not pixels: `FtraceCpuStats` messages at the beginning and end of the trace (a non-zero `overrun` field means data was lost), and `FtraceEventBundle.lost_events`, which "allows you to locate precisely the point where data loss happened." **[official guidance]** So Perfetto knows *exactly* where the hole is and still does not draw it — it surfaces it in the Trace Info / stats surface instead. → **an argument for 5b's "coverage lives on its own track" framing over 4a/4c's "decorate the main band" framing**, and a caution that a stats-page-only treatment (Perfetto's actual choice) is insufficient for a routine-hole product like Lattice.
- Perfetto's own gap-vs-loss ambiguity is acknowledged in the same doc: an empty stretch "could mean either" nothing happened or data was lost, resolvable only by the loss counters.
- **Not found:** any UI-side hatch, shading, axis-break, or "unrecorded" affordance in `ui/src`. Two GitHub code searches over `google/perfetto` (`path:ui/src/frontend` for no-data/nodata; `path:ui/src` for data-loss/lost_events/gap) returned zero results. The full repo was not cloned to exhaust this, so treat as **"no evidence found"** rather than a proven absence — though it is consistent with the documented behavior above. Legacy catapult/`chrome://tracing`: **not investigated** (deprecated in favor of Perfetto); no findings.

---

### Group 2 key takeaways

- **The dominant industry default for "no observation" on a timeline is to draw nothing at all.** Grafana proves it at the renderer level (`shouldDrawYValue` returns false → no box, no color token exists), Home Assistant sets `--history-unavailable-color: transparent`, and Perfetto documents an unmarked "huge temporal gap." **5a is the conservative, best-precedented choice**; 5c is the outlier among these six.
- **But the two products that face *routine, expected* holes both add something.** Statuspage draws a flat `#EAEAEA` grey day with the verbatim tooltip "No data exists for this day.", explicitly distinct from "No downtime recorded on this day." Netdata ships an **Info ribbon (gaps, resets, partial data)** as a separate strip under the graph. → **5c and 5b are each validated by a shipping product whose gap frequency resembles Lattice's**, whereas the blank-by-default products treat holes as rare anomalies.
- **Hatching/striping is taken. Statuspage spends it on outage severity** (diagonal stripes = major, horizontal = partial, both white at `opacity: 0.5` over the state fill), not on absence — and no product in this group hatches a hole. **4a should be considered semantically encumbered** and is the weakest-supported candidate.
- **Home Assistant independently arrived at Lattice's two-tier hole model and split the treatments**: `unavailable` → `transparent` (blank), `unknown` → opaque `#606060` (drawn grey band). This is direct precedent that *the two kinds of hole can and should look different* — supporting the plan's separate red-dashed frame for the exceptional unreachable case, and suggesting the routine case is the one that goes blank.
- **The gap-vs-zero conflation is a documented, named hazard, and the standard remedies are data-layer, not paint-layer.** Grafana's `insertNulls` (default threshold 1 h) with its `plusone` mode commented "to prevent StateTimeline from forward-interpolating prior state"; Netdata's `storage_point_is_gap()` distinct from `storage_point_is_unset()` and its "report 0%, not a gap" comment; Datadog's `default_zero()` as the explicit anti-pattern. Whatever visual Lattice picks, **the hole must exist as a typed value in the model before it is rendered**, or the band will silently sit at zero.
- **The direct competitor represents unobserved periods nowhere.** boinctasks-js has zero charting code (no `canvas`, no chart library; History is a `get_old_results` table); classic BoincTasks' Graphs page documents no gap, downtime, or coverage option at all. Any of 4a–5d is a differentiator, so choose on honesty grounds rather than parity.
- **Two evidentiary gaps, stated plainly:** no published hex/token exists for Netdata's Info-ribbon gap marking (closed-source dashboard bundle), and no color/token is documented for New Relic chart gaps (gap filling there is an alerting feature, not a chart one — there is no `FILL` clause in NRQL). Perfetto UI's absence of gap affordances is "no evidence found" from two code searches, not an exhaustive proof.

## 3. Other design systems' data-viz guidance

### 3.1 IBM Carbon Design System — **strongest stated rule found**

Carbon has an explicit, named **"Gaps in data"** section under Data visualization → Axes and labels.

- **[official guidance]** — https://carbondesignsystem.com/data-visualization/axes-and-labels/ (source of truth: `carbon-design-system/carbon-website`, `src/pages/data-visualization/axes-and-labels/index.mdx`, lines 76–79):
  > "Never interpolate between periods when data is unavailable. Always label both the start and end point during which data is not available."

  This is a two-part rule and both halves matter for Lattice: never bridge, **and** label *both* boundaries. Directly endorses **5d** (blank + edge markers + annotation) over bare **5a**.

- **[official guidance]** — same page, "Breaks in axes": when the axis is compressed, *"use a sinusoidal line to replace the straight axis line"*, with a stated minimum of 16px width on X and a fixed 16px on Y. And explicitly for the case where the hole has no data:
  > "If data isn't available between axis breakpoints, leave the area empty."

  This is a concrete, spec'd implementation of **4c** (axis-break glyph collapsing long holes), and it argues *against* **5c** (a solid muted grey band) for the collapsed case — Carbon leaves the region genuinely empty rather than filling it.

- **[official guidance]** — same page, "Time series": *"Never change axis ticks increments to accommodate data availability. If any form of axis compression is required, use the provided axis break styling to visually denote the compression."* I.e. compression is allowed but must be **declared with a glyph**, never smuggled in by silently re-spacing ticks. Relevant guardrail if Lattice adopts 4c.

- **Important negative finding on 4a (hatching).** Carbon's illustration filenames/alt-text repeatedly read `"Gap in data denoted by texture"`, which reads as an endorsement of hatching. **The rendered images contain no texture.** Both were downloaded and inspected: `axislabel-gap.png` shows a plain blank collapsed gap with the two boundary hour labels set in semibold ("landmark labels"); `axislabel-break-2.png` shows the blank region with a **small sinusoidal squiggle drawn on the baseline** at the break point. The alt text is stale copy-paste, not guidance. So **Carbon does not actually support 4a** — its shipped visual is 5a/5d/4c.
  - Note the baseline squiggle in `axislabel-break-2.png` is an interesting hybrid for Lattice: because a concurrency density band sits *on* a baseline, an axis-level break glyph is available without touching the band itself.

- **Carbon Gantt charts page** (https://carbondesignsystem.com/data-visualization/gantt-charts/): **not found** — no missing/unobserved-time guidance. The page covers card component, task component, and a colour-adjacency recommendation only, and is marked as not implemented in `carbon-charts`.
- All Carbon data-viz pages carry a standing caveat that *"This guidance is a work in progress."*
- **Gap in coverage:** `carbon-charts` library-level null defaults unverified — the GitHub code-search API rate-limited and no retry was made per the no-retry rule. Unverified, but this would be [product behavior] anyway.

### 3.2 UK Government Analysis Function / ONS — **the most on-point stated rule anywhere**

This is a cross-government statistical standard, and it addresses exactly Lattice's failure mode.

- **[official guidance]** — Government Analysis Function, "Data visualisation: charts", section **"Breaks in time series"**: https://analysisfunction.civilservice.gov.uk/policy-store/data-visualisation-charts/
  > "The best way to deal with discontinuity varies depending on the data and the story you are telling. But, it must always be highlighted."

  and, decisively:
  > "If you do use a line, do not join the points either side of the missing data point, even if the line is dotted or dashed. Joining points implies we know something about the data."

  The stated rationale — *joining implies we know something* — is the exact argument for Lattice's "never backfilled, never interpolated" rule.

- **[official guidance]** — same page, the worked do/don't pair (described in the page's own alt text): the **correct** version *"leaves a gap in the line and marks out the gap with two vertical dashed lines and annotation stating the data was not collected in 2020 due to the coronavirus (COVID-19) pandemic"* (green tick); the version that *"joins the gap with a dashed line"* gets a red cross.
  - **This is candidate 5d, endorsed essentially verbatim by a government statistical standard**: blank + dashed edge hairlines + a duration/reason annotation. It is the single strongest external precedent in this group.
  - It also **rules against 4b** in isolation (a tint on observed time with holes left bare and unmarked fails "must always be highlighted"), and against any dashed *bridging* treatment.

- **[official guidance]** — same page also offers an escape hatch worth noting: *"You do not always have to use a line for a time series with a discontinuity. Displaying the data as individual points may be better."* The general principle: when continuity is not warranted, drop the mark that implies continuity.

- **[official guidance]** — ONS Service Manual, Line chart, section "Irregular and missing data": https://service-manual.ons.gov.uk/data-visualisation/chart-types/line-chart
  > "Leave gaps in the line chart where otherwise regular data is missing to accurately represent the data's continuity."

  And, for the adjacent problem of *irregular* sampling: *"Use data markers to indicate individual data points when intervals are irregular. This will help users understand the frequency."* — i.e. make the observation cadence itself visible. Conceptually adjacent to **5b** (a coverage track that shows when observation happened).

### 3.3 GitLab Pajamas — **directly contradicts the UK guidance**

- **[official guidance]** — https://design.gitlab.com/data-visualization/charts/ (verified verbatim from page HTML):
  > "Represent gaps in continuous data with a dashed `$grey-300` line, and without a data point."

  Pajamas prescribes a **visible muted-grey dashed bridging line** across the gap, omitting only the data point. This is the *exact* treatment the Government Analysis Function marks with a red cross.
  - For Lattice this is a genuine design-system-level conflict, worth flagging to the owner: Pajamas is a developer-tooling dashboard system (built on ECharts) whose hole semantics are "sampling gap", whereas the UK guidance targets published statistics where a bridged line is a claim about the world. Lattice's holes are the *statistics* case — the hole means "we do not know", and the failure mode is misreading it as idle. **Pajamas's precedent is the weaker one for Lattice's purpose**, but its "muted grey, visually present, distinguishable" instinct is the same instinct behind front-runner **5c**.
  - **not found** in Pajamas: any rule on null-vs-zero, partial series, or annotating *why* data is missing. Its "no data" handling is pushed up to the dashboard-panel empty state, not the chart.

### 3.4 Adobe Spectrum — stated guidance, and it says the **opposite** of what Lattice needs

- **[official guidance]** — Line chart, "Null values" (https://spectrum.adobe.com/page/line-chart/, verified verbatim from page payload):
  > "When data returns null (blank) values, a chart should treat these as zeros."

  Identical wording on Bar chart (https://spectrum.adobe.com/page/bar-chart/). Histogram (https://spectrum.adobe.com/page/histogram/) goes further:
  > "When data returns null (blank) values, treat these as zeros. Place these value on a separate x-axis labeled as 'Null.'"

  **This is precisely Lattice's named failure mode** — "a hole read as 'the machine was idle'" — codified as house style by a major design system. Worth citing in the design doc as an explicit anti-pattern to reject, with the reason: Spectrum's charts are analytics-product charts where null commonly *does* mean a zero-count bucket; Lattice's holes are epistemic, not quantitative.

- **[official guidance]** — Area chart (https://spectrum.adobe.com/page/area-chart/) is more nuanced and closer to useful:
  > "When a dimension item returns a null value, the area representing it on the chart should be plotted and labeled as 'null' or 'unknown.' Null dates shouldn't be plotted…"

  Two transferable ideas: (a) an unknown **category** gets its own labelled visual presence rather than being silently dropped — an argument for **5c**'s labelled band; (b) but a null **date** is not plotted at all.

- **[official guidance]** — repeated verbatim across Histogram, Line, Bar and Area behaviours, "Empty state":
  > "When there is no data available, a chart should indicate as such and give direction as to how to make data appear there. Do not render an empty chart."

  Relevant to the Lattice edge case of a host row that is **100% unobserved**: don't render a blank lane, render a stated reason plus a next action.

- Spectrum's Scatter plot takes a third route worth noting for the annotation design: nulls are excluded, but *"Excluded values should be explained using non-removable tags"* and the count of excluded points is surfaced below the chart. The principle — **quantify what was dropped, in persistent chrome** — supports the duration-label half of **5c**/**5d**.

### 3.5 Material Design — **not found**

- **[official guidance] — absent.** Neither m3.material.io nor the M2 data-visualization article contains a missing-data / null-value rule. The only M3 data-viz document is the accessibility blog post "Top Tips for Data Visualization & Accessibility" (https://m3.material.io/blog/data-visualization-accessibility), whose six tips cover familiar chart types, comparability, summaries and orientation, exploration, labelling/structure, and multisensory output — **no missing-data guidance**. The fuller M2 article (https://m2.material.io/design/communication/data-visualization.html) is not republished in M3.
- Note both Material sites are fully client-rendered and return no text to `curl`; the above rests on search-surfaced page content, not a raw-HTML verification. Confidence: high on absence (the M3 sitemap has no missing-data page), moderate on completeness of the M2 article's text.
- **Absence is itself a finding**: the largest consumer-facing design system has no position here, which is consistent with the pattern that missing-data rules come from *statistical* publishers (ONS, Carbon, Urban) rather than consumer UI systems.

### 3.6 Highcharts — **API-level [product behavior]; no official article found**

- **[product behavior]** — `plotOptions.series.connectNulls` (https://api.highcharts.com/highcharts/plotOptions.series.connectNulls), verbatim:
  > "Whether to connect a graph line across null points, or render a gap between the two points on either side of the null. In stacked area chart, if `connectNulls` is set to true, null points are interpreted as 0."

  The second sentence is a documented **hole→zero collapse** — the Lattice failure mode, stated as a library behaviour. Since a stacked concurrency density band is structurally a stacked area chart, this is a concrete warning about how the misread arises mechanically, not just perceptually.

- **[product behavior] with a stated use rationale** — `plotOptions.series.gapSize` (Highcharts Stock, https://api.highcharts.com/highstock/plotOptions.series.gapSize): defines when to display a gap together with `gapUnit`, and *"in practice, this option is most often used to visualize gaps in time series"* — the canonical example being that intraday data exists for daytime hours while gaps appear at night. That is structurally the same shape as Lattice's routine nightly holes, and it is notable that Stock's answer is a **time-threshold-based automatic gap**, not a data-driven one. If Lattice adopts a "gap longer than N ⇒ collapse/annotate" threshold (4c), `gapSize`/`gapUnit`/`autoGapCount` is the established prior art for parameterizing it.

- **[not found]** — no Highcharts *blog* or written best-practice article on displaying missing data. Two searches surfaced only API reference pages, forum threads, and GitHub issues. The forum is user-support **[anecdote]**, not stated guidance; one recurring theme there worth one line: an ordinal x-axis (`xAxis.ordinal: true`) silently collapses empty periods, so gaps you intend to show can vanish. That is a real implementation trap for any 5a/5d treatment.

### 3.7 amCharts — **[product behavior] default only, no stated guidance**

- **[product behavior]** — Line series docs (https://www.amcharts.com/docs/v5/charts/xy-chart/series/line-series/), verbatim:
  > "A line series is normally displayed as a continuous line, jumping over gaps in data by connecting two data items that do have data. To make it break in cases where data is missing, we need to set series `connect` setting to `false`."

  **The default is the unsafe one**: `connect` defaults to `true`, i.e. amCharts bridges holes unless you opt out. No rationale, no recommendation, no do/don't — purely mechanical description.

- **[product behavior]** — the one genuinely transferable mechanic: with a date axis, breaks are **inferred from time spacing**, not from explicit null rows. *"the line will also break if the distance between two data items is greater than granularity (as defined with `baseInterval` setting of the date axis)"*, tunable via `autoGapCount` (e.g. `3.1` to break only at ≥3-day separation). Same idea as Highcharts `gapSize`. For Lattice this validates deriving holes from an observation-cadence model rather than requiring materialized null records — and `autoGapCount` is prior art for a **minimum hole duration below which you don't annotate**, which 5c/5d will need to avoid annotation spam on every poll miss.
- amCharts v4 docs additionally note automatic gaps only work with a `DateAxis`; on a `ValueAxis` you must insert empty data points.

### 3.8 ECharts — thin stated guidance, one useful sentence

- **[official guidance]**, minimal — Apache ECharts Handbook, "Basic Line" → "Empty Data" (https://apache.github.io/echarts-handbook/en/how-to/chart-types/line/basic-line/, verified against repo source `contents/en/how-to/chart-types/line/basic-line.md`):
  > "In a `series`, there are empty data. It has some difference with `0`. While there are empty elements, the lines chart will ignore that point without pass through it----empty elements will not be connected by the points next by."

  and the callout line:
  > "Please note the difference between the empty data and 0."

  Thin, awkwardly worded, but it *is* a stated instruction and it is the correct one: **empty ≠ 0**. ECharts uses `'-'` as its null sentinel (`data: [0, 22, '-', 23, 19]`), and `series.connectNulls` **defaults to `false`** — i.e. ECharts is the one mainstream library whose default is the honest one.

- **[product behavior] / open gap** — apache/echarts issue #9220 ("Echart have no presentation options for null values in trends or line charts") is a long-standing request that a connect-across-nulls segment be renderable as dashed/dotted, on the stated grounds that it *matters to differentiate that there is actually no data* from a real connection. It also asks for implicit null detection on time and category axes. Tracked to a 6.0 milestone. This is a request, not guidance — but it documents that the "bridge with a visually distinct stroke" idea (Pajamas's rule) is a widely felt need that ECharts deliberately has not shipped.

### 3.9 Observable Plot / D3 / Vega-Lite

- **Observable Plot — [product behavior] with a clearly stated semantic distinction.** Marks docs (https://observablehq.com/plot/features/marks) and Area mark (https://observablehq.com/plot/marks/area): channel values that are `null`, `undefined` or `NaN` are implicitly filtered, and for path marks *gaps will appear between adjacent points*. The load-bearing point Plot documents explicitly: **whether you get a gap or an interpolation depends on whether the missing rows are present-with-null or absent**. Leave `null` in ⇒ gap; drop the row ⇒ the line interpolates straight across. There is no written "you should show gaps honestly" recommendation, so this is product behavior — but the null-row-vs-absent-row distinction is a real architectural lesson for Lattice's data model: **an unobserved interval must be a materialized record, not an absence**, or the renderer cannot tell it from "no such time".
  - Plot's `interval` option is the sanctioned way to regularize sampled data, with a documented caution that using the `sum` reducer *defaults to zero instead of showing gaps in data* — again the hole→zero trap, this time as a transform footgun.
- **D3 — [product behavior].** `line.defined` / `area.defined` are the underlying mechanism; the `defined` accessor treats `NaN` and `undefined` as missing. (Note: `defined` is a D3 shape concept that Plot applies implicitly, not a Plot channel.)
- **Vega-Lite — [product behavior], but with the most carefully *named* taxonomy of any library here.** "Modes for Handling Invalid Data" (https://vega.github.io/vega-lite/docs/invalid-data.html) defines `mark.invalid` modes, and the distinction between two of them is exactly Lattice's 4c-vs-5a question:
  - `"filter"` — excludes invalid values from marks **and scale domains**; for path marks this *"will create paths that connect valid points, as if the data rows with invalid values do not exist"* (i.e. silent interpolation — the trap).
  - `"break-paths-filter-domains"` — breaks the path, but the missing period is **dropped from the axis domain** (≈ collapsing the hole, **4c**).
  - `"break-paths-show-domains"` — breaks the path **and keeps the missing period in the scale domain** (≈ **5a**, hole preserved at true width).
  - `"break-paths-show-path-domains"` is the v5 default, for backward-compat reasons only.
  - The docs also carry the warning most relevant to Lattice, on `"show"` mode: invalid values *"will produce the same visual values as zero (if the scale includes zero)"*. Third independent library documenting hole→zero as a real rendering outcome.
  - Vega-Lite's separate `impute` transform is the explicit, opt-in, *named* way to fill gaps — the point being that **filling is a transform you must ask for by name**, never a rendering default. Good conceptual model for Lattice: the timeline has no impute step at all.

### 3.10 Urban Institute Data Visualization Style Guide

- **[official guidance]**, general — https://urbaninstitute.github.io/graphics-styleguide/, "Showing uncertainty":
  > "It is therefore important to clearly explain when certain data are missing or if there is uncertainty around certain estimates."

  A duty-to-explain rule rather than a specific visual treatment. Supports the **annotation** half of 5c/5d; silent on the mark.
- **[official guidance]**, and the most directly transferable item — in the guide's table conventions:
  > "Use asterisk to indicate statistical significance, use em dash to indicate missing values, and use caret symbol to indicate masked values."

  Note the structure: **missing** and **masked** get *different glyphs*. Urban treats "we don't have it" and "we have it but are withholding it" as distinct states requiring distinct marks — a direct precedent for Lattice distinguishing **routine unobserved** (app was off) from **exceptional unreachable** (host down while app running), which is exactly the red-dashed-frame decision. It also confirms the general instinct that the two hole classes should not share one visual token.
- **[not found]** — no line-chart-specific "do not interpolate" rule in the Urban guide; its missing-data treatment lives in the uncertainty and table-notes sections.

### 3.11 Datawrapper Academy — vendor guidance, unusually well-reasoned

Not a design system, but it is written, official, and it is the only source that argues the *epistemics* of the choice.

- **[official guidance]** — "How to deal with missing data in line charts" (https://academy.datawrapper.de/article/321-patchy-data, last updated 2025-01-20):
  > "When you have missing data points, you can't always assume that the data developed smoothly between the data points that exist. The missing data might be strong outliers."

  followed by the recommendation that *to avoid misleading readers, you can keep gaps in your lines even when connecting data points* (their `"NA"` / `"–"` convention).
- **[official guidance]** — three concrete design tactics they recommend for making holes legible, all mapping onto Lattice candidates:
  - **Show line symbols on all data points** — *"the most intuitive way to make visible where your actual data points are"*. This is essentially **5b** (a coverage track): make the observation events themselves visible so the reader can see where evidence exists.
  - **Stepped interpolation** (they recommend "Steps (after)" or "Steps (before)") to *"make the intervals between your data points nicely clear"* — with the honest caveat that steps are *"not useful for making visible where your data points are"*.
  - **Dashed lines for assumed lines**: *"differentiate parts of the data that were collected and those that were assumed"* by combining solid and dashed strokes. Note this is scoped to **assumed/modelled** data, not to unknown data — which is a cleaner reading than Pajamas's, and consistent with the UK rule (dash = "we inferred this", so it must never be used where nothing was inferred).

### 3.12 Systems checked with no stated guidance found

- **AWS Cloudscape Design System** — **not found.** Its data-vis pattern page (https://cloudscape.design/patterns/general/data-vis/) yielded zero matches on missing/no-data/null/gap, and its `llms.txt` (57 KB, the system's own LLM-facing doc digest) contains no missing-data entry. Cloudscape handles absence only as component-level empty/error states; its charts now wrap Highcharts, so any in-series behaviour is inherited from §3.6.
- **Datadog DRUIDS** — **not found (inaccessible).** DRUIDS has a public Dataviz & Graphing patterns section (https://druids.datadoghq.com/patterns) but the system is not open source and the detailed guidance is gated. Notable as a gap, since Datadog's product domain (monitoring agents that go offline) is the closest commercial analogue to Lattice's problem.
- **Salesforce Lightning Design System** — **not found.** No missing-data rule surfaced in its charts/data-visualization guidelines.

### Group 3 key takeaways

- **The strongest external precedent is candidate 5d, and it comes from a government statistics standard, not a UI design system.** The UK Government Analysis Function prescribes exactly blank-gap + two vertical dashed edge lines + annotation stating *why* the data is absent, and marks dashed *bridging* as wrong with the stated reason *"Joining points implies we know something about the data."* Carbon independently reaches the same place — *"Never interpolate… Always label both the start and end point"* — from a completely different tradition. Two independent sources converging on 5d is the single most actionable result in this group.
- **Carbon supplies a ready-made spec for 4c and quietly refutes 4a.** Its axis-break treatment (sinusoidal glyph replacing the straight axis line, 16px minimum, ticks never re-spaced to hide compression, *"leave the area empty"* inside the break) is directly implementable. Its "gap denoted by texture" alt text is stale metadata — the shipped illustrations use no hatching at all, so no design system in this survey actually endorses **4a**.
- **Three independent libraries document hole→zero as a real rendering outcome**, which promotes Lattice's stated failure mode from a perceptual worry to a mechanical one: Highcharts (`connectNulls: true` in stacked area ⇒ *"null points are interpreted as 0"*), Vega-Lite (`"show"` mode ⇒ *"the same visual values as zero"*), Observable Plot (`interval` + `sum` reducer *"defaults to zero instead of showing gaps"*). A stacked density band is structurally a stacked area chart — this is the exact configuration that bites.
- **The library defaults are mostly unsafe, and the one written rule from a UI design system is the wrong one for Lattice.** amCharts defaults to `connect: true` (bridges silently); Vega-Lite's v5 default is backward-compat rather than principled; only ECharts defaults to `connectNulls: false`. Meanwhile Adobe Spectrum states outright *"When data returns null (blank) values, a chart should treat these as zeros"* and GitLab Pajamas prescribes a dashed grey bridging line — both of which the UK guidance explicitly marks as errors. Worth citing both in the design doc as named, sourced anti-patterns rather than leaving the rejection implicit.
- **Two hole classes want two visual tokens, and there is precedent for that.** Urban Institute assigns *different glyphs* to missing versus masked values in its table conventions — the same structural distinction as Lattice's routine-unobserved versus exceptional-unreachable, and independent support for the red-dashed-frame treatment being a separate token rather than a modifier.
- **Two mechanics worth stealing that no design system states but every time-series library implements**: (a) holes should be *derived from expected cadence*, not from materialized null rows (Highcharts `gapSize`/`gapUnit`, amCharts `baseInterval`/`autoGapCount`) — and `autoGapCount` is prior art for a minimum hole duration below which you don't annotate, which 5c/5d will need to avoid annotation spam; (b) Observable Plot's null-row-vs-absent-row distinction means an unobserved interval must exist as a *record* in Lattice's store, since an absent row is indistinguishable from "no such time" at render time.
- Two smaller items for the edge cases: Spectrum's *"Do not render an empty chart"* applies to a host lane that is 100% unobserved (show the reason and a next action, not a blank lane), and its scatter-plot rule to state the excluded count in persistent chrome supports the duration-label half of 5c/5d. Datawrapper's recommendation to show symbols at every real data point is the clearest argument found for **5b** (a coverage track that makes observation events themselves visible).

**Coverage gaps, stated honestly:** `carbon-charts` library-level null defaults are unverified (GitHub code-search API rate-limited; no retry per instruction — this would be [product behavior] regardless). Datadog DRUIDS' dataviz guidance is gated and could not be read. Material Design's M2 and M3 sites are fully client-rendered and returned no text to `curl`, so the Material "not found" rests on search-surfaced content plus the M3 sitemap rather than raw-HTML verification.

## 4. Perception & accessibility checks on 5c

### 4.1 Contrast — does WCAG 1.4.11 apply to a grey "no data" fill?

**Verdict: yes, and this is the criterion's paradigm case, not an edge case.** But the strict adjacent-colour reading rules out any *muted* flat grey, which is a direct hit on 5c as specified.

#### The normative scope

**[official guidance]** [W3C, Understanding SC 1.4.11 Non-text Contrast](https://www.w3.org/WAI/WCAG22/Understanding/non-text-contrast.html) — the SC text, verbatim:

> "The visual presentation of the following have a contrast ratio of at least 3:1 against adjacent color(s): […] **Graphical Objects** — Parts of graphics required to understand the content, except when a particular presentation of graphics is essential to the information being conveyed."

Scoping test, verbatim:

> "Not every graphical object needs to contrast with its surroundings - only those that are required for a user to understand what the graphic is conveying."

And the testing procedure that decides it for us:

> "If the least-contrasting area is less than 3:1, assume that area is invisible, is the graphical object still understandable?"

Apply that literally to Lattice: assume the NOT-OBSERVED band is invisible. What remains is a band at zero on normal canvas — **which is exactly the "observed idle" encoding**. The document's own test therefore says the hole fill is required for understanding. This is not a marginal call; 1.4.11 applies with full force. (Bears on **5a** most sharply: pure blank + baseline breaks makes the "assume it's invisible" test trivially fail, because the treatment *is* mostly absence.)

The doc's charting examples confirm charts are in scope:

> "The graphical objects are the lines in the graph, including the background lines for the values, and the colored lines with shapes."
> "The lines should have 3:1 contrast against their background, but as there is little overlap with other lines they do not need to contrast with each other or the graduated lines."

That second sentence matters: **non-overlapping objects only need contrast against the background, not each other.** A stacked density band is the opposite case — the hole region butts directly against coloured project bands on both sides, so the adjacent-colour clause bites on both the canvas *and* the neighbouring project colours.

#### Two exemptions that reshape the design, in the criterion's own words

> "A graphic with text embedded or overlaid conveys the same information, such as labels and values on a chart."
> Pie note: "If the values of the pie chart slices were also presented in a conforming manner […] the slices would not be required for understanding."

**This inverts the naive intuition behind 5c.** 5c labels the hole "when wide enough" — but a labelled hole is *exempt*, and an unlabelled narrow hole is *exactly where the 3:1 obligation lands*. A design that puts its strongest treatment on wide holes and its weakest on narrow ones has the contrast budget backwards. Narrow, unlabelled holes need the **most** contrast, not the least.

The second lever, from the magnet worked example:

> "it would also be possible to only put the outline around the white tips of the magnet and it would still conform."

**A conforming 3:1 boundary stroke discharges the obligation without requiring the fill itself to reach 3:1.** This is the structural escape from the arithmetic below, and it is blessed in the normative Understanding document rather than being a workaround.

#### The arithmetic (computed locally; WCAG relative-luminance formula, sRGB)

Real Fluent token hexes, curled from source:
- [`packages/tokens/src/global/colors.ts`](https://raw.githubusercontent.com/microsoft/fluentui/master/packages/tokens/src/global/colors.ts) — the grey ramp
- [`packages/tokens/src/alias/lightColor.ts` / `darkColor.ts`](https://raw.githubusercontent.com/microsoft/fluentui/master/packages/tokens/src/alias/lightColor.ts) — alias mappings
- [`packages/charts/react-charting/src/utilities/colors.ts`](https://raw.githubusercontent.com/microsoft/fluentui/master/packages/charts/react-charting/src/utilities/colors.ts) — `QualitativePalette` slots 1–10

Note `#FAFAFA` is literally Fluent `grey[98]` = `colorNeutralBackground2` (light), so the light canvas is already on-token. `#1C1C1C` sits between `grey[10]` `#1a1a1a` and `grey[12]` `#1f1f1f` (= `colorNeutralBackground2` dark).

Contrast of grey ramp vs **light canvas #FAFAFA**:

| token | hex | CR |
|---|---|---|
| grey90 | `#e6e6e6` | 1.20 |
| grey88 (`colorNeutralStroke2`) | `#e0e0e0` | 1.26 |
| grey82 (`colorNeutralStroke1`) | `#d1d1d1` | 1.46 |
| grey80 | `#cccccc` | 1.54 |
| grey74 (`colorNeutralForegroundDisabled`) | `#bdbdbd` | 1.80 |
| grey70 | `#b3b3b3` | 2.01 |
| grey62 | `#9e9e9e` | 2.57 |
| **grey56** | **`#8f8f8f`** | **3.10** ← first to clear 3:1 |
| grey50 | `#808080` | 3.78 |

Contrast vs **dark canvas #1C1C1C**:

| token | hex | CR |
|---|---|---|
| grey20 | `#333333` | 1.35 |
| grey24 (`colorNeutralStroke3`) | `#3d3d3d` | 1.57 |
| grey30 | `#4d4d4d` | 2.02 |
| grey32 (`colorNeutralStroke2`) | `#525252` | 2.18 |
| grey36 | `#5c5c5c` | 2.55 |
| grey40 (`colorNeutralStroke1`) | `#666666` | 2.97 |
| **grey42** | **`#6b6b6b`** | **3.20** ← first to clear 3:1 |
| grey46 | `#757575` | 3.70 |

**The killer result.** Fluent's 10 base qualitative colours occupy a narrow luminance band, Y = 0.176 (`#b146c2`) to 0.283 (`#2aa0a4`). Brute-forcing all 256 neutral greys against the strict reading (3:1 vs canvas **and** 3:1 vs all ten project colours):

- Light canvas `#FAFAFA`: **only `#000000`–`#2c2c2c` pass** (45 of 256).
- Dark canvas `#1C1C1C`: **only `#fafafa`–`#ffffff` pass** (6 of 256).

So the only flat greys that strictly satisfy 1.4.11 in a stacked band are near-black on light and near-white on dark. Both are the single most salient thing on the chart — **the exact opposite of 5c's "solid muted grey."** A muted flat fill and strict adjacent-colour conformance are mutually exclusive here. You must pick one of: (a) the boundary-stroke route, (b) the text-label exemption, (c) a deliberately non-muted fill.

Related observation, worth knowing: Fluent's own palette is calibrated to clear 3:1 against its canvases but **not** against each other — all ten land 3.02–4.45 vs `#FAFAFA` and 3.67–5.41 vs `#1C1C1C`, while adjacent pairs within the palette sit far below 3:1. Microsoft's charting docs push that liability onto the consumer explicitly.

**[product behavior]** [Fluent charting `colors.md`](https://raw.githubusercontent.com/microsoft/fluentui/master/packages/charts/react-charting/docs/colors.md), verbatim: *"The users will be responsible for managing the contrast ratio between adjacent data series and adjusting the color in relation to the light and dark themes."* There is **no accessibility annotation on `DataVizPalette` at all** — no CVD statement, no contrast claim, no reserved "no data" slot. The only neutral in the whole system is `SemanticPalette.disabled: ['#dbdbdb', '#4d4d4d']` (= `grey[86]` / `grey[30]`), which computes to **1.33:1** on `#FAFAFA` and **2.02:1** on `#1C1C1C` — Fluent's own "disabled" grey fails 1.4.11 as a standalone fill on both themes.

#### Sub-3:1 guidance for region fills — NOT FOUND

Searches for an authoritative minimum ΔL or contrast floor for large region fills below 3:1 (W3C Low Vision Task Force, USWDS, gov guides) found **no such number in any reachable authoritative source.** Every source either restates 3:1 or declines to quantify. Government/institutional data-viz guidance is uniformly "3:1 against background and adjacent elements, plus a redundant non-colour encoding":

**[official guidance]** [USWDS Data visualizations](https://designsystem.digital.gov/components/data-visualizations/) — guidance-only, no component code; advises against reusing colours for different variables and treats difficulty choosing colours as a signal of too many concepts in one chart. **[official guidance]** [Digital.gov visual design](https://digital.gov/guides/accessibility-for-teams/visual-design/). **[official guidance]** [Harvard Digital Accessibility — Data Visualizations](https://accessibility.huit.harvard.edu/data-viz-charts-graphs): 3:1 against background *and* each adjacent element, and recommends **a solid border colour between adjacent data parts as an extra layer of distinction** — independent corroboration of the boundary-stroke route. **[official guidance]** [Missouri Complex Images guide](https://at.mo.gov/wp-content/uploads/complex-images-accessibility.pdf): when bar fills fail 3:1, either darken the fill or **apply a dark outline to each bar** — same conclusion. **[anecdote]** [University of Michigan brand](https://brand.umich.edu/design-resources/accessibility/) ships a data-viz palette tested for ≥3:1 between adjacent colours with a "Recommended Sequence" ordering, and explicitly notes their sequential ramps cannot reach 3:1 between neighbours — conceding the same tension.

#### Concrete recommendation (derived — computed, not cited guidance)

Split the conformance burden between a **stroke that carries 1.4.11** and a **fill that carries the muted aesthetic**:

| role | light canvas `#FAFAFA` | CR vs canvas | dark canvas `#1C1C1C` | CR vs canvas |
|---|---|---|---|---|
| hole boundary stroke (carries conformance) | `#808080` grey50 | **3.78** | `#757575` grey46 | **3.70** |
| minimum passing stroke if you want lighter | `#8f8f8f` grey56 | 3.10 | `#6b6b6b` grey42 | 3.20 |
| hole fill (muted, non-competitive) | `#d1d1d1` grey82 (`colorNeutralStroke1`) | 1.46 | `#3d3d3d` grey24 (`colorNeutralStroke3`) | 1.57 |
| alt. fill with more presence | `#cccccc` grey80 | 1.54 | `#4d4d4d` grey30 | 2.02 |

Do **not** use `#e0e0e0`/`#e6e6e6` (light) or `#333333` (dark) as the fill — at 1.20–1.35:1 they are within LSB-noise of the canvas at small widths and will read as nothing at all. Avoid `#bdbdbd` and darker as a *flat unstroked* fill on light: it starts colliding with CVD-simulated palette colours (§4.3).

---

### 4.2 Minimum noticeable region width

**Verdict: no direct research exists. The "3px minimum" is defensible as a *detection* floor but NOT as an *identification* floor, and it cannot support 5c's label, 4a's hatching, or the red dashed unreachable frame.**

#### Direct research — NOT FOUND

**No peer-reviewed study establishing a minimum rendered width for a region or gap to be reliably noticed in a chart or timeline was found.** This is a genuine gap in the literature, not a search failure. Nearest proxies below.

**[peer-reviewed research]** Heer, Kong & Agrawala, *Sizing the Horizon: The Effects of Chart Size and Layering on the Graphical Perception of Time Series Visualizations*, CHI 2009 — [PDF](https://idl.cs.washington.edu/files/2009-TimeSeries-CHI.pdf), [DOI 10.1145/1518701.1518897](https://dl.acm.org/doi/10.1145/1518701.1518897). Studies chart **height** and layering effects on value discrimination/estimation in time series. It is the canonical chart-sizing citation but addresses vertical resolution of a line, not horizontal detectability of a region. **Does not transfer** to hole width.

**[peer-reviewed research]** JND modelling in charts (IEEE VIS 2021) — [PDF](https://deardeer.github.io/pub/VIS21_LSD.pdf). Models the minimum difference in visual attributes permitting faithful comparison of similar chart elements, relating JNDs to visual-variable intensity. Closest framing to "minimum discriminable", still about magnitude comparison rather than region presence.

**[official guidance]** ISO 9241-303:2011 §5.5, verbatim: *"The minimum height of Latin characters shall be 16 arc minutes; it is required that the system is capable of providing a character height of 20 arc minutes to 22 arc minutes."* — [sample PDF](https://cdn.standards.iteh.ai/samples/57992/bddfd91165b444f6b9815a6993feadc5/ISO-9241-303-2011.pdf). ISO 9241-306 sets the design viewing distance at 400–750 mm (optimum ~600 mm).

**[peer-reviewed research]** Grether & Baker (1972), widely cited in human factors: complex shaped objects must subtend **≥20 arcmin and ≥10 resolution elements** to be distinguishable. Note this coincides numerically with the ISO cap-height figure but is a separate criterion.

**[anecdote]** Charting-library floors: ~1.5px hard minimums; practitioner "≥3px column" heuristics motivated by rounding error (a column rendering at 1 vs 2px is a silent 100% visual error). No empirical basis.

#### Analysis (computed)

Angular size at the ISO 600 mm design distance:

| rendered width | 96 dpi @1× | 96 dpi @2× | 110 dpi @2× |
|---|---|---|---|
| 1 device px | 1.52′ | 0.76′ | 0.66′ |
| **3 device px** | **4.55′** | **2.27′** | **1.98′** |
| 8 device px | 12.13′ | 6.06′ | 5.29′ |
| 12 device px | 18.19′ | 9.10′ | 7.94′ |
| 24 device px | 36.39′ | 18.19′ | 15.88′ |

Treating a band of width *w* arcmin as a grating half-period, spatial frequency = 30/*w* cycles/degree:

- 3px @1× (4.55′) → **6.6 c/deg** — near the peak of the human contrast sensitivity function (~3–5 c/deg). At adequate contrast this is comfortably **detectable**.
- 3px @2× (2.27′) → **13.2 c/deg** — meaningfully past peak; sensitivity is falling, so contrast must rise to compensate.
- 1px @2× (0.76′) → ~40 c/deg — approaching the acuity limit. **A 1px hole on a HiDPI display is effectively invisible.**

Conclusions bearing on the candidates:

1. **A 3px floor survives as a detection rule, with a caveat the rule as stated omits: it must be expressed in *device* pixels, not DIPs.** 3 DIPs at 2× is 6 device px (4.55′) and fine; 3 *device* px at 2× is 2.27′ and marginal. Get this wrong and the rule silently weakens on exactly the displays users have.
2. **Contrast must scale inversely with width.** Because sensitivity falls at high spatial frequency, a muted 1.46:1 fill that reads fine at 40px is near-invisible at 3px. A single flat grey across all widths is the wrong model — this is a second, independent argument (alongside §4.1's label exemption) that narrow holes need *more* treatment than wide ones. **Directly refutes 5c-as-specified.**
3. **3px cannot carry any internal structure.** Hatching (**4a**) needs several stripe cycles across the width; a dashed hairline frame (**5d**) needs several dash periods; the red dashed unreachable frame needs both. At 4.55′ total width none of these resolve — they alias into a flat smear of averaged colour, which is *worse* than a flat fill because the averaged colour is unpredictable. **Any patterned treatment needs a much larger minimum width than a flat one**, and below it must degrade to a solid colour rather than render a broken pattern.
4. **The 5c duration label has its own, much larger floor.** ISO's 16′ minimum cap height ≈ 2.79 mm ≈ **10.6 px cap height at 96 dpi @1×**, implying roughly a 15px font — larger than Fluent's 12px caption size. Combined with glyph count, "wide enough to label" is on the order of 40–60px, an order of magnitude above the 3px detection floor. There is a wide middle band (roughly 3–40px) where the hole is detectable but unlabelled — and by §4.1 that band is precisely where the 3:1 obligation is unexempted.

---

### 4.3 Colour-blindness / palette interaction

**Verdict: confirmed and quantified. Fluent's own palette produces a near-perfect neutral grey under deuteranopia. This is the strongest single argument against relying on grey alone.**

#### The mechanism

**[peer-reviewed research]** Confusion lines: for a dichromat, all colours on a confusion line in CIE 1931 chromaticity are indistinguishable, and every confusion line passes through the neutral point — so any low-chroma colour near that axis is *literally grey* to a dichromat. Protanopia additionally loses brightness in the red range. Deuteranopia collapses greens/yellows/oranges toward beige-tan, which is what gets confused with a grey swatch. See [Orthogonal Relations and Color Constancy in Dichromatic Colorblindness](https://www.ncbi.nlm.nih.gov/pmc/articles/PMC4161355/) and [A Novel Approach to Image Recoloring for Color Vision Deficiency](https://www.ncbi.nlm.nih.gov/pmc/articles/PMC8069325/).

Important calibration: only achromatopsia (~1 in 30,000) yields true greyscale vision. The grey-confusion risk is specific to **low-chroma palette entries near the confusion-line neutral point**, not global desaturation.

#### Measured on Fluent's actual palette (computed, Viénot–Brettel–Mollon 1999 simplified dichromat simulation)

Simulating the 10 base `QualitativePalette` slots and measuring residual chroma as sRGB channel spread max−min (0 = exactly neutral grey):

| slot | hex | protan | deutan | tritan |
|---|---|---|---|---|
| 1 cornflower | `#637cef` | `#7878ef` (119) | `#7b71ef` (126) | `#399191` (88) |
| **2 hot pink** | `#e3008c` | `#65658b` (38) | **`#8c8987` (5)** | `#df3333` (172) |
| **3 teal** | `#2aa0a4` | **`#9494a4` (16)** | `#8a86a6` (32) | `#27a1a1` (122) |
| **4 orchid** | `#9373c0` | `#7979c0` (71) | `#827dc0` (67) | **`#888080` (8)** |
| 5 green | `#13a10e` | `#949412` (130) | `#878620` (103) | `#409898` (88) |
| 6 blue | `#3a96dd` | `#8b8bdd` (82) | `#867ede` (96) | `#00a1a1` (161) |
| 7 pumpkin | `#ca5010` | `#727209` (105) | `#898900` (137) | `#cb4b4b` (128) |
| **8 lime** | `#57811b` | `#7b7b1c` (95) | `#757520` (85) | **`#627a7a` (24)** |
| 9 lilac | `#b146c2` | `#6363c2` (95) | `#7c76c0` (74) | `#a56262` (67) |
| 10 gold | `#ae8c00` | `#939300` (147) | `#999800` (153) | `#b48484` (48) |

**`DataVizPalette.color2` (`#e3008c`) simulates to `#8c8987` under deuteranopia — a channel spread of 5 out of 255.** That is a neutral grey to within rounding. Deuteranomaly/deuteranopia is the most common CVD (~6% of males). Slot 3 under protan (spread 16) and slot 4 under tritan (spread 8) are the same failure in other channels.

Consequence for 5c: since three of the ten project colours become effectively grey for some viewer, **hue cannot distinguish the "no data" grey from a project band.** Only **luminance** can. Cross-referencing the adjacency computation:

- Light canvas, fill `#adadad` or darker → collides with deutan-simulated `color2` `#8c8987` at CR 1.55 and below.
- Light canvas, fill `#9e9e9e` → also collides with tritan `color4` `#888080` (1.44) and protan `color3` `#9494a4` (1.11).
- Dark canvas, fill `#666666` or lighter → collides with tritan `color4` (1.49) and `color8` `#627a7a` (1.25).
- Dark canvas, fill `#707070` → additionally collides with deutan `color2` (1.43).

**The safe zone for a flat grey is therefore squeezed from both sides**: too light and it vanishes into the canvas; too dark (light theme) or too light (dark theme) and it merges with CVD-simulated project colours. `#d1d1d1`/`#cccccc` (light) and `#3d3d3d`/`#4d4d4d` (dark) sit in the surviving window — which is why the §4.1 recommendation lands there, and why it *needs* the boundary stroke to be legible.

#### Palette-designer guidance: grey as a reserved "bad data" slot

**[official guidance]** [Paul Tol, *Colour Schemes*, SRON/EPS/TN/09-002 issue 3.2](https://sronpersonalpages.nl/~pault/) — Tol reserves a grey in essentially every scheme, verbatim:

> Muted qualitative: *"Colours in default order: '#CC6677', '#332288', '#DDCC77', '#117733', '#88CCEE', '#882255', '#44AA99', '#999933', '#AA4499'. **Bad data: '#DDDDDD'.** […] Pale grey is meant for bad data in maps."*
> YlOrBr sequential: *"**Bad data: '#888888'.** […] The grey is meant for bad data."*
> Iridescent: *"**Bad data: '#999999'.**"* · Incandescent: *"**Bad data: '#888888'.**"*
> Diverging (sunset/nightfall): *"The circled colour is meant for bad data, **without drawing attention away from good data** with a large deviation from zero."*
> Discrete rainbow: *"**Bad data when 23 colours are used: '#777777'.**"*

Two things to extract. First, Tol's bright and vibrant schemes list grey `#BBBBBB` **inside the colour rotation** (7th slot) — so grey is only unambiguously "bad data" if you *remove* it from the series rotation. Lattice must do this explicitly: **never assign a neutral to a project.** Second, Tol's design intent for the bad-data colour is *"without drawing attention away from good data"* — i.e. downplayed. **This is in direct tension with Song & Szafir's empirical finding that highlighting absence works better (§4.4).** Tol is an aesthetic/cartographic convention; Song & Szafir is measured behaviour. Where they conflict, the measured result should win — Tol's schemes were designed for maps, where a bad-data cell is bounded by neighbours that make it obvious, not for a timeline where a hole is bounded by *nothing*.

**[peer-reviewed research]** [cols4all](https://cols4all.github.io/cols4all-R/articles/01_paper.html) quantifies the palette trade-off: the most colourblind-friendly palette in their selection, `tol.light`, scored min_dist 7.23 — *"a strong color difference, but too low to be labeled colorblind friendly."* Fairness and CVD-friendliness are noted as conflicting objectives. Useful sanity check: even the best-regarded 7-colour palettes do not clear their CVD bar, so a 10-colour Lattice palette will have grey-collision cases by construction.

**[product behavior]** Fluent's `DataVizPalette` — **NOT FOUND: no CVD/accessibility notes whatsoever.** [`colors.md`](https://raw.githubusercontent.com/microsoft/fluentui/master/packages/charts/react-charting/docs/colors.md) claims only *"Each qualitative color is distinct from the others"* with no substantiation, and pushes contrast management onto the consumer. Fluent provides **no reserved "no data" slot** — the closest, `SemanticPalette.disabled`, is a UI-state token, not a missing-data token, and fails contrast on both themes (§4.1).

#### Mitigation that sidesteps the mechanism entirely

Encoding "not observed" with a **pattern** rather than a colour is immune to confusion lines, since texture is not a chromatic channel. That is a real point in favour of **4a (hatching)** — but it collides head-on with §4.2's finding that patterns cannot render below ~10–15px. **Neither 5c nor 4a is safe alone; the width-adaptive combination is.**

---

### 4.4 Song & Szafir 2018 follow-ups

Source paper: **Song, H. & Szafir, D. A., "Where's My Data? Evaluating Visualizations with Missing Data", IEEE TVCG 25(1):914–924** (IEEE VIS 2018), [DOI 10.1109/TVCG.2018.2864914](https://doi.org/10.1109/TVCG.2018.2864914), [open PDF](https://cmci.colorado.edu/visualab/papers/song_VIS_2018.pdf). Full citation graph pulled via the Semantic Scholar API — **78 citing works**. Filtered below.

#### (a) Timeline / gantt / state-band visualisations — NO DIRECT WORK FOUND

**This is a genuine finding: across all 78 citing works there is no study of missing data in a timeline, gantt, or state-band visualisation.** The corpus is dominated by line/bar/scatter charts, parallel coordinates, matrix/heatmap plots and imputation-algorithm papers. The nearest proxies:

- **Jiménez, E. & Macías, R. (2022)**, *Graphical Tools for Visualization of Missing Data in Large Longitudinal Phenomena*, Computer Graphics Forum 41(1):438–452, [DOI 10.1111/cgf.14445](https://onlinelibrary.wiley.com/doi/abs/10.1111/cgf.14445). **[peer-reviewed research]** — **Closest structural match.** Ordering/sampling/grouping algorithms over **lasagna plots** (rows = subjects, x = time, cell colour = value), targeting *monotone vs intermittent* missingness patterns. A lasagna plot **is** a per-entity state band over time, so this is the nearest published idiom to Lattice's per-host timeline. Note the pattern taxonomy transfers directly: a permanent unobserved window is "monotone" missingness, a poll-gap is "intermittent" — different visual affordances are warranted.
- **Alemzadeh et al. (2020)**, *Visual Analysis of Missing Values in Longitudinal Cohort Study Data*, Computer Graphics Forum 39(1):63–75. **[peer-reviewed research]** — longitudinal missing-value analysis; cited in the survey rather than citing Song & Szafir directly.
- **Davidson, T., Wall, E. & Mace, J. (2023)**, *A Qualitative Interview Study of Distributed Tracing Visualisation: A Characterisation of Challenges and Opportunities*, IEEE TVCG, [DOI 10.1109/TVCG.2023.3241596](https://doi.org/10.1109/TVCG.2023.3241596). **[peer-reviewed research]** — **Closest domain match.** Distributed tracing renders span timelines (gantt-like) over instrumented systems that are only partly observed — the same "we weren't watching" epistemics as Lattice. Qualitative/interview, no controlled missingness encoding result.
- **Oppermann, M. & Munzner, T. (2020)**, *Ocupado: Visualizing Location-Based Counts Over Time Across Buildings*, CGF 39(3):127–138, [DOI 10.1111/cgf.13968](https://doi.org/10.1111/cgf.13968), [PDF](https://michaeloppermann.com/files/Ocupado_2020_Oppermann.pdf). **[peer-reviewed research]** — structurally near-identical problem (counts over time across a fleet of spatial units, WiFi sensors with downtime). **Its missing-data encoding could not be verified from available abstract-level material** — worth a direct read of the PDF/supplemental; their stated data-first methodology explicitly foregrounds "(in)completeness and (un)certainty".
- **Bäuerle, A. et al. (2022)**, *Where did my Lines go? Visualizing Missing Data in Parallel Coordinates*, CGF, [DOI 10.1111/cgf.14536](https://doi.org/10.1111/cgf.14536). **[peer-reviewed research]** — missing-data encoding in a multi-axis idiom; closest *design*-space contribution, wrong idiom.
- **Bernard, J. et al. (2019)**, *Visual-Interactive Preprocessing of Multivariate Time Series Data*, CGF, [DOI 10.1111/cgf.13698](https://doi.org/10.1111/cgf.13698). **[peer-reviewed research]** — time-series missingness in a preprocessing workflow.

#### (b) Routine absence vs exceptional absence

**This distinction is not directly studied either, but two works give it theoretical footing** — relevant to Lattice's split between an ordinary unobserved window (app closed) and an unreachable-host hole (red dashed frame).

- **Ben Shoshan, H., Lanir, J., Goldstein, P. & Mokryn, O. (2026)**, *Making Absence Visible: The Roles of Reference and Prompting in Recognizing Missing Information*, IUI '26, pp. 1100–1111, [DOI 10.1145/3742413.3789150](https://dl.acm.org/doi/10.1145/3742413.3789150), [arXiv:2601.07234](https://arxiv.org/abs/2601.07234). **[peer-reviewed research]** — **Most directly relevant follow-up.** Names **"presence bias"**: interfaces emphasise what is present and users' ability to detect absence depends on having a *reference frame* for what should be there. Result: **absence detection was significantly higher under a Partial (concrete exemplars) reference than a Global (unified baseline) reference.** For Lattice: a hole is detectable only against an expectation of coverage — which strongly favours **5b (a persistent coverage track)**, since a continuous track *is* the reference frame, present at all times, making the hole a contrast against a visible exemplar rather than an absence in a void. This is the single strongest empirical argument for adding 5b as a companion to 5c rather than choosing between them.
- **Ross, K., Sengupta, P. & Willett, W. (2024)**, *(Almost) All Data is Absent Data*, VISions of the Future workshop, IEEE VIS 2024, [program entry](https://ieeevis.org/year/2024/program/paper_w-future-1008.html), [PDF via PRISM](https://prism.ucalgary.ca/items/4c3b4cc6-8af2-47a8-a7f7-5a357c8185ca). **[peer-reviewed research]** (workshop, position paper) — contrasts "data voids" (missing regions *within* a structure, conventionally greyed out) against "data-in-a-void" (all collected data as a speck in an infinite field of the collectable). Position paper, no empirical result, but the framing is apt: Lattice's unobserved windows are structurally the second kind — the daemon keeps no durable history, so unwatched time is not a defect in a complete record, it is the normal condition. **Argues against treating holes as exceptional/alarming by default**, and thus for reserving the red dashed frame strictly for the unreachable case.
- **Sun, M. et al. (2021/2022)**, *Toward Systematic Considerations of Missingness in Visual Analytics*, IEEE VIS, [DOI 10.1109/VIS54862.2022.00031](https://doi.org/10.1109/VIS54862.2022.00031), [arXiv:2108.04931](https://arxiv.org/pdf/2108.04931). **[peer-reviewed research]** — a taxonomy of missingness in VA; the closest thing to a framework for typing absences.
- **Hengesbach, N., McInerny, G. & Albuquerque, J. (2022)**, *Seeing what is not shown*, Information Design Journal, [DOI 10.1075/idj.22006.hen](https://doi.org/10.1075/idj.22006.hen). **[peer-reviewed research]** — design-theory treatment of non-shown information.

#### (c) Replications, extensions, and contradictions of the core findings

Song & Szafir's core result (per the [state-of-the-art survey](https://arxiv.org/pdf/2410.03712), verbatim): *"highlighting the missing data and local linear imputation resulted in higher perceived confidence and data quality, while those that break the visual continuity of a graph reduce the perception of quality in data and can bias interpretation."*

- **Song, H., Fu, Y., Saket, B. & Stasko, J. (2021)**, *Understanding the Effects of Visualizing Missing Values on Visual Data Exploration*, IEEE VIS 2021, pp. 161–165, [DOI 10.1109/VIS49827.2021.9623328](https://doi.org/10.1109/VIS49827.2021.9623328), [PDF](https://faculty.cc.gatech.edu/~john.stasko/papers/vis21-missvalues.pdf). **[peer-reviewed research]** — **Direct extension by the same lead author, and the strongest confirmation.** Moves from static chart reading to interactive exploration (Baseline vs Error-bars scatterplots). Result: visually representing missing values **encourages reasoning about data quality, yields more consistent and regular decision-making, and increases confidence.** Supports showing the hole explicitly (5c/5b/4a) over hiding it (5a).
- **Sarma, A., Guo, S., Hoffswell, J., Rossi, R., Du, F., Koh, E. & Kay, M. (2022)**, *Evaluating the Use of Uncertainty Visualisations for Imputations of Data Missing At Random in Scatterplots*, IEEE TVCG 29(1):602–612 (VIS 2022 Honorable Mention), [DOI 10.1109/TVCG.2022.3209348](https://doi.org/10.1109/TVCG.2022.3209348), [PDF](https://mucollective.northwestern.edu/files/2022-uncertainty-vis-for-imputations.pdf), [OSF](https://osf.io/q4y5r/). **[peer-reviewed research]** — **The closest thing to a tension, though not a stated contradiction.** Compared six imputation-uncertainty encodings across 202 participants. Findings: for estimating averages, uncertainty representations **may reduce bias at the cost of decreased precision**; only HOPs showed a small chance of reducing bias while increasing precision; and **participants in every uncertainty condition reported being *less* certain than baseline.** That last point cuts against a naive reading of Song & Szafir's "highlighting raises confidence" — here, making uncertainty visible *lowered* self-reported confidence. **Caveat, stated plainly: no source found claims these papers contradict each other; the tension is inferred, and the constructs differ (perceived data quality vs. self-reported response certainty).** Verify against Sarma et al.'s related-work section before citing it as a contradiction.

  For Lattice this tension is actually reassuring rather than troubling: Lattice performs **no imputation at all**. Both papers agree that *imputed* values presented smoothly are the danger; Lattice's "never backfilled/interpolated" rule sidesteps the entire contested region. The relevant transfer is only the highlight-vs-downplay axis, where Song & Szafir 2018 and Song et al. 2021 agree.

- **Sun, M., Wang, Y., Bolton, C. & Ma, Y. (2024)**, *Investigating User Estimation of Missing Data in Visual Analysis*, Graphics Interface, [DOI 10.1145/3670947.3670977](https://doi.org/10.1145/3670947.3670977). **[peer-reviewed research]** — empirical study of how users *estimate* what is missing. Directly relevant to the "hole read as idle" failure: users fill gaps with inference whether or not you ask them to.
- **Alsufyani, S., Forshaw, M. & Johansson, S. (2024)**, *Visualization of missing data: a state-of-the-art survey*, [arXiv:2410.03712](https://arxiv.org/html/2410.03712v1). **[peer-reviewed research]** — the field survey; its taxonomy axes are numerical / categorical / **temporal** / other, and its own stated gap is verbatim: *"Current work mainly addresses numerical, categorical or time series data, while less work is done on missing values in, e.g., networks, geometric or heterogeneous data."* Note that even the survey's "temporal" bucket means time-series line charts, not state bands — **independent corroboration of the (a) gap.**
- **Fernstad, S. & Johansson Westberg, J. (2020)**, *To Explore What Isn't There — Glyph-Based Visualization for Analysis of Missing Values*, IEEE TVCG, [DOI 10.1109/TVCG.2021.3065124](https://doi.org/10.1109/TVCG.2021.3065124), [arXiv PDF](https://arxiv.org/pdf/2011.12125). **[peer-reviewed research]** — glyph encodings for missingness structure.
- **McNutt, A., Kindlmann, G. & Correll, M. (2020)**, *Surfacing Visualization Mirages*, CHI 2020, [DOI 10.1145/3313831.3376420](https://doi.org/10.1145/3313831.3376420). **[peer-reviewed research]** — frames the general class where a chart reads as meaningful but is an artefact of the pipeline. A hole misread as idle is textbook.
- **Sultanum, N., Bromley, D. & Correll, M. (2024)**, *Data Guards: Challenges and Solutions for Fostering Trust in Data*, IEEE VIS, [DOI 10.1109/VIS55277.2024.00019](https://doi.org/10.1109/VIS55277.2024.00019). **[peer-reviewed research]** — trust formation around data quality signals.

#### Bonus: what Fluent itself does with gaps

**[product behavior]** Fluent's own charting library ships a `gaps` feature on `LineChart` ([`LineChartGaps.stories.tsx`](https://raw.githubusercontent.com/microsoft/fluentui/master/packages/charts/react-charts/stories/src/LineChart/LineChartGaps.stories.tsx), rendering logic at [`LineChart.base.tsx`](https://raw.githubusercontent.com/microsoft/fluentui/master/packages/charts/react-charting/src/components/LineChart/LineChart.base.tsx) `_checkInGap`, guarded by `if (!isInGap)` at the draw sites). The implementation is: **skip drawing the segment entirely.** No fill, no pattern, no annotation, no label, no legend entry. Fluent's shipped answer to a gap is **candidate 5a in its purest form** — precisely the treatment Song & Szafir 2018 measured as reducing perceived data quality and biasing interpretation. Adopting Fluent's default here would be inheriting an unexamined choice, not a designed one.

---

### Group 4 key takeaways

- **WCAG 1.4.11 applies, and its own test condemns the failure mode.** "Assume the area is invisible — is the object still understandable?" An invisible hole in Lattice reads as observed-idle, so the hole fill is unambiguously "required for understanding." But strict conformance is unsatisfiable by a *muted* flat grey: brute-forcing all 256 neutrals against 3:1-vs-canvas **and** 3:1-vs-all-ten-project-colours leaves only `#000000`–`#2c2c2c` on the light canvas and `#fafafa`–`#ffffff` on the dark. **Take the escape route the Understanding doc explicitly blesses (the magnet-outline example): a 3:1 boundary stroke — `#808080` light (3.78:1), `#757575` dark (3.70:1) — carries conformance while the fill — `#d1d1d1` light (1.46:1), `#3d3d3d` dark (1.57:1) — stays muted.**
- **5c has its contrast budget backwards, on two independent grounds.** WCAG exempts graphics whose information is also conveyed by overlaid text — so a *labelled* wide hole is exempt while a *narrow unlabelled* one is not. And the contrast sensitivity function says a fill that reads fine at 40px is near-invisible at 3px. Both say the same thing: **narrow holes need MORE treatment than wide ones, not less.** 5c's "label when wide enough, plain grey otherwise" inverts this.
- **`DataVizPalette.color2` (`#e3008c`) simulates to `#8c8987` under deuteranopia — channel spread 5/255, a neutral grey.** Slot 3 under protan (16) and slot 4 under tritan (8) fail the same way. Hue therefore cannot separate "no data" from "some project"; only luminance can, and the safe luminance window is squeezed from both sides (canvas above, CVD-simulated colours below). Fluent ships **no** accessibility notes and **no** reserved no-data slot on `DataVizPalette`; its `SemanticPalette.disabled` (`#dbdbdb`/`#4d4d4d`) fails 1.4.11 on both themes. Follow Tol: **reserve a neutral and never assign it to a project.**
- **The "3px minimum hole width" rule survives — as a *detection* floor only, and only if specified in device pixels.** No research establishes a minimum region width (a real literature gap); the nearest anchors are ISO 9241-303's 16 arcmin character minimum and Grether & Baker's 20 arcmin symbol threshold, both far above 3px @1× (4.55′). Three separate floors exist and should be modelled separately: **~3 device px to detect a luminance step, ~10–15px before hatching or a dashed frame resolves rather than aliases, ~40–60px before an ISO-legible duration label fits.** 4a and 5d must degrade to a solid fill below the pattern floor.
- **No published work exists on missing data in timeline / gantt / state-band visualisations** — verified across all 78 works citing Song & Szafir, and independently corroborated by the 2024 field survey's own stated gap. Nearest proxies are Jiménez & Macías (2022) on lasagna plots (structurally closest; their monotone-vs-intermittent taxonomy maps onto Lattice's permanent-hole-vs-poll-gap distinction) and Davidson et al. (2023) on distributed-tracing timelines (closest domain). **Lattice is designing in genuinely uncharted space; the machine gate and the owner's eye carry more weight here than usual.**
- **The strongest empirical steer is toward pairing 5c with 5b, not choosing between them.** Ben Shoshan et al. (IUI 2026) name "presence bias" and show absence detection improves significantly when users have a *concrete* reference frame rather than an abstract baseline — a persistent coverage track (5b) *is* that reference frame. Song et al. (2021) independently confirm that showing missingness improves reasoning and consistency. Note the one live tension to resolve by owner judgement: **Tol designs bad-data greys to be downplayed ("without drawing attention away from good data"), while Song & Szafir measured that highlighting absence works better.** Tol's convention comes from maps, where neighbouring cells make a hole self-evident; on a timeline a hole is bounded by nothing, so the measured result should win.

## 5. Comparison table (findings → candidate ids)

Legend: **S** = supports, **C** = contradicts, **N** = neutral/conditional. Only load-bearing findings are tabulated; see the group sections for the full evidence.

| Finding (source, evidence class) | 4a hatch | 4b inverse tint | 4c axis break | 5a pure blank | 5b coverage track | 5c grey band | 5d blank+dashed+label | red frame (unreachable) |
|---|---|---|---|---|---|---|---|---|
| UK Gov Analysis Function: gap + two dashed edge lines + reason annotation is the shown-correct treatment; bridging (even dashed) marked wrong — "Joining points implies we know something" [official] | — | C ("must always be highlighted") | — | N (blank alone lacks the required highlight) | — | N (highlight ✓, but their mark is edges+text, not fill) | **S (near-verbatim)** | — |
| IBM Carbon: "Never interpolate… Always label both the start and end point"; axis break = sinusoidal glyph, 16px min, "leave the area empty" [official] | C (illustrations show no texture; alt-text stale) | — | **S (ready-made spec)** | N (blank ✓ but unlabeled fails "label both endpoints") | — | N | **S** | — |
| Azure Monitor: "NULL is different than a zero value"; dashed line = missing; for sum/count the dash **drops to zero** (ships Lattice's failure mode) [official] | — | — | — | C (zero-silhouette = idle-silhouette hazard, stated in Microsoft's own docs) | — | S (argues for a positive mark off the zero baseline) | S (dash = missing is the Microsoft idiom) | — |
| Fluent charting: gaps prop = draw nothing; canonical sample overlays a **dashed** series + footnote + own legend entry [official + source] | C (no Fluent hatch precedent) | — | — | S (shipped default) | — | N | **S (dashed + annotation + legend entry)** | — |
| Fluent AreaChart has NO gap support; no Fluent 2 data-viz page exists [source-verified absence] | N | N | N | N | N | N | N | N (no design-system cover for any candidate on this chart form) |
| Dev Home: right-aligns observed series, bare canvas inside an always-drawn frame; no zero-padding [source] | — | — | — | **S** | — | — | S (frame-delimits-hole half) | N (error is whole-widget, out-of-canvas) |
| perfmon: outage = literal gap **plus** "a message appears describing this" [official] | — | — | — | N (gap ✓ but message required) | — | S (label half) | **S** | — |
| Grafana State Timeline: null = nothing drawn, no null color token exists; "no data as a state" only via explicit user value-mapping; insertNulls default threshold 1 h [official + source] | — | — | — | **S (renderer-level default)** | — | N (opt-in named-state path = 5c's shape) | — | — |
| Home Assistant: `unavailable` → `transparent`, `unknown` → opaque `#606060` both themes; two hole classes, two treatments [source] | — | — | — | S (unavailable half) | — | S (unknown half) | S (blank-but-interrogable tooltip) | **S (two-tier precedent)** |
| Statuspage: no-data day = flat `#EAEAEA` + tooltip "No data exists for this day." distinct from "No downtime recorded on this day."; stripes reserved for **outage severity** [shipped code] | **C (hatch semantically taken)** | — | — | — | — | **S (grey + naming words)** | — | N (severity gets the loud texture, consistent w/ red=exceptional) |
| Netdata: gap is a typed storage value ≠ zero ("report 0%, not a gap"); ships an **Info ribbon (gaps, resets, partial data)** under the plot [official + source] | — | — | — | — | **S (shipping 5b)** | — | — | — |
| Datadog: interpolates by default; `default_zero()` documented as the way gaps vanish; bars recommended to reveal gaps [official] | — | — | — | C (zero-baseline hazard) | — | — | — | — |
| New Relic: separates *loss of signal* from *gap*, expiring the distinction on a timeout [official] | — | — | — | — | — | — | — | **S (typed distinction precedent)** |
| BOINCTasks: no gap representation anywhere; boinctasks-js has zero chart code [source + official absence] | N (any candidate is a differentiator) | N | N | N | N | N | N | N |
| Perfetto: unmarked "huge temporal gap … when nothing happened"; loss surfaced only in stats counters [official] | — | — | — | C (its ambiguity is documented in its own docs) | S (out-of-band channel, but stats-only shown insufficient) | — | — | — |
| GitLab Pajamas: dashed grey **bridging** line for gaps [official] | — | — | — | — | — | N (muted-grey instinct) | C to its *bridging* form only — UK guidance red-crosses exactly this | — |
| Adobe Spectrum: "treat null as zeros" (line/bar/histogram) = codified anti-pattern; area chart: plot and label unknown region; "do not render an empty chart" [official] | — | — | — | C | — | S (area-chart labeled-unknown-region) | S (label duty) | — |
| ECharts: "note the difference between the empty data and 0"; only mainstream lib defaulting to no-connect [official, thin] | — | — | — | S | — | — | — | — |
| Vega-Lite invalid-data taxonomy: `break-paths-show-domains` (≈5a) vs `break-paths-filter-domains` (≈4c) as named modes; "show" mode = same visual as zero [product] | — | — | S (named mode) | S (named mode) | — | — | — | — |
| Urban Institute: missing vs masked get **different glyphs**; duty to explain absences [official] | — | — | — | — | — | S (explain duty) | S | **S (distinct-glyph precedent)** |
| Datawrapper: keep gaps; symbols on real data points; dash = *assumed* data only, never unknown [official] | — | — | — | S | **S (make observation visible)** | — | N (its dash means "modelled", adjacent-not-identical to 5d's edge dashes) | — |
| WCAG 1.4.11: applies (invisible-hole test fails → hole is "required for understanding"); labeled graphic exempt; 3:1 boundary stroke discharges conformance; muted flat grey cannot strictly conform in a stacked band [official + computed] | N (pattern is a non-chromatic channel: S for CVD, but must resolve) | C (bare hole = invisible-test failure) | — | **C (invisible-test failure is 5a's definition)** | S (redundant channel) | **C as specified / S if fill+3:1 stroke+label** | S (edge hairlines ≈ the blessed outline route, if ≥3:1) | N (red dash must also clear 3:1 and the pattern floor) |
| Perception floors (computed vs ISO/CSF): ~3 *device* px detect / ~10–15 px pattern / ~40–60 px label [derived] | C below ~10–15px (aliases) | — | S (collapse rescues sub-3px holes) | N | S (track has fixed height, immune to hole width) | N (fill readable only above detection floor; label floor 40–60px) | C below pattern floor unless it degrades to solid | C below pattern floor unless it degrades to solid |
| CVD: Fluent palette slot 2 → grey `#8c8987` under deutan (spread 5/255); Tol reserves grey for "bad data" but removed from rotation [computed + official] | S (texture immune to confusion lines) | — | — | — | S (position, not color, carries meaning) | N (grey must be luminance-separated from CVD-greyed project colors; reserve the neutral) | S (dash carries meaning without hue) | C if red-only (protan dims red); needs a non-hue channel too |
| Song & Szafir 2018 + Song et al. 2021: highlighting missingness → better quality reasoning, more consistent decisions; breaking continuity unmarked → biased reading [peer-reviewed] | S (highlight family) | — | — | **C** | S | **S** | S | — |
| Ben Shoshan et al. IUI 2026 "presence bias": absence detection needs a concrete reference frame [peer-reviewed] | — | S (tint = reference frame, in principle) | — | C | **S (strongest single result)** | N | N | — |
| Ross et al. 2024: routine absence should not be alarming by default [peer-reviewed, position] | C (warning-flavored) | — | — | — | — | S (muted = calm) | S | **S (reserve alarm for the exceptional class)** |
| No published research on missing data in timeline/state-band idioms (78 citing works + survey checked) [verified absence] | N | N | N | N | N | N | N | N (owner's eye + machine gate carry extra weight) |

**Net tally by candidate** (official-guidance and source-verified findings weighted above anecdotes):

- **4a hatched**: contradicted 4× (Carbon's shipped visuals, Statuspage semantics, no Fluent precedent, aliasing floor); supported only by the CVD pattern-channel argument. **Weakest candidate.**
- **4b inverse tint**: no positive precedent found in any group; fails UK "must always be highlighted" and the WCAG invisible-test on the hole side; the presence-bias logic gives it its only (theoretical) point. **Effectively unsupported.**
- **4c axis break**: well-specified by Carbon and named in Vega-Lite; Highcharts/amCharts supply threshold prior art. But nothing in the monitoring group ships it, and it sacrifices true-width duration reading. **Viable optional extension, not a base treatment.**
- **5a pure blank**: the industry default (Grafana/HA-unavailable/Perfetto/Fluent/Dev Home) — but every "default" citer treats holes as *rare*, and the empirical literature (Song & Szafir line), Microsoft's own Azure sum-drops-to-zero documentation, and WCAG's invisible-test all condemn it for Lattice's routine-hole, zero-meaningful stacked band. **Best-precedented, worst-fitting.**
- **5b coverage track**: ships in Netdata; strongest single empirical result (presence-bias reference frame); immune to hole-width and CVD problems; ONS/Datawrapper adjacent support. **Strongly supported as a companion channel; no source treats it as sufficient alone.**
- **5c grey band**: supported by the two products whose hole cadence matches Lattice's (Statuspage, HA-unknown), by Spectrum's labeled-unknown-region, by Tol's reserved grey, and by the highlight-beats-hide literature — but **contradicted as literally specified**: every Fluent neutral background token is 1.1–1.2:1 against canvas (invisible), strict 1.4.11 is unsatisfiable by any muted flat grey in a stacked band, and the label-when-wide rule puts the least treatment where the most is needed. **Right family, wrong spec — needs stroke + label + width-adaptive contrast.**
- **5d blank + dashed hairlines + annotation**: the strongest official-guidance convergence of any candidate (UK Gov near-verbatim, Carbon both-endpoints rule, Azure dash-idiom, Fluent's own gaps sample, perfmon's gap+message). Constraint: dashes/hairlines alias below ~10–15px and the interior blank still needs the baseline break to avoid idle-reading. **Co-leader with modified 5c.**
- **Red dashed frame (unreachable)**: two-tier precedent is solid (HA, New Relic, Urban's distinct glyphs); Ross et al. supports reserving alarm for this class; Statuspage's loud-texture-for-severity is consistent. Constraints: protan red-dimming means red-alone is insufficient; dashes need the pattern floor. **Supported with the same width-degradation and non-hue-redundancy caveats.**

## 6. Recommendation

**Primary: a 5c/5d fusion — a solid muted grey band carrying a ≥3:1 boundary treatment, with a duration label above the label floor — with 5b (a thin coverage track) as a strongly recommended companion. Confidence: moderate-high on the fusion, high on rejecting 4a/4b/5a-alone, moderate on 5b's cost/benefit.**

The evidence, compressed to its convergence points:

1. **The hole must be a drawn, positive mark, not an absence.** Four independent lines agree: WCAG 1.4.11's own test ("assume the area is invisible — is the graphic still understandable?" — no: it reads as idle); Song & Szafir 2018 + Song et al. 2021 (highlighting missingness improves quality reasoning; unmarked continuity breaks bias interpretation); Azure Monitor's documented sum-aggregation failure (dash drops to zero = hole looks idle — Microsoft ships Lattice's failure mode and documents user pain); and the routine-vs-rare split in group 2 (the only products whose hole cadence matches Lattice's — Statuspage, HA-unknown, Netdata — all *draw something*, while the blank-by-default products treat holes as anomalies). This rejects 5a alone and 4b.
2. **The mark should be calm grey + words, not texture.** Hatching has no Fluent precedent, is refuted by Carbon's shipped visuals, is semantically spent on *severity* by the one product that ships it (Statuspage), and aliases below ~10–15px. Grey-with-naming-words is what Statuspage (`#EAEAEA` + "No data exists for this day."), HA-unknown (`#606060`), Spectrum's area-chart rule ("plotted and labeled as 'null' or 'unknown'"), and Tol's reserved bad-data grey all converge on. Ross et al. adds the product-fit argument: routine absence should read as calm, and loudness stays reserved for the exceptional unreachable class.
3. **But 5c exactly as sketched fails on physics.** No Fluent neutral background token is visible against the canvas (all ≈1.1–1.2:1, both themes); strict 1.4.11 adjacent-color conformance is unsatisfiable by *any* muted flat grey inside a 10-color stacked band; and both WCAG's label exemption and the contrast-sensitivity function say narrow holes need *more* treatment while 5c gives them less. The repair is exactly the route the WCAG Understanding doc blesses (the magnet-outline example) and the UK guidance draws (edge lines + annotation): **keep the muted fill for the region identity, put the 3:1 conformance in the boundary hairlines, and put the semantics in words** (duration label when it fits; tooltip + accessible name always — Statuspage's tooltip and Fluent's invisible ARIA alert are the precedents). This is no longer "5c vs 5d" — the evidence-supported design is their union: **muted fill (5c's body) + edge treatment (5d's hairlines, ≥3:1) + duration/reason annotation (both)**.
4. **Width-adaptive by rule, not taste.** Three separate floors emerged: ~3 *device* pixels (not DIPs) to detect a luminance step; ~10–15px before any dash/hatch pattern resolves instead of aliasing; ~40–60px before an in-band duration label is legible. So the band's treatment must degrade deterministically: below the pattern floor → solid fill + solid (not dashed) edge hairlines; above the label floor → add the in-band duration text. This rescues the "min 3px hole width" rule (as a detection floor, in device pixels) and settles how narrow holes stay honest: the *fill+edges* carry them, and the tooltip carries the words.
5. **5b is the best-evidenced enhancement and the natural home for the long-run story.** The strongest single empirical result found (Ben Shoshan et al., IUI 2026: absence detection requires a concrete reference frame) is architecturally an argument for a persistent coverage track; Netdata ships exactly this (Info ribbon under the plot); ONS ("make the cadence visible") and Datawrapper (symbols on real observations) point the same way; and the track is immune to the hole-width and CVD problems because *position on a dedicated strip*, not color, carries the meaning. It also cleanly holds per-host baseline-break information in multi-host lanes. Recommended as a companion surface, not a replacement — no source treats an out-of-band channel alone as sufficient (Perfetto's stats-only approach is the documented cautionary tale).
6. **Unreachable holes: same band grammar, distinct second channel.** The two-tier precedent is solid (HA's transparent-vs-`#606060` split; New Relic's loss-of-signal vs gap; Urban's different-glyphs-for-different-absences). The red dashed frame is directionally right (severity gets the loud treatment — Statuspage agrees) with two evidence-driven caveats: red alone fails protan viewers (pair it with a non-hue cue — the dashed texture itself, an icon, or the label wording), and the dash pattern needs the ≥10–15px floor with a solid-edge degradation below it.
7. **4c (axis-break collapse) is a defensible later add-on, not the base.** Carbon provides a complete spec (sinusoidal glyph, 16px minimum, declare-the-compression rule) and Grafana's 1 h default threshold + Highcharts/amCharts `gapSize`/`autoGapCount` give prior art for the trigger parameter. But no monitoring product ships it, it sacrifices the at-a-glance "how long was the machine unobserved" reading that true-width holes give, and Lattice's nightly holes are precisely the ones users will want to see at true scale. Revisit only if real usage shows long holes crowding out observed data.

**Open questions the evidence cannot settle** (for the Claude Design session / owner):

- **Token blessing.** The computed-safe stroke greys (`#808080`/`#757575`-class) live in Fluent's *global grey ramp*, not behind a semantic alias; the one FluentAvalonia named resource clearing 3:1 in both themes is `SystemFillColorSolidNeutral` (`#8A8A8A`/`#9D9D9D`). Whether "Fluent tokens only" means named semantic resources (→ `SystemFillColorSolidNeutral` + `SystemFillColorSolidNeutralBackground`) or permits grey-ramp values is a design-contract decision.
- **Highlight strength.** Tol's downplay-the-bad-data convention vs Song & Szafir's highlight-absence result — the report argues the measured result should win on a timeline (holes are bounded by nothing), but exact salience is an owner-eye call on real renders.
- **5b's cost.** The coverage track spends vertical space in already-dense multi-host lanes; nothing in the evidence quantifies that trade.
- **Label floor typography.** ISO-derived 40–60px assumes ~15px text; Fluent caption sizing and actual duration-string lengths ("8.2h" vs "2d 14h") need a rendering test, not more research.
- **Genuinely uncharted territory.** No published study covers missing data in state-band/timeline idioms (verified across Song & Szafir's 78 citers + the 2024 survey). The machine gate and owner's eye carry more weight than usual; a small self-run falsification (can a viewer distinguish hole/idle/running in both themes at 3px/15px/60px?) would be the first data of its kind.

## 7. Hard-constraint flags

- **"Fluent tokens only" is under real tension and needs one explicit carve-out.** Finding: *no* Fluent 2 neutral-background token is perceptible against the chart canvas (1.1–1.2:1, both themes), and Fluent's `SemanticPalette.disabled` fails too. The honest fills/strokes exist *inside* Fluent's grey ramp (`grey50`/`grey46`-class) and in FluentAvalonia's `SystemFillColorSolidNeutral(Background)` keys — on-system but, for the ramp values, not behind a semantic alias. The design contract must either bless specific ramp values or standardize on the two named `SystemFill*` keys. Also: do **not** use `colorNeutralStencil1/2` (Skeleton = "content is coming" — asserts the opposite of a permanent hole) and do **not** use the alpha `ControlFillColorDisabled` variants (composite invisibly over a canvas).
- **Light + dark parity.** HA's single `#606060` for both themes is a working counterexample to per-theme tuning but sits below 3:1 against Lattice's dark canvas ambitions; every fill/stroke in the contract needs per-theme values with computed ratios (starting set in §4.1's table). The dark theme is the harder side: the first ramp grey clearing 3:1 on `#1C1C1C` is `#6b6b6b`.
- **Deterministic machine-gated PNG snapshots.** Three interactions to pin in the contract: (1) the width-adaptive degradation ladder must be a pure function of rendered hole width in *device pixels* (detect / pattern / label floors), or snapshots will flap across DPI configurations — this is also exactly the kind of policy-module logic Lattice already extracts and transition-table-tests; (2) dash/hatch patterns below their floor alias into unpredictable average colors — the ladder must forbid patterned rendering below ~10–15px, which incidentally makes narrow-hole snapshots *more* stable; (3) LiveCharts2 expresses 5a-style gaps natively (nullable coordinates) but the band/hairline/label layer is custom draw work — the snapshot gate applies to that custom layer, and no animated brush (skeleton-shimmer-style) may appear in it.
- **Never fabricate/interpolate.** The strongest cross-cutting finding: this rule must live in the **data model**, not the paint layer. Grafana (`insertNulls`, the `plusone` anti-forward-interpolation mode), Netdata (`storage_point_is_gap` ≠ `storage_point_is_unset`, "report 0%, not a gap"), Observable Plot (absent row ≠ null row), and Vega-Lite (impute is an explicit named transform, never a default) all converge: **an unobserved interval must be a typed, materialized value in Lattice's store** — an absent row is indistinguishable from "no such time" at render time, and a stacked-area renderer fed absence will sit at zero (Highcharts documents `connectNulls:true` interpreting stacked nulls *as* 0). Two named anti-patterns to cite in the design doc: Adobe Spectrum's "treat null as zeros" and Datadog's `default_zero()`. One trap to test against: ordinal/collapsed axes silently deleting holes (Highcharts `xAxis.ordinal` precedent).
- **Accessibility rider (not in the original constraint list but load-bearing):** Fluent's own empty-chart pattern guarantees a screen-reader statement of absence (`role="alert"`, "Graph has no data to display"). Whatever wins visually, each hole region needs an accessible name ("not observed, 8.2 h") or the hole/idle distinction exists only in pixels — and WCAG 1.4.11 compliance rides on the boundary treatment (≥3:1) plus the label/tooltip exemption, per §4.1.

---

# Addendum: candidate 5e — dashed connector across the hole (owner-proposed, 2026-08-02)

**Candidate 5e:** in the unobserved region, do NOT draw the solid band/fill; instead connect across the blank with a DASHED line where the solid band edge would be. Evaluated with the same evidence standards as the main report; new targeted evidence gathered by one Opus subagent (Song & Szafir primary source, dash-semantics literature, forecast-convention style guides, product sweep for dash-across-gap), plus re-examination of evidence already in groups 1–3.

## A. Sub-variants that materially differ

- **5e-i — dashed top-edge connector**: a dashed segment from the last observed band height to the next observed band height (a trajectory through the hole).
- **5e-ii — dashed outline of the hole region**: dashes trace the hole's boundary (edges and/or a box), no trajectory drawn.
- **5e-iii — flat zero-height dashed rule**: the baseline itself turns dashed across the hole; no value is drawn anywhere in the hole.

These have different epistemics: 5e-i asserts a *path* through unobserved time; 5e-ii marks a *region*; 5e-iii restyles the *axis*. Sources are applied per-variant below.

## B. New and re-examined evidence

### B.1 What a dashed gap-spanning line officially *means* — the semantics are taken, and they mean "estimate"

- **[official guidance]** UK Government Analysis Function, Line charts → "Textured lines" (https://analysisfunction.civilservice.gov.uk/policy-store/data-visualisation-charts/) — a sentence not captured in the main report, and it names the collision outright:
  > "Textured lines are dotted or dashed lines. They are sometimes used to differentiate lines. We advise caution when using textured lines. They can make a chart cluttered. **They may be misinterpreted as showing incomplete, forecasted or provisional data, a target or a subcategory.**"

  The same authority whose never-join rule anchors the main report states that dash is a five-way-ambiguous channel. And its discontinuity rule already adjudicated 5e-i by name: *"If you do use a line, **do not join the points either side of the missing data point, even if the line is dotted or dashed.** Joining points implies we know something about the data."* — **the red-crossed "don't" example in the AF's own worked pair IS candidate 5e-i.**
- **[product behavior]** Google Charts ships exactly the 5e-i rendering — and its API name for it is **`certainty`** (https://developers.google.com/chart/interactive/docs/roles): *"Indicates whether a data point is certain or not. … it might be indicated by dashed lines or a striped fill. For line and area charts, the segment between two data points is certain if and only if both data points are certain."* Note the semantics are **not absence**: a `certainty:false` point still has a plotted value; dash marks a *doubted value*, not a missing one.
- **[official guidance]** Datawrapper's section heading is literally *"Use dashed lines for **assumed** lines"* — dash = collected-vs-assumed differentiation (main report §3.11). Fluent's own gaps doc ties dashed gap replacement to *"low confidence **predictions**"* and its reference sample labels the dashed overlay *'Low Confidence Data\*'* (§1.1). Azure Monitor's dashed connector (avg/min/max) spans *"two nearest known data points"* — i.e. Microsoft's dash depicts an interpolation (§1.1G).
- **[anecdote / widely-followed practice]** The financial/Excel convention is solid-actual/dashed-projected (Peltier Tech, "Chart with Actual Solid Lines and Projected Dashed Lines", https://peltiertech.com/chart-actual-solid-lines-projected-dashed-lines/; FAST Graphs' orange dashed lines = analyst **estimate range**). Tableau, for contrast, distinguishes forecasts by lighter shade, not dash (**[product behavior]**).
- **[official guidance]** Appian SAIL design system: *"Leave gaps in line charts to represent missing data."* (https://docs.appian.com/suite/help/26.6/sail/ux-charts.html) — one more design system on the leave-a-gap side.
- **[official guidance]** IBM Carbon's gap rule bans the mechanism outright: *"Never interpolate between periods when data is unavailable."* (Main-report caveat stands: Carbon's "gap denoted by texture" alt text is stale metadata — the shipped illustrations are blank gaps with landmark labels, no texture; §3.1.)

### B.2 Perception literature — the decisive question is empirically untested, and what exists cuts against 5e-i

- **[peer-reviewed research]** **Song & Szafir 2018, primary source** (PDF pulled and quoted; https://cmci.colorado.edu/visualab/papers/song_VIS_2018.pdf). Scope fact first: **no condition in either of their studies is a gap-spanning dashed connector** — the one dashed condition (bar-chart "Unfilled Bars with Dashed Outlines") draws the imputed bar at its imputed height; it is an outline of a present mark, not a bridge. So 5e was not directly tested. What the paper does establish:
  - The trustworthiness danger attaches to **continuity and plausibility**, exactly the geometry 5e-i reproduces: *"linear interpolation being highest"* on credibility/confidence among imputation methods, and connected error bars *"may preserve perceived quality even as actual quality decreases, **which could bias decision making**."* A dashed top-edge connector from last-known to next-known *is* linear interpolation with a texture applied.
  - Their dashed condition was classified **downplay** and finished **worst of seven on accuracy** (75.35% ± 1.52, Table 3), with the summary that encodings which *"break the continuous visual structure of a graph reduce perceptions of data quality and may actually inhibit analysis."*
  - Their design implication licenses Lattice's stance for this exact case: *"**Decision Risk:** … Visualization systems can encourage caution in interpreting flawed datasets by using representations that avoid bias and **appropriately decrease perceived quality**."* A permanent unobservable hole is precisely the case where low perceived quality is the *correct* outcome — the fusion's calm grey achieves it without asserting a trajectory.
- **[peer-reviewed research]** **Boukhelifa et al. 2012** (sketchiness/dash/blur/grayscale as uncertainty channels; https://inria.hal.science/hal-00717441/document): dashing was preferred by 68.3% of participants — but *"the primary reason for preferring dashing … was **'noticeability'**"*, while blur was the channel chosen as *semantically congruent* with uncertainty. Their guidelines: all four channels *"require a legend"*; dashing is unsuitable for *"dense displays"*; and these channels are inappropriate *"for line graphs … where geometry is also perceived as related to values change"* — which is a concurrency density band verbatim. **Dash does not self-signify**; it is a salient but arbitrary channel.
- **[peer-reviewed research]** The 2024 missing-data survey shows the field itself disagrees on what dashing *does*: Bäuerle et al. (parallel coordinates) classify dashed lines as a **highlight** of missingness, while Song & Szafir classified dashed outlines as **downplay**. A channel whose polarity flips between papers carries no stable meaning.
- **[peer-reviewed research]** Two older studies bolster the words-over-texture route: Eaton et al. (users *"may not notice that data is missing when it is replaced by a default value"*; the **coded** display — break + icon giving the reason — was preferred) and Andreasson & Riveiro (*"emptiness plus explanation was the most preferred technique with the highest degree of decision confidence"*). Both point at annotation, not line style, as the carrier of "why".
- **NOT FOUND: no study anywhere measures how viewers interpret a dashed segment bridging a gap.** The estimated-vs-absence question that decides 5e is empirically open — 5e cannot be defended from published evidence.

### B.3 Product sweep — who dashes across a gap?

- **Chart.js — yes, the canonical sample** (**[product behavior]**, https://www.chartjs.org/docs/latest/samples/line/segments.html): *"Gaps in the data ('skipped') are set to dashed lines"* via `segment.borderDash` + `spanGaps: true`. Two caveats: the sample pairs the dash with a drop to **20%-alpha black** (dash alone was not judged sufficient), and it is a styling-capability demo with no editorial rationale.
- **Google Charts — yes, named `certainty`** (§B.1) — and its meaning is "doubted value", not "no value".
- **Grafana — no.** `Line style` (solid/dash/dots) is **per-series, not per-segment**; with `spanNulls: Always` the bridged span renders in the same solid stroke as observed data, indistinguishable. No dashed-spanned-segment feature request found in grafana/grafana; adjacent complaints exist that connected nulls are indistinguishable (issue #42993; community thread "Null values connected no matter what the settings are"). **[product behavior]**
- **Excel — no.** Gaps / Zero / "Connect data points with line" only; the connector renders in the series' own style. The accepted Super User workaround for a dashed connector is a two-series hack — users want the distinction and the product cannot draw it. **[product behavior]**
- **Plotly (`connectgaps` boolean), Observable Plot (gap or nothing), Looker Studio (per-series style only) — no.** Highcharts/amCharts: no built-in found (not found ≠ confirmed absent). **[product behavior]**
- **New Relic** (alerting-side, semantically instructive): `LAST_VALUE` gap filling is exactly the imputation a dashed hold-last connector would depict — and it **expires on long gaps** (*"When a gap is longer than the expiration duration … the gap will no longer be filled"*). Even a system that imputes refuses to impute across a *long* hole — Lattice's routine case. **[official guidance]**

### B.4 The steelman — dash-as-absence, faithfully

- **[anecdote]** UX StackExchange (Chetan): *"Use dotted base line for all the positions where data is missing … solid line indicating something present/existing and dotted line indicating something missing or virtual."* Note this is the **5e-iii baseline variant**, paired with axis-label highlighting and a tooltip — not a top-edge connector.
- **[anecdote]** Bocoup's `d3-line-chunked` (Peter Beshai, 2016) renders gap segments in a distinct style and frames the problem exactly as Lattice does (*"We could just connect the dots, but it's misleading"*) — but its stated reason for drawing the connector is animation smoothness, not semantics.
- **[anecdote]** Counter-anecdote from a practitioner who shipped it and reversed (UX StackExchange, Mike M): *"we made the call to **remove the connecting dotted line we were using at the time to remove any implications of trend**"*, recommending a marked region instead because *"it emphasizes the unknown, which will prevent trend speculation"* plus a legend. FlowingData sides with holes: *"Don't have something? Don't show it."*

The steelman is real but evidentially thin — two forum comments and a blog post whose own rationale is aesthetic — and its best-articulated form is the baseline variant (5e-iii), which draws no trajectory and is already absorbable into the fusion.

## C. Interaction with Lattice's specifics

- **Stacked density band:** 5e-i must pick a height to connect through — any choice (last→next linear, hold-last) *is* an imputation rendered as geometry, and Boukhelifa's guideline says line-style uncertainty channels are inappropriate exactly where *"geometry is also perceived as related to values change"*. 5e-ii/5e-iii avoid the fabricated height but then are no longer "connectors" — they are region/axis markings, i.e. reinventions of 5d's edges and baseline treatment.
- **Long routine holes:** a dashed connector spanning 60% of the viewport is a chart-dominating stroke that asserts a night-long trajectory; Datadog's connected-line-across-agent-outage is the documented cautionary tale, and New Relic's expiry rule shows even imputing systems refuse long spans. Conversely, at high zoom-out a *short* hole's dashed segment falls below the ~10–15px pattern floor (§4.2) and aliases into a solid-looking stroke — at exactly the width where it is indistinguishable from real band edge.
- **Deterministic snapshots:** dash phase/period across arbitrary-width holes is deterministic given fixed geometry, but the pattern-floor degradation rule would have to apply to the connector too, and a connector that degrades to a *solid* bridge at narrow widths is the worst possible failure (an unmarked fabricated segment).
- **Composition with the 5c/5d fusion:** 5e-i is an **alternative** to the fusion and conflicts with it (the fusion's premise is that nothing is drawn *as a value* inside the hole). 5e-ii collapses into the fusion's edge hairlines. **5e-iii composes cleanly**: turning the baseline dashed across the hole is a legitimate absence encoding (no value asserted), reinforces the baseline-break idea from 5a/5d, and can be adopted *inside* the fusion as the hole's baseline treatment — subject to the same pattern floor (solid-edge degradation below ~10–15px) and to not colliding with the red dashed unreachable frame (two dashed vocabularies in one chart need distinct weights/colors or the exceptional class loses its distinctness).

## D. Matrix rows and verdict

| Finding | 5e-i top-edge connector | 5e-ii dashed outline | 5e-iii dashed baseline |
|---|---|---|---|
| UK AF "do not join … even if dotted or dashed" [official] | **C (red-crossed verbatim)** | N | N (no joining of value points) |
| UK AF textured-lines ambiguity warning [official] | C | C (weaker) | C (weaker) |
| Google Charts `certainty` = dashed segment means doubted *value* [product] | **C (semantic collision)** | N | N |
| Datawrapper dash = assumed; Fluent dash = low-confidence prediction; Azure dash = interpolation; Peltier/FAST dash = projection [official/product/practice] | **C** | N | N |
| Song & Szafir: interpolation geometry = highest credibility while quality drops → bias; dashed condition worst-of-7 accuracy [peer-reviewed] | **C (reproduces the highest-bias geometry)** | N | N |
| Boukhelifa: dash = noticeable but not self-signifying; needs legend; wrong for dense value-geometry charts [peer-reviewed] | C | N | N |
| Carbon "never interpolate"; Appian "leave gaps"; GitLab Pajamas dashed grey bridge [official] | C / C / S (Pajamas is 5e-i's one design-system precedent — and it is the treatment the UK AF red-crosses) | N | N |
| Grafana/Excel/Plotly/Plot: bridge identical to data or no bridge; Chart.js dash+alpha sample; New Relic long-gap expiry [product] | C (no monitoring product ships it; the two libraries that can both undercut it) | N | N |
| Steelman anecdotes (dotted baseline = "missing/virtual"; practitioner reversal) [anecdote] | C (the reversal) | N | **S (the steelman's actual form)** |
| Pattern floor ~10–15px (§4.2) [derived] | C (degrades to a solid fabricated bridge) | C below floor | C below floor (degrade to solid baseline) |

**Verdict: 5e-i (the connector proper) is rejected — it is the single most contradicted candidate in the entire study, including 4a.** It is red-crossed verbatim by the UK Analysis Function; its visual channel officially means "estimate/forecast/doubted value" everywhere a meaning is pinned (Google `certainty`, Datawrapper "assumed", Fluent "low confidence predictions", Azure interpolation, financial convention); Song & Szafir's primary text shows its geometry (linear interpolation, connected) is the highest-bias configuration — *plausible-looking fabrication that preserves perceived quality while actual quality is zero*; and no monitoring product ships it. On the hard rule: a dashed trajectory through unobserved time **is** fabricated data with a texture apology — the dash does not neutralize the fabrication, because no evidence exists that viewers decode dash as absence, and substantial evidence exists that they decode it as estimate. 5e-ii is 5d's edge treatment reinvented. **5e-iii (dashed baseline across the hole) survives as the legitimate kernel of the owner's idea and composes with the recommended 5c/5d fusion** — adopt it, if desired, as the fusion's baseline treatment with the pattern-floor degradation and a visual-weight separation from the red unreachable frame.

**Recommendation change: none.** The 5c/5d fusion + 5b companion stands; 5e-iii is offered as an optional refinement inside it. Confidence: **high** on rejecting 5e-i (the convergence is unusually one-sided and includes the primary literature read directly); **moderate** on 5e-iii's value-add (it rests on one steelman anecdote and internal consistency with 5a/5d, not on measured evidence). Open questions: (a) the decisive perception question — how viewers read a dashed gap-bridging segment — has never been tested by anyone; if the owner wants 5e-i seriously reconsidered, the only path is our own small falsification test, not more literature; (b) whether two dashed vocabularies (baseline + red unreachable frame) can coexist without diluting the exceptional class is an owner-eye call on real renders.
