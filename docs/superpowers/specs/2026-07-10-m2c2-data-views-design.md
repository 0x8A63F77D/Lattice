# M2c-2 — Data views, staged delivery (design)

Date: 2026-07-10. Status: user-ratified decisions from the planning dialogue; supersedes nothing — extends `2026-07-09-m2c-avalonia-shell-design.md` (M2c-1, merged as PR #10).

## Goal

Complete milestone M2 by delivering the four data views (Tasks, Projects, Transfers, Event log) specified in the design package (`docs/design/m2/README.md` + `Lattice M2 Spec.html`), plus the M2c-1 deferrals that belong to them, an i18n seam, and journey-level test coverage — decomposed into independent subtasks that can be dispatched in parallel wherever dependencies allow.

Visual/behavioral requirements for every view are **defined by the design package** and are not restated here; this spec covers staging, shared infrastructure, decisions, and boundaries.

## Ratified decisions

1. **Decomposition over one big stage.** Work splits into independent parallel subtasks; sequencing only where a real dependency exists (shared scaffold before view stamping).
2. **Staged PRs.** Scaffold + Tasks view is one PR merged to main first; the remaining three views are developed in parallel worktrees and land as three separate small PRs. Each PR passes Codex Review + full CI (4 legs). Never merge before Codex posts (standing rule).
3. **Demo gates.** Wave 1 (scaffold + Tasks) gets its own user demo walkthrough — the pattern-setter is validated early. Waves 2–3 are covered by one final combined walkthrough after Wave 3 merges.
4. **i18n = ResX now.** Official Avalonia path: `Resources.resx` (default culture only, no translations), consumed via `{x:Static}` in XAML and the generated class in ViewModels. Designer-file regeneration must not depend on an IDE — use a build-time source generator (candidate: `Microsoft.CodeAnalysis.ResxSourceGenerator`; exact choice verified at plan time). All new-view strings go through it from day one; M2c-1's existing hardcoded strings migrate in the scaffold PR. Runtime language switching is out of scope (restart-to-apply is acceptable).
5. **Journey-level tests** (in-process, full composition root + fake RPC clients, one test per key user journey) start in the scaffold PR: infrastructure + journeys for the existing M2c-1 flows and the Tasks view. Each Wave-2 view PR ships its own journeys.
6. **Follow-up tracking moves to GitHub issues.** Existing cross-milestone ledger items are batch-migrated this round (Wave 0). Standing rule going forward: findings on an open PR are fixed directly; anything non-blocking becomes an issue.
7. **Hosts-rail placement design defect: not this round.** Filed as an issue; the Claude Design revision prompt happens in a later round. Wave 2/3 views are unaffected (defect is shell-internal).
8. **Upstream FluentAvalonia TextBox defect:** root-cause comment posted on amwx/FluentAvalonia#616 (2026-07-10). Their policy requires human-authored code, so any follow-up patch is written by the user personally if the maintainer approves; not part of this milestone's critical path.

## Wave structure

### Wave 0 — independent items (parallel, no code dependencies)

- **Issues migration:** create GitHub issues for all cross-milestone ledger follow-ups (Mica-on-Windows verification, hosts-rail design revision, visual-regression test leg, NuGet.Config, BOINC project attach for real smoke data, xunit v2/v3 unification, `Layout(window)` helper duplication, HostStore Changed-on-IntervalChanged cosmetic). Ledger keeps one-line pointers.
- **Hygiene chore PR:** `HostConnectionState.fs` doc comment (attempt >= 5 → >= 4; comment-only — commit message must state why no model change per the verification sync rule) + `generate-icons.sh` chmod 644 pre-mv and temp-file trap.

### Wave 1 — scaffold + Tasks view (single PR to main; sequential pattern-setter)

Shared infrastructure, built once, consumed by every view:

- **ResX seam** per decision 4, including migration of M2c-1 strings.
- **Command bar pattern:** view title, disabled M3 command placeholders, AutoSuggestBox filter, State ComboBox, "Updated Ns ago" indicator (warning icon + color when the scoped host's polling fails), refresh, density toggle, overflow. Built as reusable layout/styles, not a one-off.
- **DataGrid infrastructure:** `Avalonia.Controls.DataGrid` package + theme StyleInclude + shared column/row styling per design tokens (32px header, 36/28px rows, left-aligned columns, tabular numerals), sorting, virtualization (500+ rows).
- **Status bar custom control** (28px strip; the only custom control in M2).
- **Scope plumbing:** rail host selection = global scope shared by all views; **"All hosts" rail entry** with aggregate subtext ("3 of 5 connected") and the three-layer partial-results treatment (nav subtext, dismissable-but-reappearing warning InfoBar, status-bar counts covering reachable hosts only).
- **Shell touch-ups:** selected-view filled-icon swap.
- **Tasks view itself:** per design package (9 columns, Application derived via cached get_state join, progress cell, deadline-at-risk row treatment, suspended styling, default Deadline-ascending sort, Host column hidden in single-host scope, densities, its responsive column breakpoints, empty/loading states).
- **Journey-test infrastructure + first journeys** per decision 5.

Gate: Codex + CI + solo user demo, then merge to main.

### Wave 2 — three view PRs (parallel worktrees, each its own PR)

| PR | Content beyond the design package reference |
|---|---|
| Projects | Two-level hierarchy (aggregate parent by MasterUrl + per-host children), Varies/Mixed aggregation tiers, Host* credit-field mapping, single-host degradation. Hierarchy mechanism (flattened DataGrid vs Expander list — both design-sanctioned) decided at this PR's plan time with avalonia-docs MCP. |
| Transfers | FileTransfer columns, "34.2 / 51.7 MB" progress text, retrying row treatment with per-second countdown, completed-row fade-out, empty state (the common case). |
| Event log | **HostStore consumption of MessagesAdded** (new Core→App data path), per-host 5,000-message retention, time-merged all-hosts stream with Host column, Info/Warning/Error filter pills, Following auto-scroll toggle, monospace rows, **Event-log InfoBadge** (unread warning+error count) on the nav item. |

Conflict-isolation conventions for parallel work: each view owns its own View/ViewModel/test files; shared touch points (nav wiring, DataTemplate registration, resx additions) are single-line-per-view and each view uses its own resx key prefix (`Tasks*`, `Projects*`, `Transfers*`, `EventLog*`); each worktree rebases on main before opening its PR. Responsive column-breakpoint behavior ships per view.

Gate per PR: Codex + CI. No individual demos.

### Wave 3 — motion/polish PR (small, after Wave 2 merges)

- Motion spec table from the design package (view switch, row enter/remove, progress bar, InfoBar, dialogs, expanders) + reduce-motion support (all durations → 0 except spinners).
- Compact-rail icon-shift fix (user-deferred M2c-1 finding; bundle here).
- Gate: Codex + CI, then the **final combined user walkthrough** covering Waves 1–3. That walkthrough closes milestone M2c.

## Testing

- Every PR: component-level headless tests + its journey tests, red-first verified per repo workflow; `-warnaserror` clean; CI matrix (ubuntu/windows/macos build-test + model-check) all green.
- Visual bugs found along the way are verified by geometry/pixel probing (M2c-1 lesson), but the visual-regression *test leg* itself stays out of scope (issue-tracked).
- The parity oracle (`tests/Lattice.Tests`, 193) and verification suite (36) must stay green; no HostMonitor semantic changes are expected in this milestone — if one occurs, the verification sync rule applies in full.

## Out of scope (issue-tracked, not in any wave)

Hosts-rail placement design revision; Mica-on-Windows verification; visual-regression test leg; cross-process E2E; runtime language switching; M3 control operations (command-bar buttons stay disabled placeholders).
