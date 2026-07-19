# Engineering / historical notes (preserved from rev. B handoff)

> Engineering/historical notes preserved from the prior (rev. B) handoff README. The authoritative design source of truth is now `Lattice M2 Spec.html` + `README.md` in this folder; these sections are retained because the finalized design README is design-only and omits them. The Data model mappings drove the Wave-2 view implementations.

## Data model

Column and field names split into what exists today vs. what the M2 milestone must add to the RPC/Core layers:

**(a) Available in current `Lattice.Boinc.GuiRpc` models:**
- `Result` — task state, fraction_done, elapsed time, deadline
- `Project` — UserTotalCredit, UserExpavgCredit, HostTotalCredit, HostExpavgCredit, SuspendedViaGui, DontRequestMoreWork
- `Message` — MessagePriority: Info=1, UserAlert=2, InternalError=3

**Credit-field mapping (Projects view):** per-host child rows use `HostExpavgCredit` (RAC) and `HostTotalCredit`; the aggregate parent row **sums the host-level values across hosts** — never repeat account-level `User*` totals per host.

**Application-column derivation (Tasks view):** `Result` carries no application name. Application = join `Result.WorkunitName` → `Workunit.AppName` → `App.UserFriendlyName`, falling back to `AppName` when the friendly name is absent, using the cached `get_state` snapshot.

**(b) New M2 data requirements (standard BOINC GUI RPC fields — to be added in M2, not yet implemented):**
- Remaining/ETA: parse `estimated_cpu_time_remaining` from `<active_task>` in `get_results`
- `Project.ResourceShare` (from get_state / get_project_status)
- `FileTransfer` model + `get_file_transfers` RPC (Transfers view)
- Per-host connection-state machine in the Core layer: `connected / connecting / retrying(backoff, attempt) / unreachable / auth-failed`

## Control mapping (FluentAvalonia)

| Region | Control |
|---|---|
| Shell | NavigationView (Left, PaneDisplayMode auto-compact); views = MenuItems; hosts = **custom selectable list control in `PaneFooter`** (two-line rows; own selection state, independent of NavigationView's) |
| Host row context menu | MenuFlyout (Edit / Test connection / Remove) |
| Alert counts | InfoBadge |
| Partial banner, dialog errors | InfoBar |
| All lists | DataGrid (virtualized, sortable) |
| Command bars | CommandBar / Toolbar + AutoSuggestBox + ComboBox |
| Progress | custom thin 3px bar in cells (stock ProgressBar restyled); ProgressRing for loading |
| Log filter pills | ToggleButton styled as pill |
| Settings | SettingsExpander; Add host / confirmations = ContentDialog (TaskDialog ok for confirms) |
| Status bar | **custom control required** (28px strip; trivial) — the only custom control in M2 |

## Decision log (from review)

1. Shell = option 1c (two-zone rail); 1a/1b kept in doc for context only.
2. All table columns left-aligned, including numeric (uniform alignment beats right-aligned numerals).
3. Projects: resource share & status are per-host facts; aggregate rows show "Varies"/count summaries, never fake single values; Mixed tier exists.
4. No pill capsules for project status — icon + text everywhere; parent row 14px vs child 12px for hierarchy.
5. Event log: no in-view host TabView; scope via nav rail; merged stream gets a Host column.
6. Flat table + Host column for all-hosts task aggregation (no grouping headers — cross-host sorting is the core scenario); grouping toggle possible in M3.
7. **(rev. B)** Hosts rail bottom-docked in PaneFooter — resolves the FANavigationView slot constraint (PaneCustomContent-on-top was rejected in demo review); the views↔hosts gap is accepted, never filled. Scale-adaptive: single row / flat list / status groups — switch is **height-based ("fits → flat, doesn't → grouped")**, not a fixed host count (a fixed >12 overflows 768-high laptops at 9 hosts and wastes 1440p at 13); manual list/group override in the Hosts header, persisted.
8. **(rev. B)** Host management moved out of Settings: "+" → Add host dialog; right-click host → MenuFlyout (Edit / Test / Remove); auth-failed click → Edit dialog with password error. Settings = global groups only.
9. **(rev. B)** Primary persona is single-host/local; multi-host farms (community) drive the scale rule — persistent status list beats a scope-picker flyout for "which node died at a glance".
10. **(#107/#119)** Partial-results bar = floating overlay card, not the docked strip drawn in card `1c`: overlay child of the grid Panel (grid geometry independent of bar state — the #107 fix), below the column header (#88: header stays sortable), content-hugging centred card capped at 720px, radius 8, shadow16 elevation with a stronger dark mirror, 150ms decelerateMid enter / instant exit. Persistent + one-click dismiss; auto-collapse-to-chip rejected (timer state machine unjustified for a one-click dismissal). Placement/chrome/motion live solely in the shared `partialBar` style class; geometry pinned by `HeaderFrameGapTests`.
