# Handoff: Lattice — M2 (BOINC monitoring desktop app)

## Overview
Lattice M2 is a **read-only, multi-host BOINC monitoring** desktop client. It connects to one or
more BOINC clients over GUI RPC and shows their Tasks, Projects, Transfers and Event log, plus a
Hosts scope in the sidebar and a Settings screen. M2 is read-only (task/project *control* actions
are reserved, disabled placeholders for M3).

This bundle is the **finalized design spec** for M2, authored in the **Fluent 2** design language.

## About the design files
The file in this bundle — `Lattice M2 Spec.html` — is a **design reference created in HTML**. It is
a self-contained, offline prototype that documents the intended look, layout, copy, states and
interactions. **It is not production code to copy.**

The target codebase is a **desktop app (Avalonia / .NET, Fluent-styled — e.g. FluentAvalonia)** built
on the `Lattice.Boinc.GuiRpc` client (Result / Project / Message / connection-state records). The task
is to **recreate these designs in that environment** using its established controls and patterns
(`NavigationView`, `DataGrid`, `ContentDialog`, `InfoBar`, `SettingsExpander`, `CommandBar`, `MenuFlyout`,
`ComboBox`, `TabView`…). Where the spec names a Fluent control, use the codebase's equivalent. If a
target environment does not yet exist, stand up an Avalonia + Fluent project and implement there.

## Fidelity
**High-fidelity.** Final colors, typography, spacing, densities, row heights, dividers, hover and
motion are specified. Recreate pixel-faithfully using the codebase's Fluent controls; do not restyle.

## How to read the spec file
Open `Lattice M2 Spec.html` (double-click; works offline). It is a pannable canvas of cards grouped
into three "turns". Each card has a **badge id** (e.g. `1c`, `3a`) shown top-left and greyed
**annotation notes** beneath it — those notes are the authoritative per-screen spec. Card map:

**Turn 3 — Hosts (canonical, supersedes any older placement):**
- `3a` **Hosts rail — final.** Bottom-docked in the NavigationView **PaneFooter**, above Settings.
  Four adaptive states: 1 host (degenerate) · flat list (fits) · status groups (Attention/Healthy/Offline
  when it doesn't fit) · 48px compact. Height-adaptive, not count-based.
- `3b` **Host management — final.** "+" opens the Add-host dialog directly; host rows get a right-click
  **MenuFlyout** (Edit / Test connection / Remove); Settings keeps only global groups.

**Turn 2 — Views (all on the `1c` shell):**
- `2a` **Projects** — the Tasks DataGrid with one adaptation: hierarchical parent/child rows (chevron + indent).
  Header sort is **view-owned** (a custom DataGridSortDescription over the pure `compareRows`, swapped in a
  DataGridCollectionView), so the grid lights its native sort arrows. Only the parent **aggregates** order;
  a group's children always follow their parent, host-ascending and direction-invariant. Ties break on
  MasterUrl, also direction-invariant. Resource share sorts by `(max, min)` of the per-host shares (#57).
- `2b` **Transfers** — active/retrying/queued rows; empty state is the common case.
- `2c` **Event log** — merged stream + Host column; priority filters; follow-scroll; **has a column header
  row** (Time / Host / Project / severity / Message) so columns are drag-resizable.
- `2d` **Settings** — global groups only (Polling · Theme) + the **Add host dialog** spec (fields,
  validation, error copy). Host add/edit/remove lives in the sidebar (`3b`).

**Turn 1 — Shell + Tasks + Foundations:**
- `1c` **Shell C (recommended)** — two-zone left rail (views on top, Hosts scope below); full Tasks view
  chrome: command bar, partial-results InfoBar, selected/at-risk/running/uploading/waiting row states.
- `1d` **Tasks table anatomy** — medium (36px) vs compact (28px) density, type ramp, column-width
  strategy, and the column-detail close-up (full-height dividers + middle-ellipsized task name).
- `1e` **State matrix** — 5 host connection states · view-level populated/empty/loading/partial · first run.
- `1f` **Dark theme** — token-substitution mirror of `1c` (no layout differences).
- `1g` **Tokens** — spacing/size, font fallback stacks, keyboard, semantic colors.
- `1h` **Motion spec** — per-interaction durations/curves.
- `1i` **Responsive spec** — min window 1000×700; rail collapse + column shedding; breakpoint table.

## The DataGrid is the core pattern — Tasks is canonical
Every table (Tasks, Projects, Transfers, Event log, Responsive, Dark) uses **one** DataGrid design.
**Tasks (`1c` / `1d`) is the source of truth**; other views only adapt columns + one structural twist.

- **Header row**: 32px, `Segoe UI` 600 / 11px, color `#616161`, bottom border `#E0E0E0`, sentence-case labels.
- **Body rows**: 36px (medium, default) / 28px (compact toolbar toggle); body text 13px `#242424`, secondary `#616161`.
- **Column dividers**: full-height hairline `#EDEBE9` between columns (quieter than the row rule `#F0F0F0`);
  none on the last column, none on narrow gutter/icon columns.
- **Row hover**: whole row → `#F5F5F5` (neutralBackground1Hover), 100ms fade, `cursor:default`; headers do
  not hover; hover sits **under** state tints (a selected `#EBF3FC` / at-risk `#FFF9F5` row keeps its color;
  press = one step darker). Dark-theme hover = `#383838`.
- **Alignment**: all columns (header + content) left-aligned; numeric columns use tabular figures.
- **Truncation**: the star/Task column takes remaining width and **middle-ellipsizes** (head…tail) so the
  distinguishing segment stays visible — full value on hover tooltip. Other text columns end-ellipsize.
- **Resize**: columns are drag-resizable from the header edge; widths persist per machine.

### Default column widths
Per-view default column widths (px) and the star (`1fr`) column live in the spec itself — see the
**"Default column widths"** note under card **`1c`** in `Lattice M2 Spec.html`. That annotation is the
single source of truth; this README intentionally does not duplicate the numbers (to avoid drift).

## Interactions & behavior
- **Global scope**: the Hosts selection in the rail is an independent, persistent global filter applied to
  every view (separate from NavigationView's view selection — both selected states render at once).
- **Partial results**: when scope = All hosts and ≥1 host is unreachable, show a dismissable `InfoBar`
  (severity Warning) with a "Retry now" link. **Floating-card placement (issues #107/#119 — supersedes
  the docked strip drawn in card `1c`):** the bar is an overlay child of the view's grid Panel, never
  docked in the layout flow — grid geometry is independent of the bar in every state (open, closed,
  absent). It floats at the grid top, **below the column header** (top margin 40 = 32px header + 8px
  gap; the header stays visible and sortable under any overlay — an outage can persist for hours),
  as a **content-hugging centred card**, max width 720px, radius 8 (radiusXLarge), elevation shadow16
  (theme-tuned dark mirror), never a full-width band. It is persistent while the outage lasts and
  dismissable in one click; there is no auto-collapse. Any view that grows a partial bar inherits all
  of this by opting into the shared `partialBar` style class — placement, chrome and motion live in
  the class only, never per view.
- **Host states** (icon + text, never color alone; canonical set is the `1e` matrix): **Connected** (`checkmark_circle`,
  success `#107C10`) · **Connecting…** (`arrow_sync` rotating / ProgressRing, brand `#0F6CBD` — first connect / full
  fetch) · **Retrying in Ns (attempt N)** (`arrow_clockwise`, warning `#B85C00`, countdown ticks every second) ·
  **Unreachable** (`dismiss_circle`, danger `#C50F1F`, tooltip = last error) · **Wrong password** (`key`, danger
  `#C50F1F`, click opens the Edit host dialog per `3b`). ("Offline" in `3a` is a status-*group* label in the
  many-hosts view, not a per-host state.)
- **Add / Edit host** (`ContentDialog`): Address required (accent focus underline), Port prefilled 31416,
  info strip explains the `remote_hosts.cfg` prerequisite; Add disabled until Address is non-empty; submit
  shows an inline ProgressRing; failure → InfoBar. Edit reuses the same dialog retitled, fields prefilled;
  clicking an auth-failed host opens Edit with the password field in error + focused.
- **Polling**: interval 2/5/10/30/60s (default 5s); "Updated Ns ago" is a polling-health indicator that
  turns to warning color + icon on poll failure.
- **Motion** (see `1h`): 100–300ms, enter decelerate / exit accelerate, no bounce, no infinite loops
  (spinners excepted), animation never blocks data updates. View switch 150ms decelerateMid (opacity +
  8px translateY); row enter 100ms (opacity only, no height animation); row remove 150ms accelerateMid;
  progress bar 200ms easeEase; dialog scrim 200ms in / 150ms out; expander chevron 100ms + height 200ms;
  partial-results card enter 150ms decelerateMid (opacity + 4px translateY drop, #119) — exit instant.
- **Responsive** (see `1i`): min window 1000×700. ≥1280 full 260px rail + all columns; 1100–1279 rail
  auto-collapses to 48px; below 1100 shed Elapsed first, then Application (visibility in the overflow menu);
  Host column hidden in single-host scope. Only the grid body scrolls.

## State management
- Per-host connection state + polling loop; merged aggregate for "All hosts".
- Global host-scope selection (persisted per machine).
- Per-view sort (single column; Tasks default Deadline ↑), filter text, density toggle, column widths
  (persisted), expand/collapse state (Projects child rows, status groups).
- Theme (Light / Dark / System).

## Design tokens (Fluent 2, web light theme)
The full token set — semantic colors (light + dark), type ramp, spacing, sizes, radius, and shadows —
lives in the spec itself. See the **Tokens** note under card **`1g`** in `Lattice M2 Spec.html` (which the
dark-theme mirror `1f` reads from). That annotation is the single source of truth; this README
intentionally does not duplicate the values (to avoid drift).

## Assets
- **Icons**: [Fluent System Icons](https://github.com/microsoft/fluentui-system-icons) (MIT) — outlined
  *regular* at rest, *filled* for selected/active. Sizes 16/20. The spec uses the icon font; in Avalonia
  use the equivalent Fluent icon set / glyphs.
- **Font**: Segoe UI (system on Windows; provide the documented fallback stack cross-platform — see `1g`).
- No bitmap/illustration assets; all UI is type + icons + flat Fluent surfaces.

## Files
- `Lattice M2 Spec.html` — the finalized, self-contained HTML spec (open offline; pan/zoom the canvas).
  The greyed annotation notes under each card are the authoritative per-screen details.

_Source of truth in the design project: `Lattice M2 Spec - Final.dc.html`._
