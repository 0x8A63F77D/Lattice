# SDD ledger

Per the GitHub-first tracking rule (2026-07-10), this file no longer accumulates
shipped-work histories. Execution records live with their PRs; issues and
milestones carry follow-ups and roadmap. This file keeps only what has no
GitHub home: cross-PR workflow lessons and in-flight wave state.

## Execution-record index

| Milestone | PR | Record |
|---|---|---|
| M1 — GuiRpc protocol layer | #1 | PR body + [record comment](https://github.com/0x8A63F77D/Lattice/pull/1#issuecomment-4942413130) |
| CI quality gate | #2/#3/#4 | PR bodies |
| M2 design handoff | #5 | PR body |
| M2a — GuiRpc extensions | #6 | PR body + [record comment](https://github.com/0x8A63F77D/Lattice/pull/6#issuecomment-4942413326) |
| M2b — Lattice.Core | #7 | PR body + [record comment](https://github.com/0x8A63F77D/Lattice/pull/7#issuecomment-4942417088) |
| HostMonitor verification | #8 | PR body + [record comment](https://github.com/0x8A63F77D/Lattice/pull/8#issuecomment-4942421293) |
| HostMonitor functional core (F#) | #9 | PR body + [record comment](https://github.com/0x8A63F77D/Lattice/pull/9#issuecomment-4942423263) |
| M2c-1 — Avalonia shell | #10 | PR body + [record comment](https://github.com/0x8A63F77D/Lattice/pull/10#issuecomment-4942427194) |
| M2c-2 Wave 0 — hygiene + issue migration | #19, issues #11–#18 | PR body |
| M2c-2 Wave 1a — infra + VMs | #20 | PR body + [record comment](https://github.com/0x8A63F77D/Lattice/pull/20#issuecomment-4942430596) |
| M2c-2 Wave 1b — TasksViewModel | #21 | PR body + [record comment](https://github.com/0x8A63F77D/Lattice/pull/21#issuecomment-4942431723) |
| Design rev. B package | #25 | PR body |
| gh→MCP workflow migration | #23 | PR body |
| M2c-2 Wave 1c — TasksView + wiring | #28 | PR body + review threads |
| M2c-2 Wave 1d — journeys + sweep | #29 | PR body + review threads |

Specs and plans remain versioned markdown under `docs/superpowers/`.
The design package under `docs/design/m2/` is single-source-of-truth
(rev. B, PR #25) — changes route through Claude Design, never hand-edits.

## Cross-PR workflow lessons (durable)

- Red-first + mutation falsification is mandatory on every fix and every
  reviewer pass; a reviewer independently repeats the falsification. False
  greens have been caught at every stage this discipline was applied.
- Repeated same-class findings ⇒ restructure the invariant, don't re-patch
  (escalation ladder in user memory; TasksOverlayPolicy's taxonomy restructure
  on repair round 3 is the reference case).
- Pure decision logic is extracted into policy modules from birth
  (PartialBarPolicy / TasksOverlayPolicy / ColumnVisibilityPolicy pattern),
  with exhaustive transition-table tests.
- Deterministic UI tests: never wait on transient states or booleans that can
  go true early — settle on the EXPECTED TEXT or the fake's observed calls.
  Wall-clock settles are banned (HeadlessSync.WaitUntilAsync).
- Test dispatchers must reproduce production ordering semantics:
  lock-only serialization is not `Dispatcher.UIThread.Post` — use the
  deferred-queue dispatcher (QueueUiDispatcher + Drain) for multi-monitor
  fixtures.
- Visual bugs: geometry/pixel probing (headless Skia) is the verification bar;
  a fix without end-state visual verification has shipped broken once.
- Codex review loop: verdicts count only on the FINAL commit; a review object
  with boilerplate body is not clean — re-poll review threads ≥60 s after it
  posts (inline findings share its timestamp). Quota errors mean the round
  will never come: get the reset time from the user. Reviews route through
  the github MCP tools; pr-monitor is status-only (`.claude/agents/pr-monitor.md`).
- Design doc (`docs/design/m2/README.md`) is authoritative over plan wording
  when they conflict — cite the design line when deviating from a plan detail
  (window-width breakpoints case, PR #28).
- Verification sync rule for HostMonitor semantics: see CLAUDE.md (hard
  contract; F#/Promela/state-inventory update in the same commit).
- Scheduled tasks (client-side cron): pre-approve their tools via "Run now"
  at creation, or unattended runs die on permission prompts.

## In-flight state

- **Wave 1 COMPLETE** (2026-07-11): PRs #20/#21/#28/#29 merged; suite
  397 → 494 (193 protocol + 265 app + 36 verification), Debug+Release,
  -warnaserror clean. Post-merge demo checklist lives in PR #28's body
  (showcase, not a gate).
- **Next up: issue #26** (rev. B shell rework) — unblocked by PR #28; runs
  as its own PR before the Wave 2 views.
- Everything else is tracked on GitHub (2026-07-11 migration): Wave 2 = #31,
  Wave 3 = #32, upstream FluentAvalonia #616 = #33, remote branch cleanup =
  #34, plus the Wave-0 follow-ups #11–#18, #24 (reconciler), #27
  (README + license). Roadmap lives on the M2/M3/M4 milestones.
