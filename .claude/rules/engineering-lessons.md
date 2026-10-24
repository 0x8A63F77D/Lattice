# Engineering lessons (always loaded)

Durable lessons from Lattice PRs, distilled so they travel with the repo into every session
(local or cloud). Each line is a rule that already cost at least one review round to learn.
CLAUDE.md stays authoritative; this file elaborates it.

## Design principle: reduce complexity, then put what is left where it is cheapest to verify

- Two moves: complexity you can avoid creating, don't create (thin owned-UI surface, native
  framework mechanisms, no divergence between sibling mechanisms); complexity the domain forces
  on you, relocate to the substrate where taming it is cheapest — pure functions with exhaustive
  or property tests, formal models for concurrency.
- Pricing function: the same unit of risk costs differently per domain, priced by how tightly an
  agent can close the verification loop there. Pure logic is cheap; UI behaviour and visual
  fidelity are dear (no machine-checkable pass/fail, upstream churn, human eyeball time).
- Failure-mode locality razor: when choosing WHERE a capability lives, compare failure modes,
  not elegance. Pack riskable logic into pure functions plugged into the framework's own
  extension points; do not hand-build chrome the framework already provides. (Lattice-specific
  consequence of being built almost entirely on native Fluent/Avalonia components — the output
  flips in a custom-drawn project.)
- Some taxes are unavoidable: when the goal requires an expensive domain, pay it rather than
  contort the design. Unsure whether the tax is necessary ⇒ ask the owner.
- Classification trap: if a plan states invariants in prose that several event handlers must
  each uphold, it is a state machine wearing wiring's clothes. Extract it (closed event DU,
  total-match step, transition-table + property tests) before review, not on round 11
  (PR #73: 7+ findings were the reviewer enumerating one machine's table a row per round).

## Repeated findings mean the fix is wrong

Escalation ladder, counted per PROBLEM, not per component:
1. 2nd same-class finding ⇒ stop patching instances; name the violated invariant and enforce it
   by structure (scope, types, a single choke point, a pure extracted core).
2. Same problem repaired more than 3 rounds ⇒ root-cause analysis (formal modelling is one tool,
   not an automatic step).
3. After root-cause treatment, more than 3 further rounds ⇒ hand the judgment to the owner.

- Ask how the problem was PRODUCED, not only how to fix it. "Carry one more bit downstream so
  consumers can compensate" is the tell that the model made the bit necessary (event-log
  identity keyed by `(host, seqno, timestamp)` dissolved the replace-vs-append question).
- Plans that say "stamp this pattern N times" manufacture N transcription defects. Invariants
  ride in single-copy code; plans reference the machinery.
- A behaviour-preserving refactor can still transplant semantics wrong: audit the moved
  semantics for coverage holes and add the missing tests as part of the refactor.
- A review's diagnosis can be right while its remedy is wrong — push back with reasoning.

## Verify before building

- If you catch yourself writing "I cannot verify this link" about an assumption a mechanism
  rests on, and a human could settle it in one check: ask first, build only what does not depend
  on the answer. A safe fallback makes a wrong assumption harmless, not free. If the answer is
  negative, delete rather than patch; document the limit (#187: a whole macOS reader was built
  on a false assumption and then deleted).
- Suspected framework/OS bug: confirm empirically with our code ruled out → search upstream
  issues, docs and source for the established fix → cite it → only then design our own.
- Before recording an upstream hard block, check sibling package ids (`X.v3`, `X.Core`, …).
  "Latest X still depends on old Y" is not "X has no support for new Y" (the FsCheck "permanent
  v2 lock" was self-imposed for a year because `FsCheck.Xunit.v3` was never checked).
- Async/concurrent code: no blind write-and-rerun. State the interleaving invariant, enforce it
  by structure (single owner, atomic scope, message passing), and argue it; never ship a
  concurrency fix justified only by "the flaky test stopped failing".

## Evidence hygiene

- "Before" numbers and images come from the BASE tree: `git checkout origin/main -- <paths>`,
  clean rebuild, re-run. Never from an early iteration of your own work, never via `git stash`
  after committing (stash only reverts uncommitted work). Prove before ≠ after (`shasum`) before
  publishing a comparison.
- Anchor annotations and guide lines to measured rendered ink, never to a layout box the change
  perturbs.
- Falsify each part of a multi-part fix separately AND revert the whole change once; reverting
  one setter produces an intermediate state, not the base.
- When someone says they cannot see a difference, check the artefact before explaining the
  perception.
- After any range/bulk deletion, diff the surviving symbol list against the previous commit and
  account for every count change item by item; prefer deleting by named unit over line range.
- A build piped through `tail`/`grep` can hide the error and leave a stale binary — confirm
  "0 errors" explicitly before trusting a runtime experiment.

## Docs that assert behaviour

- Acceptance and walkthrough checklists: verify every asserted behaviour against shipped
  runtime code, not design docs, README or PR intent (design docs describe unshipped features).
- When a checklist stages state (attach/detach, sample vs real fleets, env-var configs), audit
  cross-step preconditions and shared fixtures, and state fixture roles once up front.
- A "doc-only" item that makes you write "no caller can ever…" about a public API: check whether
  the class enforces it or merely happens to hold it for in-repo callers. Enforce it at one choke
  point or narrow the claim; never leave the absolute standing.

## Self-review is not the review loop

- Your own fresh artefacts go through Codex + CI like any change. Never hand an executor your
  own draft as "final, do not edit".
- YAML frontmatter string fields containing `: `, `#`, a leading `-`, `[`, `{` or quotes must
  be quoted; an unquoted `description:` with an embedded colon-space silently breaks agent and
  skill loading.

## Git and worktree traps

- In a worktree, an absolute path into the main checkout edits the wrong tree while relative
  commands run in the worktree. Build every edit path under the worktree root and confirm the
  change reached the worktree before committing. Never `cd` to the main checkout from a worktree.
- Verify `git branch --show-current` before every commit that follows a subagent run.
- A PR's `base.sha` tracks the moving base tip, not the fork point. Use `git merge-base`, the
  PR commit list, or a grep of the head tree to learn what a branch contains.
