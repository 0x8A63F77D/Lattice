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
  fidelity. This applies to every transition timing, easing, filled-icon look, and Mica tint.
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

| Deferred-visual item (issue #32 / decisions §9) | Status after #84/#74/#87 | Evidence (current tree) | Verdict |
|---|---|---|---|
| **InfoBadge (nav counts)** | **DONE (core)** | `FAInfoBadge` bound to Event-log unread — `Views/ShellWindow.axaml:113-115`; `EventLogViewModel.cs:58` computes the count; `ShellViewModel.cs:158`. | **Dropped from core scope.** Only "per-host InfoBadge refinements beyond current behavior" (decisions §9) could remain, and the design package specs a badge only on the Event-log nav item — no per-host nav badge is specified. Treated as **not required**; if the owner wants more during the walkthrough, file a follow-up. |
| **Window-width breakpoints** | **DONE** | `Views/TasksView.axaml.cs:48-188` sheds Elapsed→Application on **window-width** thresholds (≥1280/1100/1000, card `2f`/`1i` authoritative — comment cites the window-width-not-pane-width fix, PR #28); rail auto-collapse to 48px via `ShellWindow.axaml.cs:73` Nav-bounds subscription; `MinWidth=1000 MinHeight=700`. | **Dropped from scope.** Design sheds only Tasks columns; other grids have no shed list. Residual is a *walkthrough check item* (resize hits all breakpoints), not code. |
| **Filled nav icons (selected/active)** | **MOSTLY DONE** | The four **view** items already swap outline→filled at runtime: `ShellWindow.axaml.cs:228-241` `UpdateMenuIcons()` resolves `IconFilledKey` when selected / `IconKey` otherwise, called on first render (`:85`) and on every selection change (`:222`); covered by `ShellRailTests`. The XAML `*Regular` `IconSource` (`ShellWindow.axaml:87-112`) is only the initial value the code-behind overwrites. **Gap:** the **Settings** footer item (`ShellWindow.axaml:242-245`) hard-codes `IconSettingsRegular` and sits outside the `_shell.Views` loop, so it never fills when Settings is active. `IconSettingsFilled` is already defined. *(Reconciliation corrected per Codex P2 on PR #89 — the original "NOT wired" read grepped only the XAML and missed the code-behind runtime swap.)* | **REMAINS (Settings item only) → PR B.** |
| **Mica-adjacent polish (#11)** | **NOT wired** | `ShellWindow.axaml:12` requests `TransparencyLevelHint="Mica, None"` but `:13` sets an **opaque** `Background="{DynamicResource LatticeCanvasBrush}"` (`Tokens.axaml:7,30` = `#FAFAFA`/`#1F1F1F` solids); nothing reacts to `ActualTransparencyLevel`. #11 comment (2026-07-11) confirms Mica is invisible even on Win11 today. | **REMAINS → PR A** (folds #11's code half). On-hardware verification stays in **#11**. |
| **Motion / transition polish** (spec §11 / card `1h`) | **PARTIAL** | Present: `Connecting` spinner (`ShellWindow.axaml:29` infinite animation), Projects chevron transform 120 ms (`ProjectsView.axaml:44-47`). Deliberately **instant** (owner visual verdict, #74): DataGrid row hover — *no* fade (`DataGridStyles.axaml:143-155`, documented). Missing vs card `1h`: view-switch (`ShellWindow.axaml:248` `ContentControl` has no `PageTransition`), progress-bar width, row enter/remove, transfer completed-row fade-out, expander height, **reduce-motion** gate (none anywhere). | **REMAINS → PR C1 + PR C2** (largest chunk). |
| **Compact grouped rail rendering** — stacked single-icon per collapsed Healthy group + 8 px badge dot (decisions §5/§9) | **NOT built (explicitly deferred)** | decisions §5: M2 renders individual host state-icons when compact; the "one stacked icon per collapsed group" + 8 px dot deferred to the #32 polish wave. | **REMAINS → PR D (optional, owner-call).** Highest custom-draw risk; per the failure-mode-locality razor (memory), entering the custom-draw domain is the weakest link in the AI loop. Scoped small and flagged; the owner decides at concept-summary time whether it ships in M2 or defers to M3. |

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

The Mica fix is a **derivation**, not inline reactive glue: *given the window's requested
transparency and the level the OS actually granted, choose the window background* (transparent/
tinted when Mica is live, opaque canvas otherwise). That is exactly the small, exhaustively-
testable decision the repo extracts into a policy — and #11's own acceptance shape demands an
**automated assertion** on it. Naming it at plan time keeps the risk in a pure function
(bounded failure: wrong output, machine-checkable) instead of scattered `ActualTransparencyLevel`
handlers (unbounded, eyeball-only) — the failure-mode-locality razor.

Home: `src/Lattice.App/Infrastructure/MicaBackdropPolicy.cs` (pure static, no Avalonia-visual
dependency beyond the `WindowTransparencyLevel` enum it maps). **F# declined** with the same
rationale as shell-design §7: a 2–3 case map over an Avalonia framework enum does not pay a
cross-project translation layer, and `Lattice.App.Aggregation` must stay Avalonia-free. Contract
(the executor writes the body; this is the input→output the headless test pins):

```
// input:  the level the platform reports as ACTUALLY applied (WindowTransparencyLevel)
// output: BackdropChoice { bool UseTransparentBackground; string BackgroundBrushKey }
//   Mica                -> transparent content backdrop; brush key = the tint token (design 1g §Mica)
//   None / any opaque   -> opaque fallback; brush key = "LatticeCanvasBrush" (today's behavior)
// Total, no wildcard on the handled levels; the residual (Blur/AcrylicBlur/Transparent) maps to
// the opaque fallback with a boundary comment (M2 requests only "Mica, None", so only those two
// are reachable — the others fold to fallback, never to a broken transparent-without-Mica state).
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
`HeadlessAppFixture` from #67** (see Scheduling). PR D is owner-optional and last.

### PR A — Mica backdrop wiring (folds #11 code half)

**Goal:** make Mica actually visible when the OS grants it; keep the exact opaque look everywhere
it doesn't. Closes the code half of #11.

**Contract-level tasks** (Sonnet-tier; *executor may pick the cheapest sufficient tier; escalate
to Opus on first failed fix*):

1. **`MicaBackdropPolicy`** per the contract above. Born-pure, exhaustive over the reachable
   levels, boundary comment on the residual arm.
2. **Wire `ShellWindow`** to observe `ActualTransparencyLevel` (avalonia-docs: confirm the property
   + change notification shape for Avalonia 12) and apply `MicaBackdropPolicy` output — set the
   window `Background` to transparent (or the design-`1g` Mica tint token) when Mica is live, else
   keep `LatticeCanvasBrush`. Add the tint token to `Tokens.axaml` (light+dark) per design card
   `1g` §Mica — **do not invent a value; read the design token.** If the design package does not
   specify a Mica tint, STOP and surface to the controller (do not guess a tint).
3. **Invariant:** no feature depends on the material being present (CLAUDE.md); when Mica is denied
   the app is pixel-identical to today.

**Machine gate (CORRECTNESS):** headless test on `MicaBackdropPolicy` (Mica→transparent+tint key;
None→opaque+`LatticeCanvasBrush`); headless test that the window resolves the opaque fallback under
the headless platform (which grants no Mica) — i.e. the fallback path is the tested one on CI.

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

### PR D — Compact grouped rail rendering (OWNER-OPTIONAL, decided at concept summary)

**Goal (if the owner opts in):** the compact (48 px) rail renders **one stacked icon per collapsed
Healthy group** and an **8 px status dot** badge, per card `3a`'s compact vision (deferred at
decisions §5).

**Why gated to an explicit owner decision:** this is the wave's only **custom-draw** surface. The
failure-mode-locality razor (memory: *hobby-project-idealized-design*) says the AI verification
loop is weakest on hand-drawn UI, and "no rashly entering the custom-draw domain" is Lattice's
standing posture (all-native-components). The rest of M2 ships without it; M3 may be the more
natural home. The controller presents this trade-off in the concept summary and the owner chooses
**ship-in-M2 / defer-to-M3**. If deferred, file/annotate a follow-up and this PR is dropped.

**Contract-level tasks (only if opted in):** a compact-mode template that collapses a Healthy group
to a single stacked state-icon + 8 px dot; Attention hosts still render individually (decisions §5);
the pure `RailLayoutPolicy` is **untouched** (compact is the orthogonal pane-collapse axis — it must
not leak into the height-driven core).

**Machine gate (CORRECTNESS):** headless — in compact mode a collapsed Healthy group yields exactly
one icon element and one 8 px dot; Attention hosts remain individual; expanded (260 px) rail
unchanged. **Owner gate (VISUAL):** one screenshot of the compact rail → owner eyeball.

---

## Final M2 walkthrough — owner-run acceptance checklist artifact

This is **not** an agent task. It is a live acceptance session the **owner runs** against the test
daemon (BOINC 8.2.11, dedicated test machine), closing the M2 milestone and doubling as the README
screenshot source (**#27**). The executor's deliverable is the **checklist artifact**
(`docs/design/m2/M2-walkthrough-checklist.md`) + the DEBUG sample host enabled for the data-rich
states; the **owner executes it** and its pass is what closes **#32**.

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

- **#67 (fixture consolidation) is in flight** and **#13/#88 land after it**. #67 introduces the
  shared `HeadlessAppFixture` (`FakeTimeProvider`-by-construction + deterministic `QueueUiDispatcher`
  drain, replacing the ~7 near-duplicate `MakeView`/`MakeVm` copies and the real-time `Wait.UntilAsync`
  ceiling). **PRs C1 and C2 add headless motion tests over the view fixtures → they build on the
  consolidated fixture and must sequence AFTER #67 merges.** Writing new per-view headless copies
  before #67 would regrow exactly what #67 consolidates.
- **PR A and PR B do not touch the view fixtures** (Mica policy has its own unit test; the icon-swap
  test targets the shell nav, not a data view) → they can proceed in parallel with / ahead of #67.
- Suggested order: **A, B (parallel, pre-/independent of #67) → [#67 merges] → C1 → C2 → (D, owner-
  optional) → owner walkthrough → close #32.**
- **Isolation:** any two execution chips that run in parallel get **separate worktrees** (git-index
  race rule); the controller integrates. Verify `git branch --show-current` before every controller
  commit after a chip runs.
- Standard per-PR gate: `dotnet test` green on all three CI legs + Codex clean on the **final**
  commit (re-poll detail threads ≥60 s), then autonomous merge (small-PR cadence). Every PR that
  changes a visual surface additionally requires the **owner-eyeball** rendition before merge — the
  controller surfaces the one artifact and waits.

---

## Risks & open judgment calls

- **Mica tint value** (PR A task 2): the exact transparent/tint token comes from design card `1g`
  §Mica. If the design package does not specify it, that is a genuine design gap → **STOP and
  surface to the controller**, do not guess a tint. (Everything else in PR A is settled by #11's
  comment + the policy contract.)
- **Compact grouped rail (PR D)** is a real product judgment call (ship-in-M2 vs defer-to-M3),
  deliberately routed to the owner at concept-summary time rather than guessed here.
- **FA default transitions** (PR C2 task 4): treat "FluentAvalonia already animates InfoBar/dialog
  to `1h`" as a claim to **verify via avalonia-docs**, not assume — a "framework already covers X"
  comment is an unverified claim until checked (memory: PR #84 lesson).
- **No motion the owner already rejected** — DataGrid row hover is intentionally instant (#74
  visual verdict). Executors must not "restore" it from the spec; the spec deviation is recorded
  and deliberate.
