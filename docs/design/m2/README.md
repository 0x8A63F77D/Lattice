# Handoff: Lattice M2 — Multi-Host BOINC Monitoring Dashboard (read-only)

## Overview

Lattice is a cross-platform Avalonia (XAML) desktop app using **Fluent 2 via FluentAvalonia** that monitors BOINC clients on multiple hosts — a modern BOINCTasks replacement. **M2 is read-only**: display state only; control actions (suspend/abort/attach) come in M3 but the layout reserves space for them (disabled command-bar buttons).

This package specifies the app shell + 5 views + all states, with an approved shell direction (option "1c" from the design exploration).

## About the Design Files

`Lattice M2 Spec.dc.html` is a **design reference created in HTML** — a spec document with pixel-accurate mockups and annotations, NOT production code. The task is to **recreate these designs natively in Avalonia XAML with FluentAvalonia controls**, following the control mapping below. Do not port any HTML/CSS.

Open the file in a browser to view. Sections are labeled: 1a/1b/1c (shell options — **1c is the approved direction**), 1d (row anatomy), 1e (state matrix), 1f (dark theme), 1g (tokens), 2a Projects, 2b Transfers, 2c Event log, 2d Settings, 2e Motion, 2f Responsive.

## Fidelity

**High-fidelity.** Colors, spacing, row heights, font sizes, and iconography are final and should be matched. The mockups use web rendering of Segoe UI + Fluent System Icons; in Avalonia use the platform-equivalent theme resources of FluentAvalonia rather than hardcoding hex where a theme resource exists (mapping below).

## Data model

Column and field names split into what exists today vs. what the M2 milestone must add to the RPC/Core layers:

**(a) Available in current `Lattice.Boinc.GuiRpc` models:**
- `Result` — task state, fraction_done, elapsed/remaining, deadline
- `Project` — UserTotalCredit, UserExpavgCredit, SuspendedViaGui, DontRequestMoreWork
- `Message` — MessagePriority: Info=1, UserAlert=2, InternalError=3

**(b) New M2 data requirements (standard BOINC GUI RPC fields — to be added in M2, not yet implemented):**
- `Project.ResourceShare` (from get_state / get_project_status)
- `FileTransfer` model + `get_file_transfers` RPC (Transfers view)
- Per-host connection-state machine in the Core layer: `connected / connecting / retrying(backoff, attempt) / unreachable / auth-failed`

## App shell (approved: option 1c)

**NavigationView, Left mode, 260px** (collapses to 48px LeftCompact — see Responsive).

Two zones in one rail:
1. **Views (MenuItems, top)**: Tasks, Projects, Transfers, Event log. Item = 36px high, 20px icon (regular at rest, **filled + accent when selected**), 13px label. Selected: 3px accent pill on left + `#EBEBEB` bg. Right-aligned inline counts (Tasks 47, Transfers 2) in 12px secondary; Event log carries a red **InfoBadge** = unread warning+error count.
2. **Hosts section (below a separator)**: header "Hosts" (11px semibold secondary) + "+" quick-add button. First entry **All hosts** (aggregate), then one 40px two-line entry per host: line 1 = host name (13px), line 2 = connection state (11px). Selecting a host scopes ALL views; scope persists across view switches. All-hosts selected state uses brand tint bg `#EBF3FC` + accent pill. Per-host InfoBadge when that host has alerts.
3. **Footer**: Settings.

**Host connection states** (icon + text, never color alone):
- Connected — `checkmark_circle` regular, success color; subtext "Connected · N tasks"
- Connecting — `arrow_sync` rotating (1.5s/rev linear, the only allowed looping animation), brand color; "Connecting…"
- Retrying — `arrow_clockwise`, warning color; "Retrying in 12s (attempt 3)" — countdown updates every second, plain text swap, no animation
- Unreachable — `dismiss_circle`, danger color; tooltip carries last error
- Auth-failed — `key`, danger color; "Wrong password"; clicking navigates to Settings with that host's expander open and password field focused

## Screens

### Tasks view (primary, ~80% usage)

- **Command bar** (52px): view title "Tasks" (16px semibold) · disabled Suspend/Abort buttons (M3 placeholders) · separator · AutoSuggestBox filter (220px, "Filter by name or project") · State ComboBox ("State: All") · spacer · "Updated 3s ago" (12px secondary; turns warning icon+color if polling fails) · refresh icon-button · density toggle icon-button · overflow (column visibility etc.)
- **Partial InfoBar** (Warning severity, closable, action link "Retry now"): shown only when scope = All hosts AND ≥1 host unreachable. Copy: "**Partial results.** 2 of 5 hosts aren't reachable — tasks below cover 3 hosts."
- **DataGrid** (virtualized; must handle 500+ rows). Columns: Project 108 · Application 118 · Task * (min 160, ellipsis + tooltip full name) · Progress 112 · Elapsed 68 · Remaining 74 · Deadline 100 · State 112 · Host 76 (hidden when single-host scope). Header row 32px, 11px semibold secondary. Single-column sorting, default **Deadline ascending**.
  - **All columns left-aligned** (including numeric — explicit user decision); numeric cells use tabular numerals.
  - Progress cell: 56×3px bar (2px radius, track `#E0E0E0`, fill accent) + 12px percent text; unknown fraction = empty track + "—" (never indeterminate).
- **Row states** (36px medium / 28px compact densities):
  - Normal: 13px primary text; task name in `#424242`
  - Selected: bg `#EBF3FC` + 3px accent pill at left
  - Deadline at risk (visual treatment only; logic decided by team): full-row tint `#FFF9F5`, Deadline cell gets filled `warning` icon + semibold `#BC4B09` text
  - Suspended: all text drops to `#616161`, progress fill turns gray
  - State cell icons: Running `play`/success · Waiting `clock`/neutral · Suspended `pause`/neutral · Uploading `arrow_upload`/brand
- **Status bar** (28px, custom control — the only one; trivial): "47 tasks · 8 running · 2 uploading · 1 suspended" · spacer · "⚠ 1 deadline at risk" · "Polling every 5s". Counts cover reachable hosts only.

### Projects view (2a)

Row-based hierarchical DataGrid (or Expander list), NOT cards. Columns: chevron 24 · Project 200 · Hosts 110 · Resource share 140 · Avg credit 100 · Total credit 110 · Status *.

- **Aggregate parent row** (40px, 14px, name semibold) merges by project MasterUrl across hosts.
- **Resource share and Status are per-host facts** — never fake a single aggregate value:
  - Share: identical on all hosts → single value + bar on parent; differs → text "Varies · 50–100" on parent, bars only on child rows
  - Status aggregation, three tiers: all same → "Active on all hosts"; one deviation → "Suspended · 1/2 hosts"; mixed → "Mixed · 1 suspended · 1 no new tasks" (`more_horizontal` icon, ellipsis + tooltip)
- **Child rows** (32px, 12px secondary): host name, task count, share bar+value, RAC, total credit, status icon+text.
- Status rendering: icon + text everywhere, **no pill/tag capsules** (explicit decision); status text 12px `#424242`. Active = `checkmark_circle` success; Suspended = `pause` neutral; No new tasks = `hand_right` neutral (config states, not faults — no warning colors). Default sort: Avg credit descending.
- Single-host scope: hide Hosts column and child rows.
- Empty state: "No projects attached — attach via the BOINC client on the host."

### Transfers view (2b)

Columns: File * (ellipsis+tooltip) · Project 140 · Direction 80 · Progress 190 · Speed 90 · Status 210 · Host 80. All left-aligned.
- Progress text = "34.2 / 51.7 MB" (transferred/total), not bare percent.
- Row states: Active = `arrow_sync` success icon + live speed; **Retrying** = warning row tint + "Retry in 02:41 (attempt 3)" semibold warning, countdown ticks per second; Queued = `clock` neutral, speed "—".
- Command bar: disabled "Retry now" (M3). Completed rows fade out 150ms accelerate then remove.
- **Empty state is the common case**: centered 24px `arrow_swap` icon in `#BDBDBD`, "No active transfers" 13px semibold, caption "Uploads and downloads will appear here while in progress." No button.

### Event log view (2c)

**No in-view host tabs** — scope comes from the nav-rail Hosts section like every other view (explicit decision; a TabView variant was rejected as double navigation).
- All-hosts scope = single time-merged stream **with a Host column (84px)**; single-host scope hides that column, subtitle shows the host name. Title subtitle: "All hosts · merged stream".
- Filter row: three toggle pills Info / Warning / Error (selected = brand tint `#EBF3FC` + brand border/text + check icon) · search box · spacer · **"Following"** toggle (auto-scroll to newest; scrolling up pauses it and turns it into "Resume following") · copy button.
- Rows 26px: timestamp `07-04 14:32:08` + message in **Consolas 12px**; host + project columns 12px secondary; priority icon 16px. Warning rows tint `#FFF9F5` + filled warning icon; Error rows tint `#FDF3F4` + filled `error_circle` danger icon.
- Virtualized; retain last 5,000 messages/host. New rows do NOT animate while Following (high frequency).
- Status bar: "412 messages · 3 reachable hosts · showing all priorities" / "Following live".

### Settings (2d)

- **Hosts group**: one **SettingsExpander** per host. Header: `server` icon, host name, subtext "192.168.1.40:31416 · Connected" (state text colored but also plain-language). Expanded content: Address / Port / Password fields, "Test connection" secondary button, "Save" primary, "Remove host" danger text-button (opens ContentDialog confirm).
  - **Auth-failed host auto-expands** with password field in error state (danger border) and actionable message: "The host refused this password. Check the gui_rpc_auth.cfg on office-pc."
- **Add host — ContentDialog**: Name (optional, "Defaults to the address") · Address (required; Add disabled while empty) · Port (prefilled 31416) · Password ("From gui_rpc_auth.cfg") · info strip: "Remote access must be allowed on the host — add this machine's IP to remote_hosts.cfg." Primary button shows inline ProgressRing while connecting; failure renders an InfoBar in the dialog.
- **Polling group**: SettingsExpander-style row + ComboBox: 2/5/10/30/60 seconds, default 5.
- First-run empty state (no hosts): centered `server` icon, "Connect your first host", caption "Lattice monitors BOINC clients over GUI RPC (port 31416).", accent button "Add a host" → opens the dialog. Nav rail hides the Hosts section in this state.

## States summary (1e)

- **Loading (first connect)**: ProgressRing + "Fetching state from {host}…" + 3 shimmer skeleton lines. Data from already-connected hosts renders immediately — never block on the slowest host.
- **Empty / First-run**: see per-view specs above.
- **Partial (3/5 reachable)** communicated on three layers: nav "All hosts" subtext "3 of 5 connected" · warning InfoBar in content · status-bar counts cover reachable hosts only. Never silently drop data. InfoBar is dismissable but reappears when the reachable set changes.

## Interactions & Behavior

- Keyboard: ↑↓ row navigation · Ctrl+F focus filter · F5 refresh now · Ctrl+1..4 switch views · Space on header sorts · all controls tabbable; focus visual = 2px black outline (FocusStroke2), not a glow.
- "Slide/screen scope" rule: selected host in rail is a global scope shared by all views.
- Motion spec (2e) — global: 100–300ms, enter decelerate / exit accelerate, no bounce, no loops except spinners; animation never delays data (update value first, transition is cosmetic):

| Interaction | Animation | Duration/curve |
|---|---|---|
| View switch | content opacity 0→1 + translateY 8→0 | 150ms decelerateMid |
| Row enter | opacity 0→1 (no height expand) | 100ms decelerateMid; skip while log Following; skip batches >10 |
| Row remove | opacity 1→0 then reflow (no FLIP) | 150ms accelerateMid |
| Progress bar | width to new value | 200ms easyEase; percent text jumps, no interpolation |
| InfoBar in/out | height+opacity | in 200ms decelerateMax / out 150ms accelerateMid |
| ContentDialog | scrim 0→.4, panel opacity+scale .96→1 | FluentAvalonia defaults |
| Expander | chevron 100ms; height 200ms decelerateMid, collapse 150ms |
| Completed transfer row | opacity 1→0 then remove | 150ms accelerateMid (same as row remove) |
| NOT animated | retry countdowns, "Updated Ns ago", elapsed/remaining ticks, sort reflow | — |

- Reduce-motion (Windows animation toggle / macOS Reduce Motion): all durations → 0 except spinners/ProgressRing.

## Responsive (2f)

- Breakpoints (window width): **≥1280** full rail + all columns · **1100–1279** rail collapses to 48px LeftCompact (hosts show state icon only, name/subtext/countdown in tooltip) · **1000–1099** hide Elapsed, then Application (available via overflow menu). User-dragged column widths persist and beat breakpoint defaults.
- Minimum window 1000×700. Command bar/InfoBar/header/status bar fixed; only grid body scrolls.
- Large screens (≥1600): no max content width; Task star column absorbs slack. Future charts (M4) dock as a collapsible bottom Expander — don't preclude it.

## Design Tokens

**Layout**: nav 260/48 · nav item 36 / host item 40 · command bar 52 · grid header 32 · row 36 (compact 28) · status bar 28 · surface radius 8 · control radius 4 · page padding 16 · gap 8.

**Type** (see font fallback below; Consolas-stack for log): page title 16 semibold · row body 13 (compact 12) · grid header 11 semibold · caption/subtext 12 · nav item 13 · badge 10 semibold. Numeric cells: tabular numerals, left-aligned.

**Font fallback stacks (cross-platform — every platform must resolve to a real font):**

- UI: `Segoe UI` (Windows) → `SF Pro Text` (macOS) → `Ubuntu` / `Noto Sans` (Linux) → `system-ui, sans-serif`
- Monospace (event log timestamps + messages): `Consolas` (Windows) → `SF Mono` / `Menlo` (macOS) → `DejaVu Sans Mono` (Linux) → `monospace`

Metric note: DejaVu Sans Mono is wider and has taller vertical metrics than Consolas. Keep the 26px log row height by setting an explicit row height / line-height (do not derive from font metrics), keep log font size fixed at 12px, and size the timestamp column (128px) for the widest stack — already validated. SF Pro Text and Ubuntu are metrically close enough to Segoe UI that the 36/28px grid rows need no adjustment.

**Color by role — Light / Dark** (prefer FluentAvalonia theme resources; hexes are the reference values; respect system accent — these brand hexes are defaults only, contrast ≥4.5:1 must hold):

| Role | Light | Dark |
|---|---|---|
| Canvas / chrome | #FAFAFA | #1F1F1F |
| Content surface | #FFFFFF | #292929 |
| Nav surface | #F5F5F5 | #202020 |
| Stroke / divider | #E0E0E0 / #F0F0F0 | #3D3D3D / #333333 |
| Text primary / secondary / tertiary | #242424 / #616161 / #8A8A8A | #FFFFFF / #D6D6D6 / #ADADAD |
| Accent (selection, links, progress) | #0F6CBD | #479EF5 |
| Selected row / brand tint | #EBF3FC | #123B5C |
| Success (icons only, never bg) | #107C10 | #9FD89F |
| Warning fg / row tint | #BC4B09 / #FFF9F5 | #FAA06B / #3A2A1E |
| Warning InfoBar bg/border | #FFF9F5 / #FDCFB4 | #411200 / #714224 |
| Danger fg / error-row tint | #C50F1F / #FDF3F4 | #E37D80 / #3A1E20 |
| Neutral state fg | #616161 | #ADADAD |
| Disabled | #BDBDBD | #5C5C5C |

Rule: state semantics always icon + text + color (three channels). Row background tints only for **selection and state-severity, never decoration** — allowed set: selected, deadline-at-risk, transfer-retrying, log-warning, log-error.

Contrast note: the ≥4.5:1 floor applies to all enabled text/icons on their surface. **Disabled text (#BDBDBD light / #5C5C5C dark) is explicitly exempt**, per the WCAG 1.4.3 exception for inactive UI components — do not lighten it to pass.

**Mica**: Windows 11 — Mica on nav + command bar regions; solid fallbacks above for macOS/Linux/battery. Content surfaces always opaque.

## Control mapping (FluentAvalonia)

| Region | Control |
|---|---|
| Shell | NavigationView (Left, PaneDisplayMode auto-compact); hosts = custom two-line NavigationViewItem template |
| Alert counts | InfoBadge |
| Partial banner, dialog errors | InfoBar |
| All lists | DataGrid (virtualized, sortable) |
| Command bars | CommandBar / Toolbar + AutoSuggestBox + ComboBox |
| Progress | custom thin 3px bar in cells (stock ProgressBar restyled); ProgressRing for loading |
| Log filter pills | ToggleButton styled as pill |
| Settings | SettingsExpander; Add host / confirmations = ContentDialog (TaskDialog ok for confirms) |
| Status bar | **custom control required** (28px strip; trivial) — the only custom control in M2 |

## Assets

- Icons: Fluent System Icons (MIT, microsoft/fluentui-system-icons) — regular at rest, filled when selected/active. Names used: task_list_square_ltr, grid, arrow_swap, document_text, settings, apps_list, server, checkmark_circle, arrow_sync, arrow_clockwise, dismiss_circle, key, play, pause, clock, arrow_upload, arrow_download, warning, error_circle, info, more_horizontal, add, search, chevron_down/up/right, timer, hand_right, text_line_spacing, filter, dismiss, copy, eye, arrow_sort_up/down, arrow_down, checkmark.
- No logo asset; "Lattice" wordmark = 20px accent rounded square with "L" + Segoe UI Semibold text (placeholder).

## Files

- `Lattice M2 Spec.html` — the full spec document, **fully self-contained** (all runtime scripts, styles, and icon fonts inlined; opens offline from a plain checkout with no network access and no sibling folders required).
- `support.js` — runtime library used by the original editable source document in the design workspace; not needed to view the bundled spec (kept for package-structure continuity).

## Decision log (from review)

1. Shell = option 1c (two-zone rail); 1a/1b kept in doc for context only.
2. All table columns left-aligned, including numeric (uniform alignment beats right-aligned numerals).
3. Projects: resource share & status are per-host facts; aggregate rows show "Varies"/count summaries, never fake single values; Mixed tier exists.
4. No pill capsules for project status — icon + text everywhere; parent row 14px vs child 12px for hierarchy.
5. Event log: no in-view host TabView; scope via nav rail; merged stream gets a Host column.
6. Flat table + Host column for all-hosts task aggregation (no grouping headers — cross-host sorting is the core scenario); grouping toggle possible in M3.
