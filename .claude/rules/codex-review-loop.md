# Codex review loop mechanics (always loaded)

Lattice PRs pass an automated Codex review (bot login `chatgpt-codex-connector[bot]`) before
merge. CLAUDE.md states the gate; this file records how the bot actually behaves, learned the
hard way across ~200 review rounds.

## The gate

- Never merge before Codex has reviewed the FINAL commit and CI is green on that exact head.
  Codex does not review merged PRs.
- A refuted finding (evidence-based reply on the thread) does not block merge. A finding that
  contradicts a documented owner decision is refuted on-thread AND escalated to the owner — never
  silently reversed, never silently merged over. Codex re-raising a known, accepted consequence
  is not new information.
- When a finding is wrong, react 👎 on it and write the reason in the thread reply.
- For design-heavy or concurrency/protocol work, open the PR with the spec alone and get it
  reviewed before implementing (spec-first review found 6 real P2s on the HostMonitor spec).

## Reading the bot's signals

- **Attribute every reaction.** 👀 (ack), 👍 (clean verdict) and 👎 count only when the reactor
  login is EXACTLY `chatgpt-codex-connector[bot]`. Reaction counts returned by issue/PR reads
  are anonymous; read the reaction authors from the REST reactions endpoint
  (`/issues/{n}/reactions`, `/issues/comments/{id}/reactions`, `/pulls/comments/{id}/reactions`).
  If the reactor cannot be confirmed, disregard the reaction.
- **A clean round is a bare 👍 on the PR body, from the auto review AND from manual triggers.**
  "No review object appeared" is not evidence the round is pending — check PR-body reactions
  with authors first. A 👍 carries no commit id: when merging on one, state the evidence chain
  (trigger time, 👍 time, last review time, zero new inline threads, CI on the exact head).
- **A posted review is not a clean review.** The review body is boilerplate whether or not it
  found anything; findings live in inline review comments that share the review's timestamp.
  After any review posts, fetch the review threads (≥60 s later) and check for new ones.
- Round 1 is automatic when a PR opens (or a draft is marked ready). Do NOT post `@codex review`
  for round 1 — it burns a quota unit. Re-trigger with `@codex review` only after pushing.
- Ack gate: a manual trigger is acknowledged by 👀 on the trigger comment within ~1–2 min (round
  1's ack is 👀 on the PR body). No ack in 5 min ⇒ re-trigger once. A second silent trigger means
  probable quota exhaustion — stop and get the reset time from the owner; the explicit
  usage-limit comment often arrives only later. Treat that comment as not-a-result.
- The 👀 can disappear when a review completes; absence of 👀 later does not prove a dropped
  trigger — look for a posted result before re-triggering.
- Your own inline-thread replies create empty `COMMENTED` review objects on the latest commit;
  filter them out when polling for the bot's review.
- **Paginate.** Long loops outgrow one API page (54 review objects on PR #219; page 1 = 30).
  Every listing passes `per_page=100` and follows pagination, or polls with an `after:` cursor
  anchored at the last known item.
- Reviews are single-pass (comprehensive mode is off): expect one set of major findings per
  trigger, not convergence-to-zero.

## Watchers

- Watcher agents (`.claude/agents/pr-monitor.md`) report STATUS only — posted / error / timeout
  plus a CI rollup. They never relay or judge review content; the session that merges reads the
  raw review threads itself before any merge.
- Baselines handed to a watcher are claims: verify them, and have the watcher verify on cycle 0.
  Watchers must surface anomalies (quota or error comments), not only whitelisted shapes.
- Watch loops never block an interactive main thread; long waits go to background agents or
  short (≤5 min) polling bursts.
- An auto-mode classifier may block `@codex review` on a PR the session just created (it misreads
  the PR number as an unrelated issue). Confirm the PR is yours, then escalate once — don't hammer.
