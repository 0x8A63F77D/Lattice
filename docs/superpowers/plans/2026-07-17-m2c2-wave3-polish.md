# M2c-2 Wave 3 — motion/polish + final M2 walkthrough (#32) — Implementation Plan

> **Status:** Plan only. Merging this doc does **not** authorize execution. The controller
> presents a concept summary to the owner and spawns execution chips only after owner sign-off.
> Issue **#32 stays open** until the final walkthrough (last section) completes; **#11 is
> absorbed** by this wave (its code half lands in PR A; the on-hardware Mica check stays #11).

**Date:** 2026-07-17
**Milestone:** M2 — Read-only dashboard (final wave)
**Design authority:** `docs/design/m2/README.md` + `Lattice M2 Spec - Final` HTML (cards `1h` motion,
`1i` responsive, `1c`/`1f`/`1g` shell/dark/tokens, `3a` rail). Where this plan and the design
package disagree on visuals, **the design package wins — flag it, don't silently diverge.**
Shell-design spec: `docs/superpowers/specs/2026-07-09-m2c-avalonia-shell-design.md` (§11 motion,
§10 responsive, §4 Mica). Shell-rework decisions: `docs/superpowers/specs/2026-07-12-m2-shell-rework-26-decisions.md` §9 (deferred list).

---

## Header — routing, gates, non-goals

### Plan-granularity routing (controller-decided, hybrid B)

- **C#/XAML tasks are CONTRACT-LEVEL specs** — input→output relations, invariants, and acceptance
  assertions, **not** transcription-ready code. Route to **Sonnet-tier** executors. Each task
  carries a **judgment-routing line**: *"Executor may pick the cheapest sufficient model tier for
  this task; escalate to Opus on the first failed fix (tripwire), never iterate on Sonnet."*
- **F# pure-core logic stays FULL-SNIPPET and born-pure** per the F# canon (declarative
  input→output first, recursion scheme + immutable signature up front). This wave introduces **no
  new F# module** (see "Pure policy modules" below — the one policy discovered lands as a pure
  **C#** static in `Lattice.App`, matching the `PartialBarPolicy`/`TasksOverlayPolicy`/
  `ColumnVisibilityPolicy` precedent; F# declined with rationale, same posture as shell-design §7).
- **Avalonia API discipline:** every framework-touching task consults the **avalonia-docs** MCP
  before writing XAML/framework C# (CLAUDE.md rule). Named lookups are called out per task
  (`TransparencyLevelHint`/`ActualTransparencyLevel`, `PageTransition`/`CrossFade`,
  `DoubleTransition`/`Transitions`, `PlatformSettings` reduce-motion).

### Merge-gate policy (the load-bearing rule for a motion/polish wave)

Motion/polish changes are **visual**. Two distinct gates, stated per task:

- **VISUAL gate — one version → owner eyeball, NO autonomous visual iteration.** The executor
  produces *one* rendition, captures the screenshot/recording artifact, and stops. There is no
  machine acceptance signal for "does this motion feel right"; self-iteration on visuals drifts
  (owner ruling, memory: *user-review-boundaries*). The owner is the merge gate for visual
  fidelity. This applies to every transition timing, easing, filled-icon look, and Mica region material.
- **CORRECTNESS gate — headless assertions (machine-gated).** Everything with an observable
  end-state gets a headless test: reduce-motion **zeroes** durations, a transition is **present/
  absent** under the right pseudo-class, **data updates never block on animation** (value-first),
  the Mica-backdrop **policy** maps correctly, the selected nav item resolves the **filled**
  geometry. These block the PR the normal way (`dotnet test`, all three CI legs).

Rule of thumb: *if a machine can read the settled end-state, assert it; if only an eye can judge
the in-between, it is owner-gated.* Motion **duration/curve** values are owner-gated; motion
**wiring/behavior** (present, non-blocking, reduce-motion-off) is headless-gated.

### Non-goals (unchanged from M2c scope)

- No control operations (M3), no charts/SSH/host-groups/notifications (M4).
- **No `HostMachine` / `HostMonitor.cs` change** (verification-sync guard). If any task appears to
  need it → STOP and surface to the controller (shell-design §14).
- No change to `Lattice.Core` public surface; no protocol logic in `Lattice.App`.
- No new `Lattice.App.Aggregation` F# module (nothing in this wave is a multi-case domain
  transformation that pays a cross-project F# layer — see below).

---

## Reconciliation inventory

The issue (2026-07-11) predates PRs **#84** (#26 shell rework, `b2ba4ca`), **#74** (#57/#55 grid
fidelity, `d70406f`), and **#87** (#86 view-owned order, `786f823`). Below is what those merges
already folded in, verified against the current tree, and what genuinely remains. **Scope of this
wave = the "REMAINS" rows.** Dropped items are listed with the reason.

> **Reconciliation-methodology note (corrections during Codex review, PR #89).** The first draft of
> this table marked two items "DONE" by *inferring* completion from a grep hit rather than reading
> the implementing behavior — both were wrong and Codex caught both: (1) filled nav icons (the
> code-behind `UpdateMenuIcons` swap was missed → over-scoped), and (2) rail auto-collapse (a
> height-fit subscription was mis-read as a width-collapse driver → under-scoped, dropped a real
> requirement). Every "DONE / already-landed" verdict in the table above has since been re-checked
> **against the implementing code, not a keyword match.** This is the expected outcome of routing a
> single-author draft through the normal review loop (memory: *own-output-not-pre-reviewed*).

| Deferred-visual item (issue #32 / decisions §9) | Status after #84/#74/#87 | Evidence (current tree) | Verdict |
|---|---|---|---|
| **InfoBadge (nav counts)** | **DONE (Event-log unread)** | `FAInfoBadge Value="{Binding EventLogUnread}" IsVisible="{Binding HasEventLogUnread}"` — `Views/ShellWindow.axaml:113-115`; count from `EventLogViewModel.cs:58` / `ShellViewModel.cs:158`. (Direct XAML binding, verified by reading — not inferred.) | **Dropped from scope.** The Event-log unread badge is wired. Whether the HTML spec calls for additional per-view/per-host nav badges is a **walkthrough verify-against-spec item** (card `1c`/`1e`), not asserted here; if the owner finds a missing badge during the walkthrough, file a follow-up. |
| **Window-width breakpoints** | **PARTIAL** | **Column-shed DONE:** `Views/TasksView.axaml.cs` `ApplyColumnVisibility(BreakpointWidth)` sheds Application/Elapsed on **window-width** (`BreakpointWidth => _topLevel.Bounds.Width`, card `2f`/`1i`, PR #28). **Rail auto-collapse NOT implemented:** `ShellWindow.axaml:72` pins `PaneDisplayMode="Left"` (always-open pane); the `ShellWindow.axaml.cs:73` Bounds subscription feeds **height** into `SetRailViewportHeight` (the flat↔grouped fit math, decisions §3), **not** a width→pane-collapse driver; no handler sets `IsPaneOpen`/`PaneDisplayMode` at the 1100 breakpoint (the `#Nav.IsPaneOpen` refs are consumers that hide text when compact). *(Corrected per Codex P2 on PR #89 — the original "DONE" read inferred rail-collapse from the Bounds subscription without reading that it drives height, not width.)* | **Column-shed dropped (done); rail auto-collapse REMAINS → PR E.** |
| **Filled nav icons (selected/active)** | **MOSTLY DONE** | The four **view** items already swap outline→filled at runtime: `ShellWindow.axaml.cs:228-241` `UpdateMenuIcons()` resolves `IconFilledKey` when selected / `IconKey` otherwise, called on first render (`:85`) and on every selection change (`:222`); covered by `ShellRailTests`. The XAML `*Regular` `IconSource` (`ShellWindow.axaml:87-112`) is only the initial value the code-behind overwrites. **Gap:** the **Settings** footer item (`ShellWindow.axaml:242-245`) hard-codes `IconSettingsRegular` and sits outside the `_shell.Views` loop, so it never fills when Settings is active. `IconSettingsFilled` is already defined. *(Reconciliation corrected per Codex P2 on PR #89 — the original "NOT wired" read grepped only the XAML and missed the code-behind runtime swap.)* | **REMAINS (Settings item only) → PR B.** |
| **Mica-adjacent polish (#11)** | **NOT wired** | `ShellWindow.axaml:12` requests `TransparencyLevelHint="Mica, None"` but `:13` sets an **opaque** `Background="{DynamicResource LatticeCanvasBrush}"` (`Tokens.axaml:7,30` = `#FAFAFA`/`#1F1F1F` solids); nothing reacts to `ActualTransparencyLevel`. #11 comment (2026-07-11) confirms Mica is invisible even on Win11 today. | **REMAINS → PR A** (folds #11's code half). On-hardware verification stays in **#11**. |
| **Motion / transition polish** (spec §11 / card `1h`) | **PARTIAL** | Present: `Connecting` spinner (`ShellWindow.axaml:29` infinite animation), Projects chevron transform 120 ms (`ProjectsView.axaml:44-47`). Deliberately **instant** (owner visual verdict, #74): DataGrid row hover — *no* fade (`DataGridStyles.axaml:143-155`, documented). Missing vs card `1h`: view-switch (`ShellWindow.axaml:248` `ContentControl` has no `PageTransition`), progress-bar width, row enter/remove, transfer completed-row fade-out, expander height, **reduce-motion** gate (none anywhere). | **REMAINS → PR C1 + PR C2** (largest chunk). |
| **Compact grouped rail rendering** — stacked single-icon per collapsed Healthy group + 8 px badge dot (decisions §5/§9) | **NOT built (explicitly deferred)** | decisions §5: M2 renders individual host state-icons when compact; the "one stacked icon per collapsed group" + 8 px dot deferred to the #32 polish wave. | **DEFERRED to M3 (owner ruling, 2026-07-17) → issue #96.** The wave's only **custom-draw** surface; per the failure-mode-locality razor (memory) the AI loop is weakest on hand-drawn UI, against Lattice's all-native-components posture. Out of this wave's scope; #96 (milestone M3) carries the reference. |

**Opportunistically-swept adjacent issues** (issue #32 "candidates to sweep if cheap"), assessed:

- **#17 (Layout helper dedupe)** — the six `Layout(window)` copies precedent is already
  consolidated; #67 is the *current* altitude of this work. **Nothing to do here; owned by #67.**
- **#13 (visual-regression test leg)** — the visual-regression harness exists (`tests/Lattice.VisualTests`, PR #82, report-only, env-gated). Its Wave-3 relevance is that **PR A/B/C motion+Mica+icon renditions are natural new baseline candidates**. **Do not fold #13 into a code PR**; instead the walkthrough section notes which Wave-3 surfaces are baseline-worthy, and #13 lands separately. Reason: bundling a screenshot-baseline expansion into a motion PR muddies the owner's visual gate.
- **#18 (HostStore no-op notification)** — a Core/infra correctness nit, **not visual**, orthogonal to this wave. **Dropped**; leave to its own PR (headless-gated, no owner eyeball needed).
- **#88 (empty-state, decision (a))** — queued behind #67; **not** motion/polish. Dropped from this wave; it is its own VISUAL PR with an owner-eyeball merge gate (per project status).

---

## Pure policy modules named at plan time

Per the "state-machine-like logic → named pure policy at plan time" rule (`PartialBarPolicy`
precedent), two candidates were examined:

### `MicaBackdropPolicy` (NEW — pure C# static in `Lattice.App`) — **adopt**

The Mica fix is a **derivation**, not inline reactive glue: *given the level the OS actually
granted, decide whether the Mica-bearing surfaces go transparent or fall back to opaque*. That is
exactly the small, testable decision the repo extracts into a policy — and #11's own acceptance
shape demands an **automated assertion** on it. Naming it at plan time keeps the risk in a pure
function (bounded failure: wrong output, machine-checkable) instead of scattered
`ActualTransparencyLevel` handlers (unbounded, eyeball-only) — the failure-mode-locality razor.

Home: `src/Lattice.App/Infrastructure/MicaBackdropPolicy.cs` (pure static). **Framework fact
(controller-verified 2026-07-17):** `WindowTransparencyLevel` in Avalonia 12.1 is a **readonly
struct with static properties** (`None` / `Transparent` / `Blur` / `AcrylicBlur` / `Mica` are `P:`
entries in the package XML docs), **not an enum** — so there is no compiler exhaustiveness to lean
on; the map is **equality-based** and totality comes from the **else-branch**. **F# declined** with
the same rationale as shell-design §7: a one-line equality map over an Avalonia framework struct
does not pay a cross-project translation layer, and `Lattice.App.Aggregation` must stay
Avalonia-free. Contract (the executor writes the body; this is the input→output the headless test
pins):

```
// input:  the level the platform reports as ACTUALLY applied (WindowTransparencyLevel struct)
// output: BackdropChoice { bool WindowTransparent; bool RegionSurfacesTransparent }
//   level == WindowTransparencyLevel.Mica -> { WindowTransparent = true;  RegionSurfacesTransparent = true }
//   anything else (the fallback)          -> { WindowTransparent = false; RegionSurfacesTransparent = false }
// EQUALITY-BASED, not a switch over enum cases (WindowTransparencyLevel is a struct — no
// exhaustiveness). Totality is the else-branch; a boundary comment states that ONLY Mica takes the
// transparent path and every other granted level (including a DENIED Mica reported as None) falls
// back to opaque — never a broken transparent-without-material state. CONTENT surfaces are NOT part
// of this choice: they stay opaque unconditionally (design: "content surfaces are always opaque").
```

> Naming trap to avoid at execution: the switch must NOT branch on the **requested** hint
> (`Mica, None` is always requested); it branches on **`ActualTransparencyLevel`** — the granted
> level — because Win10/macOS/Linux request Mica and are denied. This is the exact bug the #11
> comment describes (opaque brush painted over a granted-or-not backdrop).

### Reduce-motion gate — **considered, NOT a policy module**

"System reduce-motion → zero all durations except spinners" is a single boolean applied via a
**root style-class toggle** driven by `TopLevel.PlatformSettings` (Avalonia surfaces the OS
setting). There is no branching domain logic to model — it is a resource/style switch, not a
transition table. Extracting it would be ceremony. It lands as a styling concern in PR C2, with a
**headless assertion** that the reduce-motion class zeroes a representative transition's duration.

### Transfer completed-row fade-then-remove — **considered, NOT a policy module**

The 150 ms fade before removal (spec §8) is animation-timed reconciler removal, not a decision DU.
It lands in PR C2 as a reconciler/animation concern; correctness (the row *is* eventually removed,
data not blocked) is headless-gated.

---

## PR breakdown (small-PR cadence; each with its own machine gate)

Ordering rationale: **PR A and PR B are independent and headless-gatable up front** (Mica policy +
icon-swap have crisp correctness assertions and don't touch the shared view fixtures). **PR C1/C2
add headless motion tests over the view fixtures and therefore build on the consolidated
`HeadlessAppFixture` from #67** (see Scheduling). PR D (compact grouped rail) is **deferred to M3
(#96)** and retained below only as an M3 reference.

### PR A — Mica backdrop wiring (folds #11 code half)

**Goal:** make Mica visible in the **nav + command-bar regions** when Windows grants it; keep the
exact opaque look everywhere it doesn't. Closes the code half of #11.

**Design prescription (controller read the design HTML at joint review, 2026-07-17):** the design's
only Mica line is — *"Mica: on Windows, the nav and command-bar regions use Mica material; `#202020`
is the solid fallback for macOS/Linux and battery saver; content surfaces are always opaque."* So
the effect is **region-scoped, not a whole-window tint, and there is NO Mica tint token to read**
(the plan's earlier STOP-on-missing-tint condition triggered and was resolved here).

**Contract-level tasks** (Sonnet-tier; *executor may pick the cheapest sufficient tier; escalate
to Opus on first failed fix*):

1. **`MicaBackdropPolicy`** per the contract above (equality-based, boundary comment on the
   else-branch; `WindowTransparencyLevel` is a struct — no enum switch).
2. **Wire `ShellWindow`** to observe `ActualTransparencyLevel` (avalonia-docs: confirm the property
   + change-notification shape for Avalonia 12) and apply the policy: when Mica is granted, the
   **window background goes transparent** AND the **nav + command-bar region surfaces stop painting
   opaque** so the material shows through; otherwise keep today's opaque surfaces. **Content
   surfaces stay opaque always** — never bound to the Mica state.
3. **Region fallback brushes — dedicated, NOT the shared canvas or surface brush.** The design's
   `#202020` is the *Mica-region* fallback for the nav (already `LatticeNavSurfaceBrush` `#202020`,
   `Tokens.axaml:32`). Do **not** retoken the content canvas (`LatticeCanvasBrush` `#1F1F1F`,
   `:30` — paints Settings/content, MUST stay; retokening it darkens dark-mode content, Codex P2).
   **Cover ALL command bars, not just Tasks:** every view's top command-bar `Border` paints
   `LatticeSurfaceBrush` inline — `TasksView.axaml:45`, `ProjectsView.axaml:97`,
   `TransfersView.axaml:35`, `EventLogView.axaml:97` — so naming only Tasks would leave the other
   three opaque over Mica (Codex P2 on PR #89). But `LatticeSurfaceBrush` is **also** used by
   loading/empty overlays (`TasksView.axaml:235`, `EventLogView.axaml:226`, …), so it must **not**
   be blanket-transparentised. Introduce a **dedicated command-bar region brush** (e.g.
   `LatticeCommandBarSurfaceBrush`, seeded to today's `#292929`) that the four command bars switch
   to, and have the Mica toggle flip **that** brush (plus `LatticeNavSurfaceBrush` for the pane)
   transparent↔opaque — one toggle, all four views, content/overlays untouched. **Design-fidelity
   check at execution:** the design names `#202020` for *both* nav and command-bar; the command bar
   is currently `#292929` — confirm with design/owner whether the command-bar region should become
   `#202020`; flag, don't silently change it.
4. **Invariant:** no feature depends on the material being present (CLAUDE.md); when Mica is denied
   the app is the opaque fallback — pixel-identical to today (no token changes; the region brushes
   already exist).

**Machine gate (CORRECTNESS):** headless test on `MicaBackdropPolicy` (Mica → both-transparent; any
other level → both-opaque); headless test that under the headless platform (grants no Mica) the
window + nav/command-bar regions resolve the opaque fallback and content/overlays stay opaque — i.e.
CI exercises the fallback path. Assert the command-bar surface is driven by the **shared**
`LatticeCommandBarSurfaceBrush` in **all four** views (Tasks/Projects/Transfers/EventLog), so no
view is left opaque over Mica.

**Owner gate (VISUAL):** one screenshot on macOS confirming the solid fallback is unchanged
(one version → owner eyeball). **On-hardware Mica verification is NOT in this PR** — it is issue
**#11**, which stays open and runs on real Win11 hardware after this merges. This PR's completion
comment on #11 links here and states "code half landed; on-hardware check remains."

### PR B — Filled Settings nav icon on active (extends the existing view-icon swap)

**Goal:** close the ONE remaining filled-icon gap — the **Settings** footer nav item. The four
view items already swap outline→filled via `UpdateMenuIcons()` (see reconciliation); this PR
**extends that same mechanism** to the Settings footer item so it fills when Settings is the active
page, matching the design asset rule ("outlined regular at rest, filled for selected/active").

**Contract-level tasks** (Sonnet-tier; judgment-routing as above):

1. Extend the existing icon-swap path — **do NOT add a parallel mechanism** (altitude; a
   `Selector`-driven style would duplicate the working code-behind swap). When the active page is
   Settings, `NavSettings.IconSource` resolves `IconSettingsFilled`, else `IconSettingsRegular`.
   `UpdateMenuIcons()` currently loops `_shell.Views` only; fold the Settings footer item into the
   same recompute (it is tracked separately by `SyncNavSelection`, `ShellWindow.axaml.cs:192` —
   `CurrentPage == Settings ? NavSettings`). `IconSettingsFilled` is already defined in
   `Icons.axaml`; the XAML `IconSettingsRegular` stays the initial value the recompute overwrites
   (same pattern as the view items).
2. **Invariant:** exactly one filled glyph across the whole rail (the active destination — a view
   OR Settings, never both); deselecting Settings restores `IconSettingsRegular`; compact (48px)
   rail unaffected.

**Machine gate (CORRECTNESS):** headless test — navigate to Settings → `NavSettings` resolves
`IconSettingsFilled` and all four view items resolve `*Regular`; navigate back to a view →
`NavSettings` returns to `IconSettingsRegular`. Extends the existing `ShellRailTests` view-icon
coverage; geometry identity is readable headlessly.

**Owner gate (VISUAL):** one screenshot of the rail with Settings active (filled Settings glyph) →
owner eyeball.

### PR E — Responsive rail auto-collapse (1100–1279 → 48px compact)

**Goal:** implement the missing responsive breakpoint (design card `1i`; README:106) — the nav/
hosts rail **auto-collapses to the 48px compact pane** when window width is in **1100–1279**, and
is fully open (260px) at **≥1280**. Today `PaneDisplayMode="Left"` pins the pane open at every
width; compact is reachable only via the manual pane toggle, never by width. (Discovered by Codex
P2 on PR #89 — see reconciliation.)

**Contract-level tasks** (Sonnet-tier; judgment-routing as above; **avalonia-docs REQUIRED**):

1. Drive the pane between exactly **two** states at the design's single 1280 breakpoint: width ≥
   1280 → **expanded** (260px, `PaneDisplayMode=Left`); **1000 ≤ width < 1280 → 48px compact**
   (`PaneDisplayMode=LeftCompact`, pane visible, icons-only). The compact rail must hold across the
   **whole** 1000–1279 band (the rail stays 48px below 1100 while columns shed — design `1i`), never
   the menu-button-only Minimal pane. avalonia-docs FIRST: confirm FA 3.0.1 exposes `LeftCompact`.
   **Do NOT use `PaneDisplayMode=Auto` with `CompactModeThresholdWidth=1100`** — FA `Auto` has three
   tiers (Minimal → Compact → Expanded) and `CompactModeThresholdWidth` is the width at which
   *Compact* begins, so 1100 renders the supported **1000–1099** range as **Minimal** (menu-button
   only, pane hidden), not the 48px compact rail the design requires (Codex P2 on PR #89). Prefer an
   explicit `LeftCompact`/`Left` toggle from a **window-width** handler (mirror TasksView's
   `BreakpointWidth => _topLevel.Bounds.Width` — window-width, NOT pane-relative, the PR #28
   landmine). If `Auto` is used instead, `CompactModeThresholdWidth` must be ≤ the 1000 minimum (so
   Minimal is never reached) with `ExpandedModeThresholdWidth = 1280`.
2. **Invariants:** the existing consumers already react to `IsPaneOpen` correctly (row-text hides,
   icons-only via `#Nav.IsPaneOpen`; `SetRailPaneCompact` force-expands Healthy so icons show,
   decisions §5) — this PR adds only the width→collapse *driver* and must not change those
   consumers; the manual pane toggle still works; the height-driven flat↔grouped fit math
   (`SetRailViewportHeight`) is orthogonal and untouched.

**Machine gate (CORRECTNESS):** headless — at **~1050px** (inside the 1000–1099 band) the rail is
the **48px compact** pane (present, icons-only), **NOT** Minimal/menu-button-only; at ~1200px
compact; at ~1300px **expanded** (260px). The ~1050 case is the one the design's below-1100 range
needs and the naive 1200/1300 pair would miss (the P2 root cause). Real machine signal (pane
mode/width readable headlessly); dovetails with the existing `ShellRailTests` compact pins
(`Collapsed_pane_keeps_the_all_hosts_sentinel_icon_only`).

**Owner gate (VISUAL):** one screenshot at ~1200px showing the 48px compact rail → owner eyeball.

**Dependency:** independent of #67 (shell-level, no view fixture) and of motion — runs alongside
PR A/B. (The deferred M3 compact grouped-rail work, #96, will build on E's width-reachable compact
rail.)

### PR C1 — Motion: view-switch + progress-bar width

**Goal:** the two highest-visibility motions from card `1h`.

**Contract-level tasks** (Sonnet-tier; judgment-routing as above; avalonia-docs on every API):

1. **View switch** — `ShellWindow.axaml:248` `ContentControl` → a transitioning host with a
   **150 ms decelerateMid** page transition, **opacity + 8 px translateY** (design `1h`; spec §11).
   avalonia-docs: the Avalonia 12 idiom (`TransitioningContentControl` + `PageTransition`, or a
   composed `CrossFade`/`PageSlide`). **Data-first invariant:** the new page's data is already
   bound before the transition plays; the animation is cosmetic and never delays a data update.
2. **Progress-bar width** — Tasks progress cell (56×3 restyled `ProgressBar`) and Transfers
   `Border.progressFill` (`TransfersView.axaml:126`) animate width over **200 ms easeEase**
   (`1h`). Value-first: the bound value updates immediately; the width transition is cosmetic.

**Machine gate (CORRECTNESS):** headless — the transitioning host **has** a `PageTransition` of the
specified duration (property present); a progress value change updates the bound value synchronously
(value-first, not blocked on the transition). *These assert wiring/behavior, not the visual feel.*

**Owner gate (VISUAL):** one recording (or before/after screenshots) of a view switch and a
progress tick → owner eyeball for timing/curve. **No autonomous re-timing** — if the owner wants a
different feel, that is the owner's call, not an executor iteration loop.

### PR C2 — Motion: rows, expander, transfer fade + reduce-motion gate

**Goal:** the remaining card `1h` motions + the global reduce-motion switch.

**Contract-level tasks** (Sonnet-tier; judgment-routing; avalonia-docs on every API):

1. **Row enter** 100 ms opacity-only (no height animation — design `1h` explicit); **row remove**
   150 ms accelerateMid. Respect the **existing owner verdict** that DataGrid row *hover* is
   instant (`DataGridStyles.axaml`) — **do not add hover motion**; enter/remove only.
2. **Transfers completed-row fade-out 150 ms then remove** (spec §8) — fade, then the reconciler
   drops the row. Invariant: the row **is** eventually removed (no leak); the fade never blocks the
   snapshot apply.
3. **Expander chevron 100 ms + height 200 ms** (`1h`) — align the Projects chevron (currently a
   single 120 ms transform) to the spec's split (rotation 100 ms, content height 200 ms).
4. **InfoBar in/out and dialog scrim (200 ms in / 150 ms out)** — confirm the FluentAvalonia
   defaults match `1h`; only override if they don't (avalonia-docs: FA InfoBar/ContentDialog
   default transition timings). If FA already matches, record "no change needed, FA default
   verified" rather than restyling.
5. **Reduce-motion gate** — a root style class driven by `TopLevel.PlatformSettings` (avalonia-docs:
   the reduce-motion / `PlatformSettings` surface in Avalonia 12) that **zeroes every transition
   duration except spinners** (spec §11). Applied globally, once.

**Machine gate (CORRECTNESS):** headless — reduce-motion class **zeroes** a representative
transition's duration (assert the resolved duration is `0` under the class, non-zero without it);
a completed transfer row is **removed** after its fade (settle on the collection end-state, not a
wall-clock delay — use the deterministic drain, not a real sleep); row/expander transitions are
**present** with the specified durations. *All behavioral, not feel.*

**Owner gate (VISUAL):** one recording of: a row entering/leaving, a transfer completing, an
expander toggling, and the app under OS reduce-motion → owner eyeball. One version, no iteration.

### PR D — Compact grouped rail rendering — DEFERRED to M3 (owner, 2026-07-17) → #96

**Not in this wave.** At joint review (2026-07-17) the owner ruled the compact grouped-rail
rendering (one stacked icon per collapsed Healthy group + 8 px status dot, card `3a` compact
vision, decisions §5) **defers to M3** — it is the wave's only **custom-draw** surface, and the
failure-mode-locality razor (memory: *hobby-project-idealized-design*) makes hand-drawn UI the
weakest link in the AI verification loop, against Lattice's all-native-components posture. Tracked
as **issue #96 (milestone M3)**.

**M3 reference (what #96 will scope), retained here for continuity:** a compact-mode template
collapsing a Healthy group to a single stacked state-icon + 8 px dot; Attention hosts still render
individually (decisions §5); the pure `RailLayoutPolicy` stays **untouched** (compact is the
orthogonal pane-collapse axis — it must not leak into the height-driven core). Machine gate then:
headless — a collapsed Healthy group yields exactly one icon element + one 8 px dot, Attention hosts
individual, expanded (260 px) rail unchanged; plus one owner-eyeball screenshot. Until then the
current behavior stands — individual host state-icons when the pane is compact (decisions §5).

### PR F — DEBUG sample host (walkthrough prerequisite)

**Goal:** build the DEBUG-only injectable sample host the final walkthrough (and shell-design §12.4 /
§13.2) depends on. The live daemon has **0 attached projects**, so the data-rich states — 500+
tasks (virtualization check), transfers in every state, multi-project aggregates — can only be
exercised from canned data. **Codex P2 on PR #89 confirmed no `SampleHost`/canned-snapshot exists in
the tree today** (grep of `src`/`tests` is empty); without it the owner cannot run walkthrough steps
1–2, so this is a **hard prerequisite** to the walkthrough, not optional tooling.

**Contract-level tasks** (Sonnet-tier; judgment-routing as above):

1. A `#if DEBUG`-gated injectable host backed by canned `HostSnapshot`s (shell-design §12.4): ≥500
   tasks (virtualization), transfers in all states (active/retrying/queued/completed), a
   multi-project set that exercises Projects aggregation (`Varies`, mixed status tiers). Togglable in
   a DEBUG build with no live daemon; it feeds the **same** `HostStore`/VM path as a real host (no
   bypass — the UI computes nothing new).
2. **Boundary:** DEBUG-only, never compiled into Release; **no protocol / `Lattice.Core` change** —
   inject at the App/`HostStore` seam, the same shape as the fake `IGuiRpcClient` used in tests.

**Machine gate (CORRECTNESS):** headless (DEBUG build) — with the sample host injected, the Tasks
grid materializes ≥500 rows, Transfers shows each state, Projects shows a `Varies`/mixed-tier
aggregate (reuses the `HeadlessAppFixture` fake-fed pattern; asserts the canned data reaches the
grids). **No owner-visual gate for the tooling itself** — its visual payoff is the walkthrough.

**Dependency:** independent of A/B/E and of the #67 fixture (an App-seam injectable); can build early
alongside A/B/E, but **must land before the walkthrough** (it is that session's data source).

---

## Final M2 walkthrough — owner-run acceptance checklist artifact

This is **not** an agent task. It is a live acceptance session the **owner runs** against the test
daemon (BOINC 8.2.11, dedicated test machine), closing the M2 milestone and doubling as the README
screenshot source (**#27**). The executor's deliverable is the **checklist artifact**
(`docs/design/m2/M2-walkthrough-checklist.md`); the **DEBUG sample host it relies on for the
data-rich states is built by PR F** (prerequisite above). The **owner executes** the checklist and
its pass is what closes **#32**.

Checklist artifact contents (the executor writes the checklist; the owner ticks it):

1. **Multi-host live run** — ≥2 hosts against the test daemon; all four views render live data
   (Event log shows real merged messages; Tasks/Projects/Transfers show design-correct empty states
   at 0 attached projects). With the **DEBUG sample host**: 500+ task rows scroll smoothly; sort/
   filter/density/column-show-hide work; Projects parent/child expand with correct aggregates
   (`Varies`, mixed status tiers); transfer retry countdown ticks; Event-log Following pauses on
   scroll-up.
2. **Connection-state matrix, live** — Connecting → Connected; quit the daemon → Retrying (live
   per-second countdown + attempt) → Unreachable tier (attempt ≥ 4); wrong password → Auth-failed →
   rail click opens the Edit-host dialog (card `3b`) with the password field in error+focus.
3. **Scope** — All-hosts vs single-host switches every view; the partial-results InfoBar appears
   under All-hosts when ≥1 host is unreachable and dismisses/reappears on fingerprint change.
4. **Responsive** — resize hits all three breakpoints (rail collapse at 1100, Elapsed then
   Application shed below 1100); minimum 1000×700 enforced; dragged column widths survive restart.
5. **Wave-3 polish, on the owner's eye** — filled nav icon on the selected view (PR B); view-switch,
   progress, row, expander, transfer-fade motions (PR C1/C2); OS reduce-motion zeroes them; Mica on
   Win11 if available (else the opaque fallback is clean) — note this row cross-references **#11**.
6. **Light + dark** — every above check in both themes; matches the `1f` dark mockups.
7. **README screenshots (#27)** — capture the canonical shots (shell + each view, light+dark) as
   the session runs; these seed the README. Note which surfaces are **visual-regression baseline
   candidates for #13** (do not add baselines here — flag for #13).

**Gate:** the owner running this checklist to completion is the M2-close acceptance. Only then is
**#32 closed** (with the wave). No agent asserts M2 done.

---

## Scheduling & dependencies

- **#67 (fixture consolidation) MERGED to `main` today (PR #90 @ `edf90fc`)** — its shared
  `HeadlessAppFixture` (`FakeTimeProvider`-by-construction + deterministic `QueueUiDispatcher` drain,
  replacing the ~7 near-duplicate `MakeView`/`MakeVm` copies and the real-time `Wait.UntilAsync`
  ceiling) lives on `main`. **This plan branch does NOT contain it** — it forked from `786f823`,
  before #90; that is expected for a docs-only PR, and `HeadlessAppFixture` is reached by the
  **execution chips, which branch from `main`** *after* this plan merges (not from this branch).
  **PRs C1 and C2 add headless motion tests over the view fixtures → they reuse `HeadlessAppFixture`
  (write no new per-view fixture copies).** So the C1/C2 dependency is **satisfied on `main`**, not a
  pending blocker. (#13/#88 also sequence after #67, per project status.)
- **PRs A, B, and E do not touch the view fixtures** (Mica policy has its own unit test; the
  icon-swap and rail-collapse tests target the shell nav, not a data view) → they can proceed in
  parallel with / ahead of #67.
- Suggested order: **A, B, E, F (parallel — shell-level + DEBUG tooling, independent of #67) → C1 →
  C2 (both on the merged #67 fixture) → [PR F must be in place] → owner walkthrough → close #32.**
  (PR D deferred to M3 / #96.)
- **Isolation:** any two execution chips that run in parallel get **separate worktrees** (git-index
  race rule); the controller integrates. Verify `git branch --show-current` before every controller
  commit after a chip runs.
- Standard per-PR gate: `dotnet test` green on all three CI legs + Codex clean on the **final**
  commit (re-poll detail threads ≥60 s), then autonomous merge (small-PR cadence). Every PR that
  changes a visual surface additionally requires the **owner-eyeball** rendition before merge — the
  controller surfaces the one artifact and waits.

---

## Risks & open judgment calls

- **Mica is region-scoped; no canvas retoken** (PR A): the design prescribes Mica on the nav +
  command-bar regions only, content always opaque. `#202020` is the *region* fallback and **already
  exists** as `LatticeNavSurfaceBrush`; the content canvas `LatticeCanvasBrush` (`#1F1F1F`) is a
  different surface and stays. So there is **no** whole-window tint and **no canvas retoken** — PR A
  toggles the existing region surfaces transparent/opaque on the Mica state. One open design-fidelity
  check (not a blocker): whether the command-bar fallback should be `#202020` (design) vs its current
  `#292929` — confirm with design/owner at execution, flag don't silently change.
- **Compact grouped rail** — owner ruled **defer to M3** at joint review (issue #96); no longer a
  Wave-3 judgment call.
- **FA default transitions** (PR C2 task 4): treat "FluentAvalonia already animates InfoBar/dialog
  to `1h`" as a claim to **verify via avalonia-docs**, not assume — a "framework already covers X"
  comment is an unverified claim until checked (memory: PR #84 lesson).
- **No motion the owner already rejected** — DataGrid row hover is intentionally instant (#74
  visual verdict). Executors must not "restore" it from the spec; the spec deviation is recorded
  and deliberate.
