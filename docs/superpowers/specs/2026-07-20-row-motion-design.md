# Row Enter/Remove Motion for the Data Grids — Design (#112)

**Goal:** decide whether and how to build the row enter/exit motion family (design card `1h` / M2 spec §8) for the Tasks / Transfers / Projects grids: row-enter fade 100 ms, row-remove fade 150 ms accelerateMid, transfer completed-row fade-then-remove — without breaking the #24/#86 pure-diff reconciler invariant, and without repeating the failure modes the cut C2 prototype already surfaced.

**Status: design only.** No product code ships with this document. Per the issue's own reconsideration trigger (owner YAGNI ruling, #112 comment), the recommendation section is allowed to conclude "don't build" — and does. If the owner approves a build direction, an implementation plan is a separate step.

**History this design must not re-litigate:**
- The family was cut from M2 twice: first split out of #32 PR C2 (removal side), then the enter side was cut too (C2 shipped only the chevron retime). A row-enter-fade + reduce-motion prototype (commit `ed61223` on the deleted C2 branch) was **never merged and is not reachable from any ref** — do not plan around recovering it. Its two Codex findings survive in the salvaged-constraints comment on issue #112 and are restated *in full* as constraints C1/C2 below; this document plus that comment are the durable record.
- The owner rejected grid hover motion once already (#74): DataGrid row hover is deliberately instant ("applied INSTANTLY (no fade)", src/Lattice.App/Theming/DataGridStyles.axaml:153). Dense-grid strobe is a real, previously-exercised owner sensitivity, not a hypothetical.

---

## Part 1 — Current state (verified against shipped code, 2026-07-20)

### 1.1 The rendering pipeline every candidate must live inside

Every data-view VM follows the same shape (`TasksViewModel.Rebuild`, src/Lattice.App/ViewModels/TasksViewModel.cs:185; ProjectsViewModel.cs:143; TransfersViewModel is the Tasks shape minus columns):

1. `ViewSliceProjection.Compute` → per-host rows merged into a flat target array.
2. `Reconcile.alignToExisting` — surviving keys keep their current source slots, so the diff **emits no `Move` by construction** (grid-bound collections must never see a Move; Avalonia 12.1's `DataGridCollectionView` silently drops it — contract documented at CollectionReconciler.cs:47).
3. `Reconcile.diff existing target` — pure keyed diff. **Removals are re-derived every rebuild**: any key in `existing` not in `target` gets a `RemoveAt`, back-to-front (Reconcile.fs:26–37). Postcondition: applying the ops to `existing` yields `target` *exactly*.
4. `CollectionReconciler.Apply` mutates the `ObservableCollection<RowHolder<..>>`. Holders are mutable-identity wrappers (RowHolder.cs) — selection survives value-change polls because `Update` swaps `Data` in place.
5. The grid binds a `DataGridCollectionView` over the holders; the **view owns display order** (#86). Steady-state polls raise zero `CollectionChanged` (#24).

`Rebuild` runs on every store change, every scope/filter change, **and every UI-clock tick** (TasksViewModel.cs:174 — freshness text). Rebuild frequency is therefore ~1/s minimum, higher under poll churn.

Projects expansion is not a view-side filter: `ToggleExpand` → `Rebuild` → child rows **enter or leave the collection as `Insert`/`RemoveAt` ops** (ProjectsViewModel.cs:107–112, 166–173). An "expand reveal" and a "data arrival" are indistinguishable at the reconciler: both are Inserts.

### 1.2 Motion that shipped, and motion infrastructure that did NOT

| Surface | State | Evidence |
|---|---|---|
| View switch 150 ms decelerateMid (fade + 8 px rise) | shipped | `FadeSlidePageTransition.cs` — includes the "motion never gates data" structural argument |
| Progress fill 200 ms width + recycle snap | shipped | `ProgressFillBehavior.cs` (#103) — the recycle-safe row-visual precedent |
| Projects chevron rotate 100 ms | shipped | ProjectsView.axaml:44–50 — "the only surviving card-1h motion in C2" |
| Row-enter fade, row-remove fade, transfer fade-then-remove | **not shipped** | cut from C2; the enter-fade prototype was never merged (salvaged findings: issue #112 comment) |
| **Reduce-motion gate** | **does not exist anywhere** | grep: only a cut-note comment in ProjectsView.axaml:48. Avalonia 12.1 exposes **no OS reduce-motion signal** (verified against the 12.1 assemblies during C2), so the trigger must be a Settings toggle persisted in UiState. |

Consequence: the reduce-motion gate is a **fixed cost of entry** for any candidate that ships any row motion — the issue's acceptance shape requires it to zero the exit fade, and it does not exist to be reused.

### 1.3 Salvaged constraints (Codex findings on the cut prototype — priors, do not re-derive)

- **C1 (was P2): enter triggers cannot be inferred from collection shape.** "Fade any insert when ≥1 prior row survives" mislabels view-local rebuilds: clearing a Tasks filter, or switching single-host → All-hosts with shared survivors, reintroduces *known* rows as Inserts → old rows fade in as if new → the exact strobe the gate exists to avoid. **The discriminator is per-view intent, supplied by the caller/VM** — Tasks/Transfers want genuine *data arrivals*; Projects wants a *view-local expand reveal*. No reconciler-level heuristic can satisfy both.
- **C2 (was P3): row visuals must survive recycling.** A fire-and-forget animation started on a `DataGridRow` keeps running if the row is recycled inside the animation window; fast scroll/sort right after an insert leaves an *unrelated* row briefly transparent. Any row-motion design needs per-row cancellation on rebind — reset opacity first, then (and only for a genuinely entering/exiting holder) play. The shipped pattern to generalize is `ProgressFillBehavior`'s transitions-off-for-one-dispatcher-turn on `DataContextChanged`, plus `RowClassBinder`'s holder→row subscription lifecycle (apply on load, re-apply on Data swap, unsubscribe on unload/recycle/detach).

### 1.4 The conflict, stated precisely

Exit motion requires a row to **linger visually while it is logically gone**. The shipped contract has no such state:

- `Reconcile.diff` re-derives removals from scratch every rebuild (1.1 step 3). Keeping a fading row in the bound collection means the next rebuild's `existing` contains a key absent from `target` → a second `RemoveAt` for the same key, this time actually destroying the row mid-fade — or, if the key reappears (a transfer retry), an `Insert` alongside the still-present holder → duplicate key, violating diff's precondition.
- Equivalently: the bound collection is the diff's *output domain*. Anything rendered must be in it; anything in it is diffed. A fading-but-dead row must therefore be **in the diff's target**, or the diff must stop being the single authority over the collection. Those are the only two places a design can go, and they separate the candidates below.

---

## Part 2 — Intent taxonomy (which view animates what, and why)

Per constraint C1 the trigger is per-view intent. Each `Rebuild` call site declares *why* it is rebuilding; only specific reasons arm motion. Everything else — scope switch, filter change, initial load, clock tick, host add/remove — is **instant, by design**, because those are bulk view-reconfigurations where fading is mislabeling (C1) and dense-grid strobe (#74).

| View | Enter fade (100 ms, opacity only) | Exit fade (150 ms accelerateMid, opacity only) | Why this intent |
|---|---|---|---|
| **Tasks** | *Data arrival*: a poll delta adds a task on a host that was already contributing rows before this rebuild | *Data departure*: a task leaves the daemon's result set (completed & reported, aborted) | New work arriving / finishing is the event a monitoring dashboard exists to surface; a fade marks it as an event rather than a repaint. First snapshot of a host is a bulk load, not N arrivals. |
| **Transfers** | *Data arrival*: same rule as Tasks | *Data departure*, notably **completed-transfer fade-then-remove** (spec §8's named case) | The exit is the semantically loaded half: today a finished transfer vanishes between two frames, indistinguishable from a glitch. This is the one motion in the family with informational value, not just polish. |
| **Projects** | *Expand reveal*: child rows inserted by `ToggleExpand` for that group only | none (collapse is instant; detach is rare and stays instant) | Expand is a user-initiated reveal — motion confirms causality. Collapse wants immediate feedback; fading out children after the chevron already flipped reads as lag. Attach/detach churn is rare enough that animating it buys nothing (and M3's attach flow lands rows via a bulk state refresh anyway). |
| **Event log** | none | none | Streaming log with Following-scroll semantics; opted out in the prototype and stays out. |

Mechanics of the discriminator (design-level, all VM-local — no `HostStore` contract change needed): each rebuild carries a reason (`StoreDelta | ViewConfig | Tick | ExpandReveal(group)`); "host was already contributing" is decidable from the previous rebuild's `CoveredIds`, which the VMs already compute per rebuild via `ViewSlice`. Only `StoreDelta` (Tasks/Transfers) and `ExpandReveal` (Projects) arm motion; `ExpandReveal` arms it only for the toggled group's children.

---

## Part 3 — Candidate architectures for the exit side

The enter side is invariant-neutral in every candidate (an entering row is genuinely in the collection; motion is a one-shot row class derived from its row value) — the candidates differ only in how a *departing* row lingers. Verification-lane framing used below: this family is the **weakest verification surface in the project** (visual feel has no machine acceptance signal; owner-eye is the merge gate for fidelity, one version, no iteration), so each candidate is scored on how much of it is machine-checkable and how much lands on the owner's eye.

### Candidate A — tombstone layer as a pure visual-target composition (two layers, one diff)

**Shape.** Keep the diff as the single authority, but make its *target* the visual layer. A pure F# module (sketch: `RowExits`) owns the exit set. Two Codex P2s on this PR (R1: the marker channel, R3: the tombstone's data source) both caught the same class of gap — a prose promise with no input carrying it — so per the repeated-finding rule the shape is now stated as the complete data-flow relation of a rebuild, from which the signature and every input follow; nothing below relies on data the relation does not name:

```
// Today (per VM Rebuild):
//   existing     = Rows |> project (holder -> struct(Key, Data))     // already computed for diff
//   liveTarget   = slice |> view shaping (filter / slots)
//   apply(diff(existing, alignToExisting(existingKeys, liveTarget)))
//
// Candidate A inserts one pure call between those lines:
//   (visualTarget, exits') =
//       RowExits.step reason now fadeLength existing liveTarget exits
//   apply(diff(existing, alignToExisting(existingKeys, visualTarget)))
//
// where  exits : Map<'Key, Exit>,  Exit = { Deadline: DateTimeOffset }
//   departed     = keys existing \ keys liveTarget \ keys exits      // newly gone this frame
//   exits'       = (exits restricted to un-expired, minus reappeared keys)
//                  + (departed ↦ { Deadline = now + fadeLength }, when reason arms exit motion;
//                     ViewConfig/scope reasons flush the set instead)
//   visualTarget = liveTarget ⊎ [ for k in exits' -> k, markExiting (rowOf existing k) ]
```

The tombstone's frozen data is `existing`'s row value for that key — the same holder→value projection `Rebuild` already builds for `diff`, so the pure boundary sees the departed row's last displayed value without any reliance on UI state (the R3 fix: `existing` is an explicit input, not an assumption). Expired entries drop out of `exits'`; a reappearing key leaves it (its live row wins — the holder never left the collection, so identity and selection survive the round-trip). The downstream pipeline — `alignToExisting`, `diff`, `Apply`, view-owned order — runs **unchanged on `visualTarget`**: the diff still yields its postcondition exactly; the applier and `Reconcile` are not touched at all.

**Motion state lives in the row value, not on the holder** (adopted from a Codex P2 on this design PR): nothing in the unchanged diff/applier can set a side-channel `IsExiting` holder flag — if the frozen tombstone equals the current holder data, the departure frame's diff emits no `Update` at all, so no holder property changes and `RowClassBinder` never re-fires. The exit (and enter) marker therefore becomes a field of the per-view row record itself (`with { Motion = Exiting }` on the frozen copy): the marker change *is* a data change, so the departure frame structurally yields `Update(i, key, markedRow)` → in-place `Data` swap → `RowClassBinder` re-applies → row class → opacity transition in XAML. Reappearance clears the marker the same way; `RowHolder` needs no change at all. This is the repo's standing fix-class — the invariant moves into the data model instead of being compensated downstream (the row records are per-view types, so the field costs nothing generic; XAML binding paths and the view sort comparers read the same record and are unaffected by design — confirming that is an implementation-plan detail). Expiry needs one scheduled rebuild at the earliest pending deadline (the 1 s heartbeat alone would leave an invisible row holding a slot for up to a second — a visible late jump in a dense grid).

**Preserves.** The full #24/#86 contract: single source of truth, no Move, removals re-derived per rebuild (a tombstone *is* removed by an ordinary `RemoveAt` the rebuild after its deadline — "no leak" is the module's postcondition, not a cleanup callback's good behavior). Data-first is structural: `Apply` is synchronous inside `Rebuild`; the fade is a style transition after the fact, same argument as `FadeSlidePageTransition`. All semantic reads (counts, at-risk, partial bar, overlays) keep consuming the *live* slice — tombstones exist only between `step` and the collection, so they cannot contaminate any summary.

**Breaks / bends.** One honest bend: the bound collection now renders the *visual* layer, so "the grid renders exactly what the live data says" weakens to "…what the pure composition of live data + exit set says". The invariant survives in the form that mattered (`diff`'s postcondition against its target; unique keys; no Move; identity preservation), but the #24 doc-comment contract gains a clause. Duplicate-key safety on reappearance is by construction (a key is in `liveTarget` or `exits`, never both).

**Verification-lane cost.** Best of the three, and the reason this is the only build-worthy shape. Machine-checkable (F# + FsCheck over `step`, per the left-shift canon): visual = live ⊎ exits with disjoint keys; every exit expires by its deadline (no leak); a tombstone's row value is the departed key's last value in `existing`, marker aside (the R3 P2 pinned as a property); reappearance cancels and preserves identity; `ViewConfig` reasons flush; steady-state fixpoint (no churn when nothing changed); the departure frame emits an `Update` carrying the exit marker (the R1 P2 pinned as a property). Headless: motion-marker → row-class wiring, recycle snap (C2, #103 pattern), eventual `RemoveAt` under a fake clock, reduce-motion zeroing durations. Existing reconciler tests stand untouched. Lands on the owner's eye: fade feel/curves, dense-grid churn under real poll cadence, the opacity-0 gap before the slot collapses. That residue is irreducible for any motion — A adds no *extra* eye-only surface.

**Blast radius on the reconciler contract.** Zero code change in `Reconcile.fs` / `CollectionReconciler.cs` / `RowHolder`. One new pure module, a motion-marker field on the per-view row records, and a rebuild-reason parameter threaded through three VMs' `Rebuild` entry points. Contrast with the interception variant the issue sketched (applier suppresses `RemoveAt` and fades the holder): that one puts the visual layer *inside* the applier, desynchronizes every subsequent op index (`diff` indices assume the removal happened), and demands logical→visual index translation in `Apply` — re-opening exactly the surgery #86 closed. A exists to make that variant unnecessary; it is rejected, not proposed.

### Candidate B — exit-snapshot overlay (ghost layer above the grid)

**Shape.** Let the removal happen instantly (pipeline untouched, byte-for-byte). On a data-departure `RemoveAt`, capture the departing row's rendered visual (`RenderTargetBitmap` of the realized `DataGridRow`, when realized), place it in an overlay at the row's last viewport bounds, fade it 150 ms, discard.

**Preserves.** The entire reconciler contract, trivially — no second layer exists in the data at all.

**Breaks.** The visual truth instead. Rows below shift up *immediately* (the slot is gone), so the ghost fades **on top of the row that slid under it** — overlap, the least Fluent-looking possible outcome; card `1h`'s "row remove 150 ms" clearly means fade-in-place-then-collapse, which B cannot express. The ghost is viewport-anchored while the grid keeps scrolling/sorting under it. A virtualized, scrolled-out row has no realized visual to capture, so off-screen departures silently differ from on-screen ones. Per-removal bitmap capture on a 500-row grid reconciling every poll is also the wrong cost curve.

**Verification-lane cost.** Worst of the three. The machine can assert "a ghost appeared and was disposed"; *everything that can actually go wrong* — placement, overlap, scroll desync, DPI — is pixel-geometry under virtualization, i.e. the owner's eye plus fragile headless-Skia geometry probing. It also depends on `DataGridRow` bounds/realization internals (upstream churn exposure — the failure-mode-locality razor says pack risk into pure functions, not into owned compositing over framework internals).

**Blast radius.** Zero on the reconciler; a new owned UI-chrome subsystem (overlay adorner, capture, positioning) instead — unbounded-failure territory this all-native-components project has deliberately stayed out of. **Rejected.**

### Candidate C — enter-only (no exit motion; removals stay instant)

**Shape.** Ship only the invariant-neutral half: intent-gated enter fades (Part 2's enter column) via the same row-value motion marker, recycle-safe per C2, plus the reduce-motion gate. Exit motion: not built; #112's exit half closes as won't-build.

**Preserves.** The reconciler contract untouched — no tombstones, no second layer, nothing lingers.

**Breaks.** Nothing structurally; it just delivers the least valuable half. Part 2's own analysis says the *exit* is where the informational value lives (a completed transfer vanishing without trace); enter fades on a monitoring grid are closer to pure polish — and polish on the exact surface where the owner already rejected motion once (#74). C is what "do the easy part" looks like, not what the product asks for.

One more honest note: enter fades still interact with the 1 s heartbeat pipeline — `Rebuild` runs every tick, so the intent gate (Part 2) is what keeps tick rebuilds motion-free; C needs that machinery at full fidelity, same as A.

**Verification-lane cost.** Low: headless class/wiring/recycle tests plus the same irreducible owner-eye feel check. But the fixed cost of entry (reduce-motion gate: Settings toggle + UiState persistence + root class + zeroed-duration styles) is identical to A's, so C costs a large fraction of A while foregoing the one semantically motivated motion.

**Blast radius.** Rebuild-reason threading through the VMs (same as A), `IsEntering` flag, XAML styles. No aggregation-layer change.

---

## Part 4 — Recommendation

**Recommend: don't build now (option D — status quo).** Keep this document as the design of record; if the reconsideration trigger fires, build **Candidate A** (which subsumes C's enter machinery), never B.

The issue itself defines the trigger: **a real user request** (owner YAGNI ruling, #112 comment). None exists. The family has been cut twice on exactly this reasoning; nothing material has changed since, and this design exercise found no hidden cheapness — honest costing below says a disciplined build is a mid-size feature.

**Honest cost of building A** (order of magnitude, at project discipline — Codex rounds, red-first, headless gates, owner visual gate): roughly 3–4 PRs. (1) reduce-motion gate build (from scratch — the unmerged prototype is unrecoverable, see History; the durable facts are that Avalonia 12.1 has no OS signal, so it is a Settings toggle persisted in UiState driving a root style class); (2) `RowExits` pure module + FsCheck invariants + rebuild-reason threading; (3) row classes / recycle-snap behavior / XAML across three views; (4) the owner-eyeball visual PR (one version, no iteration). Comparable to a meaningful slice of M3 — spent on cosmetics for a surface whose only motion verdict so far was a rejection. The single-PR prototype of a *subset* of this drew two P-level findings; that is evidence about this family's review cost, not bad luck.

**Minimal slice if the owner wants one anyway:** Transfers-only exit fade — Candidate A wired into one VM, enter side skipped. It targets the sole motion with informational value (completed transfer vanishing without trace) on the lowest-churn, lowest-row-count grid, cutting the #74 strobe exposure. Cost floor stays ~2–3 PRs because the reduce-motion gate and the recycle-safe motion plumbing are fixed costs of any first motion; that fixed cost is the strongest argument for D.

### Owner decision framing

**(a) Product-language consequences.**
- *Build nothing (D):* rows keep appearing/disappearing between one poll repaint and the next. A finished transfer vanishes instantly; the event log — not the grid — remains the way to notice it. The app stays exactly as motion-quiet as the version the owner has already accepted; no new Settings toggle.
- *Build A:* new work fades in over 100 ms; finished work fades out over 150 ms and then the row collapses; expanding a project fades its children in. A "Reduce motion" toggle appears in Settings (Avalonia exposes no OS signal). Poll-driven grids gain motion at data-churn frequency — on a busy 500-row Tasks grid, arrivals/departures every few seconds; a risk the taxonomy gates but cannot eliminate.
- *Transfers-only slice:* completed transfers get a visible send-off; Tasks and Projects stay static. Least motion for the most meaning; still ships the Settings toggle.

**(b) Steelmanned alternatives to the recommendation.**
- *Build A now:* the strongest case is that exit motion is a **BOINCTasks-parity-plus differentiator** — completion feedback where the incumbent has none — and building it while the reconciler context is warm is cheaper than rediscovering it in a year. Countered by: no user has asked; differentiator budget is M4's charts/tunnels, which have signal; the design doc *is* the warm-context artifact, so deferral loses little.
- *Build the Transfers slice now:* real informational value, minimal strobe surface. Countered by: it drags in the full fixed cost of entry (motion gate + recycle-safe plumbing) for one grid's exit fade, and it opens the "one version, owner-eye" visual gate for the smallest possible payoff.
- *Build C (enter-only):* cheapest code path if any motion is wanted. Countered above: pays most of the fixed costs, forgoes the only semantically valuable motion, and adds fades precisely where #74 says the owner is strobe-sensitive.

**(c) What would prove the recommendation wrong.**
- A real user request for row motion or completion feedback — the pre-agreed trigger; this doc then upgrades from "don't build" to "build A".
- The owner, using the app on live hosts, personally missing transfer-completion feedback — same trigger, first-party.
- Evidence that A's cost estimate is inflated — e.g. if the reduce-motion gate lands independently for some other motion first, the marginal cost of the Transfers slice drops to ~1–2 PRs and the trade re-opens.
- M4's notification surface (task failures / unreachable hosts via InfoBar/tray) shipping *without* covering transfer completion — if completion events end up with no home anywhere, the grid fade regains product weight.

---

## Non-deliverables

No implementation plan, no task breakdown, no code. If the owner approves a direction (A, the Transfers slice, or C), the implementation plan is a separate document; the F# signature above is a design sketch fixing the module's shape and invariants, not plan-doc code.
