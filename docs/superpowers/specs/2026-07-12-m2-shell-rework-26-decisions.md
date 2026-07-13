# M2 Shell Rework (#26) — Resolved-Decisions Spec

> Companion to the implementation plan `docs/superpowers/plans/2026-07-12-m2-shell-rework-26.md`.
> The **authoritative design** is `docs/design/m2/` cards `3a` / `3b` / `1c` / `2d` / `1f` / `1g` / `1i`
> (greyed annotations). This file records **only the ambiguities that card text did not settle** and
> the resolution I chose, plus the pure-core contract the plan builds against. Where this file and the
> design cards disagree, the cards win — flag it instead of silently diverging.

## 1. Framework facts verified up front (not assumptions)

- **`FANavigationView.PaneFooter` exists in FluentAvalonia 3.0.1.** Confirmed by dumping the shipped
  `FluentAvalonia.dll` (`get_PaneFooter` / `set_PaneFooter` / `PaneFooterProperty` / template part
  `_paneFooterOnTopPane`).
- **`PaneFooter` renders ABOVE `FooterMenuItems`.** Confirmed against FA's `NavigationViewStyles.axaml`
  template: inside `ItemsContainerGrid` the row order is `MenuItemsHost` (Row 0, star) → separator (Row 1)
  → **`PaneFooter` (Row 2)** → **`FooterMenuItemsHost` (Row 3)**. So hosts-in-`PaneFooter` sit directly
  above Settings-in-`FooterMenuItems`, exactly as card `3a` states. The current `PaneCustomContent`
  placement is the rejected "on-top" slot (decision log #7): `PaneCustomContent` is Row 4 of
  `PaneContentGrid`, *above* the menu-items region.
- **Consequence for the flexing gap (`3a`: "MenuItems zone absorbs remaining height"):** the menu-items
  row is the star row and `PaneFooter` is `Auto` and bottom-pinned, so the gap between the last view item
  and the hosts block flexes for free. No filler element is needed (and the design forbids one).

The plan still opens with a **headless geometry probe** (Task 1) that asserts hosts render above Settings
and inside the 48 px compact strip, so a future FA upgrade that reorders the template fails a test rather
than shipping a broken rail.

## 2. Status-group tier taxonomy (card `3a` was internally ambiguous)

Card `3a` annotation prose says **Attention = "unreachable / auth-failed / retrying, always expanded"**,
yet its 40-host mock simultaneously shows an **`Offline · 2`** group *and* a node in `Attention` that is
`Unreachable`. Those cannot both be literally true, so the mock's bucket counts are illustrative, not
normative. The README confirms the direction:

> "'Offline' in `3a` is a status-*group* label in the many-hosts view, **not a per-host state**."

**Owner decision (2026-07-12): ship TWO tiers only — `Attention` and `Healthy`. Do NOT carry a
speculative `Offline` tier.** This is a deliberate YAGNI simplification *over the mock's `Offline · 2`
group*: the mock's Offline nodes fold into `Attention` in M2 (persistently-unreachable hosts are
attention-worthy). No `RailState` distinguishes an "offline/paused/disabled" host from `Unreachable`
today, so an `Offline` tier would be a DU case that nothing ever produces. When M3 adds a terminal
paused/disabled/snoozed state, **that** PR reopens the taxonomy — F#/C# exhaustiveness will flag every
`match`/`switch` to update, which is cheap to add back then.

| Tier | Populated by (over `RailState`, the authoritative per-host enum, spec §9) | Row behavior |
|---|---|---|
| **Attention** | `Unreachable` ∪ `AuthFailed` ∪ `Retrying` | header + always-expanded 36 px host rows |
| **Healthy** | `Connected` ∪ `Connecting` | collapsed 28 px count row; expand state persisted |

- `Connecting` is not a problem (not `Attention`) so it folds into **Healthy** as "on its way up".
- The classifier `RailState -> RailTier` is **total with no wildcard** (CLAUDE.md DU rule). Two distinct C#
  diagnostics apply to a no-`_` enum switch: **CS8509** fires when a **named** value is unhandled (the guard
  we want — a new `RailState` must force a choice here); **CS8524** fires for the residual *unnamed* value
  (an out-of-range cast) even when all named values are covered. So the switch **suppresses CS8524 only**
  (`#pragma warning disable CS8524` at the switch) and keeps CS8509 live — a `_` arm would defeat CS8509 and
  is forbidden. `Lattice.App.csproj` sets no `TreatWarningsAsErrors`, so a local un-flagged build only warns,
  but **CI promotes both to errors solution-wide** via `dotnet build Lattice.sln -c Release -warnaserror`
  (`.github/workflows/ci.yml:26`) — hence CS8524 *must* be suppressed or the plan's own build fails before
  any new case exists. (No repo precedent for a no-`_` enum switch; only CS1591 doc pragmas exist today. The
  F# `RailLayoutPolicy` match is additionally covered by `Lattice.App.Aggregation.fsproj`'s own
  `TreatWarningsAsErrors`.) The renderer skips an empty tier, so an all-healthy farm shows no Attention
  header, and in practice `GroupHeaderRow` only ever collapses **Healthy** (Attention is always expanded).

**Deliberate design deviation to record on the #57 design-fidelity tracker:** the mock's `Offline · 2`
group is intentionally NOT implemented in M2 (folded into Attention, per owner). This is the same
"restructure the taxonomy rather than special-case" posture as `TasksOverlayPolicy`.

## 3. Fit test: pure math in the core, pixel chrome in the shell

Card `3a`: *"available list height = window height − ~290 px fixed rail chrome ⇒ All hosts + ~8 hosts on a
768-high screen …"*. The `~290` and `~8` are **approximate anchors, not a formula to hard-code**. Baking a
window-chrome magic number into the pure core would make it fragile and untestable.

**Split of responsibility:**

- **Pure core** takes an already-measured `AvailableHeight` (the footer's usable budget, px) plus
  `RowHeight` (40 px, `LatticeHostItemHeight`) and decides fit by
  `fits = (hostCount + 1) * RowHeight <= AvailableHeight` (the `+1` is the "All hosts" row). No window, no
  chrome constant, no DPI — trivially FsCheck-testable.
- **Shell** derives `AvailableHeight` from real layout: `AvailableHeight = paneContentHeight −
  ReservedRailChrome`, where `ReservedRailChrome` is a single named constant (`≈150`, covering the Hosts
  header + Settings footer item + paddings). Its exact value is pinned by a **headless geometry test**, not
  guessed, and lives only in the shell. Resizing the window re-feeds `AvailableHeight` → the core
  re-evaluates (card `3a`: "Window resize across the fit boundary re-evaluates").

The core is `AvailableHeight`-driven so tests state heights directly (e.g. "9 rows × 40 = 360 into a 340
budget ⇒ Grouped") with zero pixel-chrome coupling.

## 4. Manual list/group toggle visibility (`ShowToggle`) rule

Card `3a`: toggle "appears only while auto-grouping is in effect", "Single-digit-host users never see the
toggle", "toggling forces flat/grouped, persisted per machine". Resolved to a total predicate:

```
ShowToggle = hostCount >= 2 && (not fits || Override <> Auto)
```

- Single host ⇒ `SingleHost` mode, never a toggle.
- A list that fits under `Auto` ⇒ hidden (nothing to toggle).
- A list that overflows ⇒ shown (auto-grouping is warranted), even if the user has forced flat — so they
  can switch back.
- Any active manual override (`ForceFlat` / `ForceGrouped`) ⇒ shown, so the user can always undo their own
  choice, even after a resize made it fit again.

The toggle's **next override** (opposite layout, with the Auto-return so the toggle can hide once it fits)
is decided by the pure `RailLayoutPolicy.toggleOverride` (§6), never hand-wired in the VM — the toggle
returns to `Auto` exactly when the flip target coincides with what `Auto` would show now. Keeping this in
the core (not a VM `if`) is deliberate: the layout/override/Auto-return decisions belong under
`RailLayoutPolicy`'s transition-table, and the **scope**/selection/persistence decisions belong under the
sibling `ScopeMachine` core (plan Task 6B) — matching the `PartialBarPolicy`/`TasksOverlayPolicy` "born-pure"
canon. No rail or scope decision is hand-wired in the VM; the shell only translates UI occurrences into core
inputs/events.

## 5. Compact (48 px) rail vs. height grouping — orthogonal axes; grouped-compact deferred

Controller decision (do not re-litigate): the 48 px compact rail is the **NavigationView pane-collapse
axis** (width/responsive-driven, card `1i`), **orthogonal** to the height-driven flat↔grouped core. **The
pure core never outputs "compact"** — it outputs `SingleHost | Flat | Grouped` from height + hosts, and the
view renders that. Compact is applied on top by the existing `#Nav.IsPaneOpen` bindings that hide row text
and show icons only.

**M2 scope for the compact + grouped intersection:** card `3a`'s compact vision ("group headers collapse to
nothing; Attention hosts render individually; Healthy/Offline each become one stacked icon; badge → 8 px
dot") needs bespoke stacked-icon rendering. For M2, when the pane is **compact**, the rail renders the
**individual host state-icons** (group-header rows hide their text like every other row; names/subtext/
countdown live in tooltips — current behavior). The pixel-exact "one stacked icon per collapsed group" and
the 8 px badge dot are **deferred to the #32 polish wave** (noted in the plan's Deferred section). This
keeps the pure core clean and M2 shippable; the expanded (260 px) rail implements the full grouped design.

## 6. Pure decision core — name and signature (born pure, F#/FsCheck tier)

Home: `src/Lattice.App.Aggregation/RailLayout.fs` (the existing App-adjacent pure-F# project, consumed by
`Lattice.App`, FsCheck-tested by `tests/Lattice.Aggregation.Tests/` — same pattern as `ViewSlice`).

Naming: the **result record** is `RailLayout`; the **module** is `RailLayoutPolicy` (a type and a module cannot
share a name in one namespace, and `*Policy` matches the repo's pure-decision-module convention —
`PartialBarPolicy` / `TasksOverlayPolicy` / `ColumnVisibilityPolicy`). C# calls `RailLayoutPolicy.compute(...)`.

```fsharp
namespace Lattice.App.Aggregation

open System

/// Status-group tier for the many-hosts rail. Two tiers only (owner decision §2);
/// M3 reopens this when a terminal paused/disabled state exists.
type RailTier =
    | Attention
    | Healthy

/// User's persisted list/group override; Auto lets the height fit test decide.
type RailOverride =
    | Auto
    | ForceFlat
    | ForceGrouped

/// Effective mode after fit test + override resolve.
type RailMode =
    | SingleHost   // exactly one host: no "All hosts", scope pinned to it
    | Flat         // "All hosts" + individual host rows
    | Grouped      // status groups

/// One host projected by the shell from HostStore (registry order preserved).
type RailHost = { Id: Guid; Tier: RailTier }

/// An ordered rail row the shell reconciles into a view-model.
type RailRow =
    | AllHostsRow
    | HostRow of Guid
    | GroupHeaderRow of tier: RailTier * count: int * expanded: bool

/// What the shell measures / persists and hands to the pure core.
type RailLayoutInput =
    { Hosts: RailHost[]        // registry order
      AvailableHeight: float   // measured footer budget, px (decisions §3)
      RowHeight: float         // 40px (LatticeHostItemHeight)
      Override: RailOverride
      HealthyExpanded: bool }

/// The layout the shell renders.
type RailLayout =
    { Mode: RailMode
      ShowToggle: bool         // decisions §4
      Rows: RailRow list }

module RailLayoutPolicy =
    /// Total and pure over the input. Group order is fixed Attention → Healthy;
    /// an empty tier is skipped; within a tier, registry order is kept.
    /// Attention is always expanded; Healthy honors the persisted flag.
    val compute : RailLayoutInput -> RailLayout

    /// Next override for the list/group toggle (§4): the opposite of the current
    /// effective layout, collapsing to Auto when that target equals what Auto would
    /// produce now (so a resize-to-fit can hide the toggle). Total, no wildcard.
    /// The VM delegates here instead of re-deriving fit/override logic.
    val toggleOverride : RailOverride -> RailLayoutInput -> RailOverride
```

Contract highlights the transition-table tests pin:
- `Hosts.Length = 1` ⇒ `Mode = SingleHost`, `Rows = [HostRow id]`, `ShowToggle = false` (no `AllHostsRow`).
- `Mode = Flat` ⇒ `Rows = AllHostsRow :: hosts in registry order`.
- `Mode = Grouped` ⇒ `Rows = AllHostsRow :: (per non-empty tier: header :: (expanded ? tier hosts : []))`.
- `Override` resolution + `ShowToggle` per §4; fit per §3.
- **Row conservation:** every `HostRow` id appearing in `Rows` is a host from the input, and in `Flat`/
  expanded-`Grouped` every input host appears exactly once.

## 7. Persistence keys (UiStateStore)

Added to `UiState` (positional record, new params **appended with defaults** so older `ui-state.json`
files deserialize to defaults — STJ uses the param default for a missing JSON member):

| Field | Type | Default | Meaning |
|---|---|---|---|
| `RailGrouping` | `RailGroupingMode` (`Auto`/`Flat`/`Grouped`) | `Auto` | maps to F# `RailOverride` |
| `RailHealthyExpanded` | `bool` | `false` | Healthy group expand state (the only collapsible tier) |
| `Theme` | `AppTheme` (`Light`/`Dark`/`System`) | `System` | app theme (card `2d`/`1f`) |
| `ScopeHostId` | `Guid?` | `null` | persisted global host scope; null/absent = All hosts. **Design requires this**: README:80 ("independent, persistent global filter") + README:108 ("Global host-scope selection (persisted per machine)"). Unknown/removed id on load → All-hosts fallback. Writes are decided by the pure `ScopeMachine.step`, not the shell: **only `ExplicitSelect` persists an id** (or clears it for All hosts), and the **invalidation paths** clear a stale id (a scoped host removed, R11; an unknown saved id on restore, R10). There is **no** single-host auto-pin: `SingleHost` is presentation-only (highlights the sole host, `Scope` stays All hosts — data-identical), so nothing is written for a lone host unless the user explicitly clicks it. See the plan's Task 6B (`ScopeMachine`) — the born-pure core that closes the R5/R9/R10/R11 scope-finding class. |

Enums persist as **strings** via `JsonStringEnumConverter` added to `UiStateStore.JsonOptions` (robust to
member reordering, human-editable). `RailGroupingMode` / `AppTheme` are C# enums in
`Lattice.App.Infrastructure`; the shell maps `RailGroupingMode` → F# `RailOverride`.

## 8. Host-management surface moves (card `3b`)

- Add & Edit share one `FAContentDialog` (`AddHostDialog` + `AddHostViewModel`), now mode-aware
  (`HostDialogMode.Add | Edit`). Edit prefills fields, retitles to "Edit host", primary button "Save"
  (calls `HostRegistry.UpdateHost`), adds a **secondary "Test connection"** button (never closes; runs the
  same `HostMonitorManager.TestConnectionAsync`, shows result inline), and supports opening with the
  password field in **error state + focused** (auth-failed deep link, card `3b` / §4 of card `3a`).
- Host rows get a right-click **`MenuFlyout`**: Edit host… / Test connection / Remove host… (32 px items;
  Remove is danger `#C50F1F` = `LatticeDangerFgBrush`; icon+label). **No** menu on the `All hosts` sentinel
  or group-header rows.
- Remove confirmation is a `FAContentDialog` (the exact single-flight machinery currently in
  `SettingsView.axaml.cs` moves to the rail/shell path, unchanged).
- Settings loses its Hosts group entirely (`HostSettingsItemViewModel` deleted); it keeps **Polling** and
  gains **Theme**, plus a one-line pointer caption ("Hosts are managed from the sidebar…"). The auth-failed
  rail click no longer routes to Settings.

## 9. Deferred to #32 polish wave (noted, not built here)

- Compact grouped rendering: stacked single-icon per collapsed Healthy group; 8 px badge dot (§5).
- Filled/selected rail icons and per-host `InfoBadge` refinements beyond current behavior.
- #11 Mica (opaque `LatticeCanvasBrush` over Mica) — separate on-hardware pass, out of scope here.
