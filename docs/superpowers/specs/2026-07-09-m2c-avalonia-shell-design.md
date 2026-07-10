# M2c — Avalonia Shell & Views: Design

**Date:** 2026-07-09
**Status:** Approved (concept-level decisions user-approved in planning session; technical detail gated by machinery per review-calibration note in §12)
**Visual authority:** `docs/design/m2/README.md` + `Lattice M2 Spec.html` (high-fidelity, final). This spec does NOT restate visual specifications; it records implementation architecture and decisions. Where this document and the design package disagree on visuals, the design package wins.

## 1. Context and goals

M2c delivers the read-only dashboard UI on top of the finished Core layer (PR #7/#8/#9): app shell (NavigationView + host rail), five views (Tasks, Projects, Transfers, Event log, Settings), full state matrix, responsive behavior, and motion — per the design handoff. Everything renders live data from `Lattice.Core` (`HostMonitorManager`, `HostRegistry`, `HostSnapshot`, `ConnectionStatus`, `MessageLog`).

Non-goals: control operations (M3), charts/SSH tunnels/host groups/notifications (M4), any change to `Lattice.Core` public surfaces beyond §7's additive aggregation module, any change to `HostMachine` (see §14 guard).

## 2. Delivery plan — two stages, one spec

User-approved split, chosen for maintainability (contract-first):

- **M2c-1 "shell"** (branch `m2c-shell`, own PR): `Lattice.App` scaffold, theming/token dictionaries, `IUiDispatcher` + `HostStore` (the UI-thread bridge, built and tested in isolation), NavigationView shell + host rail, Settings view, Add-host dialog, first-run empty state. End-to-end usable: add/edit/remove hosts, watch connection states live, change polling interval.
- **M2c-2 "views"** (branch `m2c-views`, own PR): Tasks view first as pattern-setter → extract shared scaffold (view layout, `CollectionReconciler`, status bar, converters) → stamp Projects/Transfers/Event log from it → responsive + motion polish.

The shell↔view contract (§5) is defined here, implemented and frozen in M2c-1; M2c-2 only consumes it. Stage-1 PR merges before stage-2 work starts.

## 3. Project and dependencies

New project `src/Lattice.App` (net10.0, `OutputType=WinExe` — the cross-platform Avalonia convention):

- Avalonia 12.x + `Avalonia.Desktop` + `Avalonia.Diagnostics` (debug only)
- FluentAvaloniaUI 3.x (Fluent 2 theme, NavigationView, InfoBar/InfoBadge, SettingsExpander, ContentDialog, TaskDialog)
- `Avalonia.Controls.DataGrid`. **Decision (2026-07-08): TreeDataGrid is Avalonia Pro (paid) in the Avalonia 12 era; the free MIT package tops out at 11.x. All tabular views use the free DataGrid.** Plan-time verification RESOLVED (2026-07-09): FluentAvaloniaUI 3.0.1 depends on `Avalonia.Controls.DataGrid` and ships its own Fluent-2 DataGrid control themes (`src/FluentAvalonia/Styling/ControlThemes/BasicControls/DataGrid/`) — no separate theme `StyleInclude` needed; only token-level overrides remain (row heights 36/28, header 32/11px, selection tint).
- CommunityToolkit.Mvvm (source-generated `[ObservableProperty]`/`[RelayCommand]`)
- Icons: Fluent System Icons (MIT) — package only the ~35 glyphs named in the design README (subset font or path geometries; decide at plan time with size measurement), not the full family.

Project-wide compiled bindings: `AvaloniaUseCompiledBindingsByDefault=true`, `x:DataType` everywhere, binding errors fail the build (CLAUDE.md rule).

Layout:

```
Lattice.App/
├── App.axaml(.cs)        # composition root: LatticeConfig.Load → HostRegistry → HostMonitorManager
├── Theming/              # token dictionaries (§4) + icon resources
├── Infrastructure/       # IUiDispatcher, HostStore, UiClock (1 s tick), CollectionReconciler (stage 2)
├── ViewModels/           # Shell + 5 views + row VMs; MUST NOT reference Avalonia types
├── Views/                # .axaml + minimal code-behind (view-only concerns)
└── Controls/             # StatusBar (only custom control) + thin-progress-cell restyle
```

Boundary discipline unchanged: no protocol logic in App; ViewModels consume `Lattice.Core` only.

## 4. Theming and design tokens

All color roles, type sizes, row heights, spacing from the design README §Design Tokens land in `Theming/Tokens.axaml` as theme-variant-aware resources (light/dark via `ThemeVariantScope`); FluentAvaloniaTheme provides system accent + variant sync. Rules:

- Views/controls reference tokens only — no literal hex outside `Tokens.axaml`.
- Prefer FluentAvalonia theme resources where the design README maps to one; reference hexes are fallbacks.
- Mica via `TransparencyLevelHint` on Win11 with opaque fallback (design README §Mica); no feature depends on the material.
- Font stacks and the fixed 26px log row height per design README (explicit row height, never font-metric derived).

## 5. Shell↔view contract (frozen after M2c-1)

Three abstractions, all defined in stage 1:

1. **`IUiDispatcher`** — minimal wrapper over `Dispatcher.UIThread` (`Post`, `CheckAccess`). The only thread-semantics entry point ViewModels see. Tests use a synchronous fake.
2. **`HostStore`** — the single class that touches the event/UI thread boundary. Subscribes `HostMonitorManager.{StatusChanged, SnapshotUpdated, MessagesAdded}` (raised on background threads), marshals via `IUiDispatcher.Post`, and maintains on the UI thread: per-host `HostUiState` (status incl. retry deadline, latest snapshot, bounded message buffer ≤ 5000/host) plus derived aggregates (counts for nav badges, reachable-set fingerprint). Exposes plain CLR events/properties consumed by all view VMs on the UI thread only. Handlers passed to Core do nothing but `Post` (Core escalates subscriber exceptions to Retrying — keep them un-throwing by construction). Reentrancy from UI thread into Core public methods is safe (PR #8 reentrancy pins).
3. **`ScopeSelection`** — `AllHosts | Host(Guid)`, owned by `ShellViewModel`, written by the host rail, read by every view; persists across view switches (design rule "selected host scopes ALL views").

Shell composition: NavigationView (Left, 260px, LeftCompact 48px) with view MenuItems + hosts section (custom two-line item template) + Settings footer, per design. Nav counts/badges derive from `HostStore`. A single `UiClock` (1 s `DispatcherTimer`) drives every relative-time/countdown string ("Updated 3 s ago", "Retrying in 12 s", transfer retry countdowns) — row VMs store absolute timestamps and recompute display text on tick; no per-row timers, no animation on these (design rule).

## 6. Data flow (stage 2 core)

One direction: Core event (bg thread) → `HostStore.Post` → `HostUiState` update (UI thread) → view VM projection → DataGrid binding.

- **`CollectionReconciler`** (shared helper, pure diff logic unit-tested): on snapshot arrival, keyed reconcile into the view's `ObservableCollection` — update existing row VMs in place (progress, state, ETA), add/remove only changed identities. Row keys: task = hostId+result name; transfer = hostId+file+project; project rows per §7. Preserves selection and sort stability; avoids full-list resets. All four data views use it.
- **Event log**: `MessagesAdded` appends; per-host ring buffer (≤ 5000); all-hosts scope = time-merged insert into the merged list. "Following" flag controls view auto-scroll; scrolling up pauses it ("Resume following"). No row animation while Following (design rule).
- **Tasks view derived fields**: Application column join (WorkunitName → Workunit.AppName → App.UserFriendlyName, fallback AppName) and deadline-at-risk both come from Core (`SnapshotBuilder` M2b work) — the UI computes nothing domain-shaped.
- Sorting: DataGrid single-column sort over the merged collection; defaults per design (Tasks: Deadline asc; Projects: Avg credit desc).

## 7. Projects aggregation — in Core, flattened DataGrid in UI

**Mechanism (user-approved option A):** parent (project) and child (per-host) rows are one flat collection of discriminated row VMs; the chevron inserts/removes child rows; a `CustomSortComparer` compares (parent sort key, is-child, child key) so children stay glued to their parent under any column sort. Same DataGrid scaffold as every other view. Single-host scope: hide Hosts column and child rows (design rule).

**Aggregation logic lives in `Lattice.Core`** (new pure static module, additive — no existing surface touched): input = per-host snapshots, output = parent/child row models. Merge by `Project.MasterUrl`; credit sums use Host* values only (never User* per design README); resource share "identical → value / differs → Varies · min–max"; three-tier status aggregation (all same / one deviation / mixed). Rationale: CLAUDE.md — "Core owns multi-host aggregation"; fully unit-testable without UI. **F# was weighed** (pure transformation, per the functional-domains directive) **and declined**: a fourth project for one transform doesn't pay its interop/build cost, and `Lattice.Core.Machine` stays decision-core-only by design. Recorded per the directive's note-the-choice rule.

## 8. Views

Visuals, columns, states, copy: design README is authoritative. Implementation notes only:

- **Tasks**: command bar (disabled M3 placeholders, filter AutoSuggestBox, state ComboBox, updated-ago, refresh, density toggle, overflow w/ column visibility); partial InfoBar (§9); virtualized DataGrid ≥ 500 rows; progress cell = restyled ProgressBar (56×3px); status bar custom control.
- **Projects**: §7.
- **Transfers**: same scaffold; progress text "34.2 / 51.7 MB"; retry countdown via UiClock; completed-row fade-out 150 ms then remove.
- **Event log**: filter pills (ToggleButton restyle), search, Following toggle, copy; monospace stack; Host column only in all-hosts scope.
- **Settings**: SettingsExpander per host (address/port/password, Test connection via `HostMonitorManager.TestConnectionAsync`, Save, Remove w/ ContentDialog confirm); Add-host ContentDialog (validation, inline ProgressRing, failure InfoBar); polling interval ComboBox (`LatticeConfig.AllowedPollingIntervals`); auth-failed host auto-expands with password error state.

Keyboard per design (↑↓, Ctrl+F, F5, Ctrl+1..4, Space sort, FocusStroke2 visuals).

## 9. State matrix and partial semantics

- Rail states project Core's seven `HostConnectionState` values onto the design's five visuals (icon+text+color three channels): `Disconnected|Connecting|Authorizing|FetchingState → Connecting`; `Connected → Connected`; `Retrying (attempt < 4) → Retrying`; `Retrying (attempt ≥ 4) → Unreachable` (backoff has reached the 8 s tier, ≈15 s of continuous failure — Core itself never gives up and has no Unreachable state); `AuthFailed → Auth-failed`. The threshold is a UI display tier, not domain state (amendment 2026-07-09, mirrors the I6 projection philosophy).
- Partial InfoBar: scope == AllHosts && ≥1 host unreachable; dismissable, reappears when the reachable-set fingerprint changes; status-bar counts cover reachable hosts only.
- First-connect loading is per-host — connected hosts render immediately, never block on the slowest (design red line).
- Empty states per view; first-run (no hosts) hides the rail hosts section, Settings shows the CTA.
- AuthFailed rail click → navigate Settings + expand that host + password error focus (the one cross-view linkage).
- Polling failure: updated-ago turns warning icon+color; no dialogs.

## 10. Responsive and persistence

- Breakpoints (≥1280 full / 1100–1279 LeftCompact / 1000–1099 drop Elapsed then Application) implemented with **Avalonia 12 container queries** (verified available; purpose-built replacement for width triggers). Minimum window 1000×700.
- User-dragged column widths persist and beat breakpoint defaults (design rule). UI-only preferences (column widths, density, log filter defaults, window bounds) go to a separate `ui-settings.json` beside the Core config — `LatticeConfig` (Core) is not touched by UI concerns.

## 11. Motion

Per design motion table (view switch 150 ms, row enter/remove 100/150 ms, progress width 200 ms, InfoBar in/out, expander, dialog defaults); implemented with Avalonia Transitions/pseudo-class styles; retry countdowns/updated-ago/elapsed ticks explicitly NOT animated; reduce-motion (system setting) zeroes all durations except spinners; animation never delays data (value first, transition cosmetic).

## 12. Testing and quality gates

User review calibration (recorded in memory 2026-07-09): the user reviews concepts and outcomes, not framework internals. Compensating machinery:

1. **ViewModel/unit tests** (xUnit, no Avalonia): drive real Core (`HostMonitorManager` + fake `IGuiRpcClient` factory — same fixture approach as M2b tests) through `HostStore` with a synchronous `IUiDispatcher` fake. Pure logic (reconciler diffs, aggregation §7, scope filtering, countdown formatting) tested directly.
2. **Headless UI tests** (`Avalonia.Headless.XUnit`): app boots, theme loads, navigation switches views, DataGrid materializes rows from a fake-fed store, add-host dialog validation. Run in the normal suite on all 3 CI legs.
3. **Compiled-binding errors = build failures** (already the -warnaserror posture).
4. **Debug sample host**: DEBUG-only injectable host backed by canned snapshots (many tasks, transfers in all states, multi-project) — needed because the live daemon has 0 projects; used for design-fidelity screenshots, 500-row virtualization checks, and the user demo.
5. **Design-fidelity check**: at each stage's final review, controller compares running-app screenshots against the HTML spec section by section (DevTools MCP is paid — manual screenshots).
6. **User demo gate**: each stage PR merges only after the user runs the app on this machine against the live local daemon (BOINC 8.2.11 installed 2026-07-09) and the acceptance checklist below.
7. Standing rules: never print/log RPC passwords (incl. tests); no real sleeps in tests; subagent model policy incl. no-haiku-for-race-pinning; two-stage review per task; Codex before merge.

## 13. Stage acceptance criteria (user-verifiable, plain language)

**M2c-1 (shell):**
1. App launches on macOS; first run shows the "Connect your first host" state per design (rail hosts section hidden).
2. Add host via dialog (localhost + password from `gui_rpc_auth.cfg`) → host appears in rail: Connecting → Connected with "Connected · N tasks" subtext.
3. Quit BOINC Manager → rail shows Retrying with live per-second countdown and attempt number → relaunch Manager → auto-reconnects to Connected. Wrong password → Auth-failed state; clicking it lands in Settings with that host expanded and password field in error state.
4. Settings: edit host, Test connection, Remove (with confirm dialog), polling interval change — all persist across app restart.
5. Light/dark theme follows the system, matches the design package's 1f dark mockups.

**M2c-2 (views):**
1. All four data views render live daemon data (Event log shows real merged messages; Tasks/Projects/Transfers show design-correct empty states while 0 projects attached).
2. With the debug sample host enabled: 500+ task rows scroll smoothly; sorting, filtering, density toggle, column show/hide work; Projects parent/child expand-collapse with correct aggregates ("Varies", mixed status tiers); transfer retry countdown ticks; Event log Following pauses on scroll-up.
3. All-hosts vs single-host scope switches every view's content and the partial InfoBar appears when a host is unreachable under All hosts.
4. Window resize hits the three breakpoints (rail collapse, column drop); minimum size enforced; dragged column widths survive restart.
5. Side-by-side screenshot comparison against the HTML spec accepted by the user.

## 14. Decision log and guards

- TreeDataGrid is paid (Avalonia Pro) → free `Avalonia.Controls.DataGrid` everywhere (2026-07-08, licensing-decided).
- Projects hierarchy = flattened DataGrid, option A (user-approved 2026-07-09).
- Two-stage delivery (user-approved 2026-07-09, maintainability rationale §2).
- Aggregation in Core as C#, F# weighed and declined with rationale (§7).
- Container queries for responsive (Avalonia 12 feature, replaces manual width listeners).
- UI prefs in `ui-settings.json`, not `LatticeConfig` (§10).
- Debug sample host as permanent dev tooling (§12.4).
- **HostMachine guard**: M2c must not touch `src/Lattice.Core.Machine` or `HostMonitor.cs`. If any task appears to need it, STOP and surface to the user — any `HostMachine.Phase` change triggers verification correspondence rule 4 (same-commit I6 projection-table extension) and belongs in its own round.
- Avalonia API discipline: consult the avalonia-docs MCP before writing framework-touching code (CLAUDE.md rule; MCP registered project-wide via `.mcp.json`).

## 15. Risks

- **DataGrid theme fidelity**: RETIRED 2026-07-09 — FluentAvalonia 3.x ships Fluent-2 DataGrid control themes (see §3); only token-level metric overrides remain, done in stage 2 with the Tasks view.
- **Icon packaging**: subset strategy needs a build-time check that all ~35 named glyphs resolve; missing glyph = build failure, not runtime blank.
- **Daemon lifecycle on this machine**: the local client runs only while BOINC Manager runs (`--launched_by_manager`) — acceptance runs must start Manager first; documented in dev-environment memory.
- **Real task data**: still 0 projects attached; the attach follow-up remains open and is NOT a stage gate (sample host covers data-rich states).
