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

### Default column widths (px; star column takes the `1fr` remainder)
- **Tasks**: Project 108 · Application 118 · Task 1fr · Progress 112 · Elapsed 68 · Remaining 74 · Deadline 100 · State 112 · Host 76
- **Projects**: chevron 24 · Project 200 · Hosts 110 · Resource share 140 · Avg credit 100 · Total credit 110 · actions 1fr
- **Transfers**: File 1fr · Project 140 · Direction 80 · Progress 190 · Speed 90 · Status 210 · Host 80
- **Event log**: Time 128 · Host 84 · Project 140 · severity 20 · Message 1fr

## Interactions & behavior
- **Global scope**: the Hosts selection in the rail is an independent, persistent global filter applied to
  every view (separate from NavigationView's view selection — both selected states render at once).
- **Partial results**: when scope = All hosts and ≥1 host is unreachable, show a dismissable `InfoBar`
  (severity Warning) with a "Retry now" link above the grid.
- **Host states** (icon + text, never color alone): Connected · Retrying (mm:ss countdown + attempt, ticks
  every second) · Unreachable · Wrong password · Disconnected. Retry uses warning `#B85C00/#BC4B09`.
- **Add / Edit host** (`ContentDialog`): Address required (accent focus underline), Port prefilled 31416,
  info strip explains the `remote_hosts.cfg` prerequisite; Add disabled until Address is non-empty; submit
  shows an inline ProgressRing; failure → InfoBar. Edit reuses the same dialog retitled, fields prefilled;
  clicking an auth-failed host opens Edit with the password field in error + focused.
- **Polling**: interval 2/5/10/30/60s (default 5s); "Updated Ns ago" is a polling-health indicator that
  turns to warning color + icon on poll failure.
- **Motion** (see `1h`): 100–300ms, enter decelerate / exit accelerate, no bounce, no infinite loops
  (spinners excepted), animation never blocks data updates. View switch 150ms decelerateMid (opacity +
  8px translateY); row enter 100ms (opacity only, no height animation); row remove 150ms accelerateMid;
  progress bar 200ms easeEase; dialog scrim 200ms in / 150ms out; expander chevron 100ms + height 200ms.
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
- **Colors**: ink `#242424`, secondary `#616161`, tertiary `#767676`, body-alt `#424242`; brand `#0F6CBD`
  (hover `#0F548C`); selection tint `#EBF3FC`; success `#107C10`, warning `#B85C00`/`#BC4B09`, danger `#C50F1F`.
  Surfaces: canvas `#FAFAFA`, card `#fff`, rail `#F5F5F5`, hover `#F5F5F5`, pressed/selected-neutral `#E0E0E0`/`#EBEBEB`.
  Lines: strong `#E0E0E0`, row `#F0F0F0`, column divider `#EDEBE9`, input hairline `#D1D1D1` (bottom `#616161`, focus 2px `#0F6CBD`).
  Dark: canvas `#1F1F1F`, surface `#292929`, line `#333`, brand `#479EF5`, hover `#383838`, text `#fff`/`#ADADAD`/`#D6D6D6`.
- **Type**: Segoe UI. Body 13/20; grid header 11 semibold; page title 16 semibold; caption/subtext 12 `#616161`; monospace = Consolas (Event log). Numeric = tabular figures.
- **Spacing**: 2/4/6/8/10/12/16/20/24/32. Page padding 16; card padding 16; section gap 24–32.
- **Sizes**: rail 260 (collapsed 48) · nav item 36 / host item 40 · command bar 52 · grid header 32 · rows 36/28 · status bar 28.
- **Radius**: controls 4, surfaces/cards/dialogs 8, tiny 2, avatars/badges/switch fully round.
- **Shadows**: cards shadow4 (rest) → shadow8 (hover); menus shadow8; dialogs shadow64. Never shadow + border on the same surface.

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
