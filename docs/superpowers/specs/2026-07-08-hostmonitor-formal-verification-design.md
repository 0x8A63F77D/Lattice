# HostMonitor Concurrency Formal Verification — Design

**Date:** 2026-07-08
**Status:** Approved (pending user review)
**Scope:** `Lattice.Core.HostMonitor` only

## Motivation

M2b's PR #7 went through 9 Codex review rounds; the hardest findings were concurrency
defects in `HostMonitor` (the per-host actor loop). The structural restructure
(`RunAttemptAsync` scoping, commit 97dfb74) closed the defect *classes*, but two facts
remain uncomfortable:

1. The design invariants are enforced by structure **and documented only in comments**.
   Nothing machine-checks them; a future semantic change can silently violate them.
2. Round 7 proved that a behavior-preserving refactor can transplant an atomic step
   into two lock acquisitions — a class of bug that **no deterministic unit test can
   pin** and that our review process caught only by luck of an external reviewer.

The project has not shipped a release yet. This is the cheapest point to lock the
design in a machine-checked form and to build a regression net for implementation-level
interleaving bugs.

## Goals

- A **normative, machine-checked model** of HostMonitor's concurrency protocol that
  CI re-verifies on every PR (the design layer).
- A **deterministic interleaving harness** that exercises the real C# code at every
  interleaving point against every environment action (the implementation layer).
- Fix the two known violations of the target invariants, both of the same class
  (user-visible state mutated by a not-yet-accepted connection attempt):
  `_daemonVersion` stamped mid-attempt (PR #7 round-9 P2 deferral), and the
  message-log reset (`_lastSeqno = 0` + `_messages.Clear()`) running *before* the
  pre-Connected `_configChanged` guard (Codex PR #8 round-2 finding) — an aborted
  attempt destroys the public log, with no self-healing if the new config then
  fails or parks. The required fix is the STRUCTURAL one specified in "Code change
  riders" below (delete the `_lastSeqno` field, atomic `ReplaceAll` on first tick);
  merely moving the reset behind the guard is explicitly NOT acceptable — it only
  narrows the destructive window (PR #8 round-3/round-4 findings).
- Verify **lifecycle edges**: `Start`/`DisposeAsync` ordering, double-dispose,
  start-after-dispose. Suspected latent defects (found during this design's
  completeness audit, to be confirmed by red tests first): a second `DisposeAsync`
  calls `Cancel()` on a disposed CTS → `ObjectDisposedException` escapes, violating
  IAsyncDisposable idempotency convention; `Start()` after dispose evaluates
  `_cts.Token` in the loop lambda → the loop task faults unobserved. If confirmed,
  harden (idempotency flag / started-state check) as part of this work.

## Non-goals

- `HostMonitorManager` / `HostRegistry` verification (thin composition; unit tests
  suffice; teardown already covered).
- Weak-memory-model verification (GenMC territory). The model assumes sequential
  consistency at its granularity; §"Memory model note" justifies why.
- Exhaustive verification of the C# code itself (no IL-rewriting tools like Coyote —
  considered and rejected: heavy CI integration, uncertain maintenance, and the probe
  harness covers the target bug class deterministically with zero dependencies).

## Architecture: two layers, one correspondence rule

| Layer | Artifact | Question it answers | Bug class it catches |
|---|---|---|---|
| Design (primary) | F# executable specification + exhaustive explicit-state explorer (xUnit, all platforms) | Is the protocol itself correct? | design errors, guard placement, missing transitions (compile-time), lost wakeups |
| Design (double-check) | Promela model + Spin (exhaustive, LTL + weak fairness) | Does an independent formalization agree? | transcription/encoding bugs in either model; authoritative for liveness |
| Implementation | C# probe-point sweep tests (xUnit, in the normal suite) | Does the code faithfully implement the protocol? | r7-class transplant races, guard misplacement in code |

The two design artifacts share one property list (I1–I5, L1–L3) and both must pass;
any green/red disagreement between them is itself a finding (a transcription bug in
one of the two encodings) and blocks. This N-version redundancy is a deliberate,
user-chosen mitigation of the shared-blind-spot risk: one author, but two foreign
encodings that fail differently.

**Correspondence rules (the bridge, human-checked at review time), stated in
`verification/README.md` as the checklist for any PR touching `HostMonitor.cs`:**

1. Every atomic step in the model that touches `_gate`-protected state must
   correspond to **exactly one** `lock (_gate)` block in the code. Splitting one
   model step into two lock acquisitions (the round-7 bug shape) violates the rule;
   the model will not turn red on its own, but the sweep harness will.
2. Every field of `HostMonitor` must appear in the README's **shared-state
   inventory**: field → protection regime (`_gate` / `volatile` / `Volatile.R/W` /
   single-writer loop-confined) → modeled or excluded, with justification.
   (E.g. `_lastSeqno` and `_messages` are single-writer loop-confined — only the
   loop thread writes them, so they carry no *data-race* surface — but
   single-writer does NOT exclude a field from the model: the message-log clear is
   a user-visible mutation and is modeled under I1's guard discipline. Codex's PR
   #8 round-2 finding caught exactly this: the original draft excluded these fields
   on the single-writer argument and thereby hid a real pre-accept clear defect.
   Exclusion justifications must address vintage/visibility, not just races.)
   A new field with no inventory row is a review failure.
3. Any new shared-state touch point in the code requires a probe point (the harness
   drifts too, not just the model): adding a write/read of shared state between two
   existing probe points without adding a probe there is a review failure.

**Verification assumptions (also in the README — outside what either layer checks):**
event subscribers terminate (a blocking subscriber stalls the actor thread; liveness
properties are conditional on this); subscribers may synchronously **reenter**
`UpdateConfig`/`RequestRefresh`/`SetPollingInterval` from a handler — this is
IN-contract and needs no new model states: publishes run outside `_gate` and a
reentrant call's effects (flag/CTS/wake, all `_gate`-serialized) are a deterministic
special case of the concurrent environment action already modeled at that boundary
(the harness pins this with a targeted reentrant-`UpdateConfig`-from-`StatusChanged`
case); what subscribers must NOT do is synchronously block on the monitor's own
lifecycle (e.g. `DisposeAsync().AsTask().Wait()` from a handler deadlocks the loop
by construction — documented as out-of-contract, Codex PR #8 round-4); `SnapshotBuilder.Build` is pure (no shared
state); the `IGuiRpcClient` is confined to its attempt's scope (structural, but
assumed rather than modeled); `Status`/`Snapshot` are two independent volatiles —
readers may observe a fresh `Status` with a stale `Snapshot` (deliberate snapshot-
retention semantics; M2c ViewModels must tolerate this cross-field tearing).

## Layer 1 — design-level models (F# primary, Spin double-check)

### Tool choice

**Primary: an F# executable specification.** The protocol is a pure transition
system — immutable state record, discriminated unions for loop steps and environment
actions, `step : State -> Action -> State list` (nondeterminism as a list) — checked
by a small explicit-state BFS explorer (~150 lines of pure F#), run as ordinary xUnit
tests on all three CI platforms. What F# buys over a foreign modeling language:

- **Typed correspondence:** the model references `Lattice.Core`'s real types
  (`HostConnectionState` etc.); renames and added states break the model at compile
  time — the first compiler-enforced drift guard in the design.
- **Compile-checked totality:** exhaustive-match warnings as errors make an
  unhandled state×action combination a build failure, not a runtime discovery.
- **Zero foreign toolchain for the primary check** and three-platform execution.

Cost owned honestly: the explorer is ours, including the hard part — liveness
(SCC/non-progress-cycle detection under weak fairness). Mitigations: the red-first
discipline (deliberately break the protocol per property; a checker that stays green
is itself broken) exercises the checker, and the explorer is ~150 lines of
reviewable pure code.

**Double-check: Spin/Promela** (over TLA+/TLC: no JVM; over P: exhaustive vs.
bounded/randomized). Spin's 30-year-mature LTL + weak-fairness machinery is
**authoritative for the liveness properties**; it also re-checks all safety
properties as the independent second encoding. `pan` needs a C compiler — trivial on
CI ubuntu, CI-only is acceptable on the Windows dev machine.

### Model boundary and granularity (shared by both encodings)

- **Processes:** one `Loop` process (dispatcher → attempt → poll, transcribing
  `RunAsync`/`RunAttemptAsync`/`PollAsync`/`TickAsync` control flow) and one `Env`
  process that nondeterministically performs a bounded number of `UpdateConfig`,
  `RequestRefresh` (wake), `SetPollingInterval`, and lifecycle actions: `Start`
  (including start-after-dispose ordering) and up to two `Dispose` calls
  (idempotency). The CTS is modeled with a Disposed state so that
  cancel-after-dispose / token-after-dispose faults are expressible.
- **Atomicity:** one atomic step = one `lock (_gate)` block (wrapped in `atomic{}`)
  or one straight-line segment between interleaving points. `Env` actions can
  interleave between any two steps — this models the truth that environment threads
  interleave at *instruction* granularity, abstracted to the points where shared
  state is actually touched.
- **State:** `status` (7-value mtype), `version` (config generation counter),
  `configChanged`, `connectionCts` (None/Live/Canceled/Disposed), `attempt`, sticky
  `wake` flag (modeled with the real TCS protocol:
  consume-if-completed-else-subscribe), `disposed`, a published-events vintage log,
  and — load-bearing for I5/A6 (Codex spec-review finding, PR #8 round 1) —
  `started` plus a `loopTask` publication variable
  (Initial/Stored/TokenRead/Running/Exited/Faulted): `Start` models
  store-task-then-lambda-reads-token as separate steps, and `DisposeAsync` models
  its real read-`_loop` → await → dispose-CTS sequence, so the
  token-read-after-CTS-dispose fault is a reachable model state. Without this
  variable, Spin proves I5 vacuously — the Start/Dispose race A6 targets would not
  be expressible in the model at all.
- **Bounds (pan constants):** ≤ 2 config updates, ≤ 3 injected connect/poll failures,
  attempt counter capped at 3, ≤ 2 stray wakes. Small enough for exhaustive search in
  seconds.

### Primitive-semantics anchoring

The model must not invent ad-hoc encodings for .NET primitives; every primitive is
anchored on a mature, documented semantic model, and the anchoring is stated in the
model's header comments so a reviewer can check the mapping:

- **`async`/`await`:** the loop process is transcribed as the state machine the C#
  compiler itself lowers async methods to — every `await` is an explicit
  continuation point and nothing else is. The set of interleaving points is thus
  *derived from the language's execution model*, not hand-picked.
- **`lock (_gate)`:** .NET monitor semantics → Promela `atomic { }` (the code never
  awaits inside a lock, so lock queuing/reentrancy need not be modeled — stated as
  an assumption tied to correspondence rule 1).
- **`TaskCompletionSource` (RunContinuationsAsynchronously):** a one-shot latch —
  monotonic completed bit, idempotent `TrySetResult`, per its documented contract.
- **`CancellationTokenSource`:** monotonic canceled bit; the linked CTS is the
  disjunction of its own bit and the outer token's; `Dispose` transitions to a state
  where `Cancel`/`Token` fault (documented ODE behavior) — the I5 fault states.
- **`Task.WhenAny`/`Task.Delay`:** nondeterministic completion choice (time is
  abstracted; the FakeTimeProvider layer owns time determinism, not the model).
- **`volatile` flags:** plain shared bits under the SC assumption, valid per the
  memory-model note (single-direction `false→true` transitions).
- **Promela idiom discipline:** standard Spin patterns (Holzmann) — a bounded
  nondeterministic environment process, `d_step` only for internal bookkeeping that
  can never block, LTL checked per-property with weak fairness (`pan -f`).

### Properties

Safety (inline assertions / monitor):

- **I1 — guarded publish/mutation:** every publish (Connected status, snapshot,
  message batch) **and every user-visible mutation** (the message-log `ReplaceAll`,
  which after rider 2 exists only on a connection's first tick, post-accept) is
  immediately preceded by a guard step that observed `configChanged == false`, and
  the vintage it carries equals the version current at *guard* time. A
  not-yet-accepted attempt has NO user-visible mutation available to it at all —
  by construction, not by guard. (The code
  documents a benign guard-to-publish window — see `HostMonitor.cs` comment at the
  final snapshot recheck; the model encodes exactly this contract, no stronger.)
- **I2 — connection exclusivity:** at most one client connection live at any time;
  during Retrying backoff waits and the AuthFailed park, no connection is live.
- **I3 — abortability (round-7's property):** once `UpdateConfig` completes, the
  in-flight attempt on the old config either has its CTS canceled or is guaranteed to
  observe `configChanged` before any publish. No path exists where the cancel no-ops
  on a null CTS *and* the stale attempt runs unaborted to a publish.
- **I4 — attempt-counter semantics (round-6's property):** a failure after Connected
  was published enters Retrying with `attempt == 1`.
- **I5 — lifecycle safety:** no reachable step faults on a disposed resource
  (cancel-after-dispose, token-after-dispose modeled as assertion failures);
  `Dispose` is idempotent; `Start` at any point never produces a faulted loop.

Liveness (LTL, weak fairness on `Loop`):

- **L1 — no lost wakeup:** a wake that lands while the loop is waiting (or busy)
  is observed by the current or next wait.
- **L2 — config convergence:** after `UpdateConfig`, eventually the loop runs an
  attempt that snapshotted the new version, or the monitor is disposed.
- **L3 — disposal termination:** after `Dispose`, the loop eventually exits with
  final status Disconnected; AuthFailed has no exit except config change or disposal.

### Memory model note

The model assumes sequential consistency. Justification for why C#'s weaker guarantees
suffice at this granularity: all multi-writer state is either `_gate`-protected
(full fences), `volatile` (`_configChanged`, acquire/release — the flag is only ever
written `false→true` by `Env` and reset under `_gate`, so a stale read can only delay,
never un-happen, an observation; every delayed-read path re-converges via the canceled
CTS, whose `ThrowIfCancellationRequested` pairs with `Cancel` internally), or
`Volatile.Read/Write` published (`_status`, `_snapshot`). This argument lives in
`verification/README.md`.

## Layer 2 — C# interleaving harness

### Probe seam

`HostMonitor` gains one internal seam:

- `internal Func<string, Task>? InterleaveProbe` — `null` in production (single null
  check per point, no allocation); tests set it via the existing
  `InternalsVisibleTo("Lattice.Tests")`.
- ~14 named probe points (`internal static class InterleavePoints` with string
  constants, same file), awaited at: before/after the attempt's snapshot lock block,
  before each `_configChanged` guard, between each guard and its publish, before/after
  the poll interval wait, at the attempt `finally` entry and after CTS disposal, and
  before the dispatcher's Retrying publish and AuthFailed park.
- Placement rules: never inside a `lock` block (the controller thread takes the same
  lock — guaranteed deadlock); only at locations where environment threads can
  already interleave in production (which, for shared-memory threads, is anywhere in
  the loop's straight-line code — probes do not create impossible interleavings).

### Sweep tests

For each probe point P × environment action A ∈ {UpdateConfig, DisposeAsync,
RequestRefresh}: script `FakeGuiRpcClient` through a full connect + first tick cycle,
freeze the loop at P, execute A on the test thread, release, run to quiescence
(FakeTimeProvider, no real sleeps), then assert the code-level invariants:

- **A1 (I1):** frozen *before a guard* + UpdateConfig ⇒ the guarded publish must NOT
  happen — and the message log must survive: freezing at ANY pre-first-tick point,
  then updating config, must leave the old messages intact (red on current code's
  eager clear until rider 2's structural fix lands; afterwards the property holds by
  construction — there is no code path that clears without new data in hand — and
  the sweep arm additionally asserts the first-tick `ReplaceAll` semantics). Frozen *between guard and publish* ⇒ the publish may still occur (the gap
  is physically unclosable without publishing under the lock), but the doctrine and
  assertion differ per publish type (Codex spec-review finding, PR #8 round 1):
  - *Snapshot publish:* benign by the documented retention semantics — an old-vintage
    snapshot is indistinguishable from the deliberate keep-last-known behavior.
  - *Messages append / Connected status publish:* NOT blessed as benign — no
    retention doctrine covers them. Tolerated only as **self-healing transients**
    (UpdateConfig has already canceled the CTS, so teardown is in flight), and the
    sweep arm asserts the healing, not mere tolerance: after release and quiescence,
    the message log has been cleared and refetched from the new config's connection,
    and the status has progressed through the new connection's states. A regression
    in the cancel path or in clear-on-reconnect turns this arm red.
  Both arms also pin structure: guard and its publish must remain one straight-line
  segment (no awaits between them) — enforced by correspondence rule 3's
  probe-point requirement plus review.
- **A2 (I2):** by the time a Retrying/AuthFailed status event is observed, the fake
  client records itself disposed.
- **A3 (I3):** with the fake's connect scripted to block until canceled: after
  UpdateConfig at any P, the monitor reaches an attempt on the new config within
  bounded fake time (proves the cancel reached a live CTS or the flag was observed).
- **A4 (I4):** connect-success then scripted failure ⇒ Retrying event carries
  `Attempt == 1`.
- **A5:** DisposeAsync at any P ⇒ loop task completes non-faulted, final status
  Disconnected, client disposed.
- **A6 (lifecycle, targeted rather than swept):** double `DisposeAsync` does not
  throw; `Start()` after dispose neither throws nor leaves a faulted loop task;
  `DisposeAsync` racing `Start()` settles in Disconnected with no unobserved fault.
  Expected red on current code (see Goals) → harden → green.

Roughly 14 × 3 sweep cases plus the targeted A4 scenario — all deterministic, all in
the normal three-platform test run. `SetPollingInterval` is modeled in the Promela
`Env` but excluded from the sweep: it is a single volatile write plus a wake, with no
gate-protected interaction — its interleaving surface is L1's, covered by the
RequestRefresh sweep arm. Event observations go through a recording
subscriber; connection identity is tagged by config generation on the fake.

### Code change riders: pre-accept side effects

A1 will genuinely fail on current `main` in two places, both the same defect class —
user-visible state mutated before the attempt is accepted:

1. `_daemonVersion` is written to the field mid-attempt, so a failed attempt pollutes
   subsequent Status publishes with an unaccepted daemon version (PR #7 round-9 P2,
   deferred to this work). Fix as agreed in that thread: keep the exchanged version
   attempt-local; write the field only at the Connected publish (post-guard).
   Retrying/AuthFailed statuses then carry the last *accepted* version.
2. The message-log reset (`_lastSeqno = 0` + `_messages.Clear()`) runs before the
   pre-Connected guard (Codex PR #8 round-2). Round 3 showed the first proposed fix
   (move the reset behind the guard) merely narrows the window — `UpdateConfig` can
   still land between guard and reset, and even an atomic guard+clear leaves the log
   destroyed if the accepted connection dies before its first tick. Fix is
   structural elimination, not a better guard: delete the `_lastSeqno` field
   entirely (it is per-connection state — becomes a `PollAsync` local threaded
   through `TickAsync` like `state` already is), and replace the eager `Clear()`
   with an atomic `MessageLog.ReplaceAll(batch)` on the new connection's **first
   tick**. The log then transitions old→new at the instant new data exists; no
   destructive pre-accept (or pre-first-tick) mutation remains *expressible*.
   Deliberate user-visible behavior change, to be flagged in the PR: during
   reconnects the old messages stay visible instead of vanishing — now consistent
   with `Snapshot`'s keep-last-known retention doctrine. (A first tick that fetches
   zero messages replaces the log with empty — correct: the daemon genuinely has an
   empty buffer.)

Both are in scope because the alternative is weakening A1 to excuse known violations.

## Toolchain, layout, CI

```
tests/Lattice.Verification/     # F# executable spec (PRIMARY design-level check)
├── Lattice.Verification.fsproj #   xUnit; references Lattice.Core for shared types
├── Model.fs                    #   state record + action DUs + step function
├── Explorer.fs                 #   BFS reachability + SCC/fairness liveness (~150 lines)
├── Properties.fs               #   I1–I5 as reachable-state invariants, L1–L3
verification/
├── HostMonitor.pml      # Promela model (independent double-check; same properties)
├── README.md            # correspondence rules + shared-state inventory + property
│                        #   cross-reference table (F# ↔ Promela), memory-model note
scripts/
├── model-check.sh       # spin -a → gcc pan.c → pan runs (safety + one per LTL property);
                         # verifies spin/gcc presence, prints versions into the log
```

- **CI:** the F# spec project rides the existing `build-test` matrix (three
  platforms, already a required check) — the primary design-level gate costs zero
  new CI surface. One new `model-check` job for the Spin double-check
  (ubuntu-latest, `apt-get install -y spin gcc`, run `scripts/model-check.sh`),
  added to the `protect-main` ruleset as a required check (per the standing
  authorization for ruleset edits from PR #7's CI-matrix change). Harness tests
  also ride `build-test` — no further jobs.
- **Local (Windows dev machine):** spin's Windows binary + MSYS2 gcc; if local setup
  fights back, CI is the gate and the script documents the ubuntu path. Not a blocker.

## Process

1. Model first: write `HostMonitor.pml` transcribing the *current intended* protocol;
   get all properties green under exhaustive search (the model must stand on its own).
   Property-by-property: temporarily break the model (e.g., split the snapshot/CTS
   atomic step — replay round 7) to confirm each property actually fails when its
   invariant is violated — the model-layer equivalent of TDD's red step.
2. Probe seam + sweep harness, TDD: A1 is expected red on `_daemonVersion` → apply the
   rider fix → green. All other assertions expected green (if any other sweep case
   turns red, that is a real pre-existing bug: stop, root-cause, fix as its own commit
   with the sweep case as the regression test).
3. CI job + ruleset update.
4. PR → self-trigger Codex review (verify 👀 ack) → triage → merge per severity
   delegation. SDD ledger updated throughout.

## Risks / accepted tradeoffs

- **Model-code drift:** the models re-check *themselves*, not the code. Primary
  control is now the **verification sync rule in CLAUDE.md** (user-set process,
  2026-07-08): all code here is AI-written, so any semantic change to
  `HostMonitor.cs` must update the F# spec, the Promela model, and the
  inventory/probe list in the same commit — sync is the author's write-time
  obligation, not a review hope. Backstops: correspondence rules in README (review
  checklist), typed F#↔Core references (drift breaks the build for renames/shape
  changes), and the sweep harness on the code side.
- **Shared blind spot (irreducible, now double-mitigated):** all layers are authored
  from one understanding of the code — a misunderstood interleaving surface gets
  mis-modeled AND un-probed simultaneously. Mitigations: external review (Codex)
  against the shared-state inventory (spec-first review already caught three such
  gaps: A1 doctrine, lifecycle task-publication state, the pre-accept log clear),
  and the F#/Spin N-version redundancy — the same author, but two foreign encodings
  that fail differently; a green/red disagreement is a transcription bug surfaced.
- **Two-encoding maintenance (user-chosen):** every protocol change must be applied
  to both design models. Accepted deliberately for the cross-validation value;
  controlled by the README's property cross-reference table (a property present in
  one encoding and missing in the other is a review failure).
- **Bounded exhaustiveness (irreducible):** pan is exhaustive only within the stated
  bounds (≤ 2 config updates, etc.); defects requiring longer histories are invisible.
  Accepted on the small-scope hypothesis — the protocol's state is essentially
  "current vs. stale", not history-dependent.
- **Probe seams in production code:** ~14 awaited null-checks on the loop's paths.
  Zero measurable cost at 2–60 s polling cadence; the seam is `internal` and invisible
  to the public API.
- **Benign guard-to-publish window:** deliberately specified as-is (I1/A1 encode it),
  matching the code comment's "cheap correctness polish, not load-bearing machinery".
  Tightening it would require publishing under `_gate` — rejected: publishes call
  subscriber code paths and must never run under the lock.
- **Spin on Windows locally:** may end up CI-only; acceptable.
