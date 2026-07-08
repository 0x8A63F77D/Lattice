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
- Fix the one known violation of the target invariants: `_daemonVersion` stamped from
  unaccepted connection attempts (PR #7 round-9 P2 deferral).

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
| Design | Promela model + Spin (exhaustive) | Is the protocol itself correct? | design errors, guard placement, lost wakeups, livelocks |
| Implementation | C# probe-point sweep tests (xUnit, in the normal suite) | Does the code faithfully implement the protocol? | r7-class transplant races, guard misplacement in code |

**Correspondence rule (the bridge, human-checked at review time):** every atomic step
in the model that touches `_gate`-protected state must correspond to **exactly one**
`lock (_gate)` block in `HostMonitor.cs`. Splitting one model step into two lock
acquisitions (the round-7 bug shape) violates the rule; the model will not turn red on
its own, but the sweep harness will. The rule is stated in `verification/README.md`
and is the checklist item for any PR that touches `HostMonitor.cs`.

## Layer 1 — Promela model (Spin)

### Tool choice

Spin/Promela over TLA+/TLC (no JVM dependency; exhaustive checking, equal verification
power for this model) and over the P language (P's checker is bounded/randomized
exploration — strictly weaker than exhaustive search on a deliberately small state
space). Cost accepted: Promela specs read as operational C-style code rather than
declarative math, and the `pan` verifier needs a C compiler (trivial on CI ubuntu;
MSYS2 gcc or CI-only on the Windows dev machine).

### Model boundary and granularity

- **Processes:** one `Loop` process (dispatcher → attempt → poll, transcribing
  `RunAsync`/`RunAttemptAsync`/`PollAsync`/`TickAsync` control flow) and one `Env`
  process that nondeterministically performs a bounded number of `UpdateConfig`,
  `RequestRefresh` (wake), `SetPollingInterval`, and one `Dispose`.
- **Atomicity:** one atomic step = one `lock (_gate)` block (wrapped in `atomic{}`)
  or one straight-line segment between interleaving points. `Env` actions can
  interleave between any two steps — this models the truth that environment threads
  interleave at *instruction* granularity, abstracted to the points where shared
  state is actually touched.
- **State:** `status` (7-value mtype), `version` (config generation counter),
  `configChanged`, `connectionCts` (None/Live/Canceled), `attempt`, sticky `wake`
  flag (modeled with the real TCS protocol: consume-if-completed-else-subscribe),
  `disposed`, and a published-events vintage log.
- **Bounds (pan constants):** ≤ 2 config updates, ≤ 3 injected connect/poll failures,
  attempt counter capped at 3, ≤ 2 stray wakes. Small enough for exhaustive search in
  seconds.

### Properties

Safety (inline assertions / monitor):

- **I1 — guarded publish:** every publish (Connected status, snapshot, message batch)
  is immediately preceded by a guard step that observed `configChanged == false`, and
  the vintage it carries equals the version current at *guard* time. (The code
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
  happen. Frozen *between guard and publish* ⇒ the publish MAY happen and carries the
  old vintage — asserted to match the documented benign-window semantics (snapshot
  retention), not treated as failure.
- **A2 (I2):** by the time a Retrying/AuthFailed status event is observed, the fake
  client records itself disposed.
- **A3 (I3):** with the fake's connect scripted to block until canceled: after
  UpdateConfig at any P, the monitor reaches an attempt on the new config within
  bounded fake time (proves the cancel reached a live CTS or the flag was observed).
- **A4 (I4):** connect-success then scripted failure ⇒ Retrying event carries
  `Attempt == 1`.
- **A5:** DisposeAsync at any P ⇒ loop task completes non-faulted, final status
  Disconnected, client disposed.

Roughly 14 × 3 sweep cases plus the targeted A4 scenario — all deterministic, all in
the normal three-platform test run. `SetPollingInterval` is modeled in the Promela
`Env` but excluded from the sweep: it is a single volatile write plus a wake, with no
gate-protected interaction — its interleaving surface is L1's, covered by the
RequestRefresh sweep arm. Event observations go through a recording
subscriber; connection identity is tagged by config generation on the fake.

### Code change rider: `_daemonVersion`

A1 will genuinely fail on current `main`: `_daemonVersion` is written to the field
mid-attempt (before Connected is accepted), so a failed attempt pollutes subsequent
Status publishes with an unaccepted daemon version — exactly PR #7 round-9's P2,
deferred to this work. Fix (as agreed in the PR thread): keep the exchanged version
attempt-local; write the field only at the Connected publish (post-guard). Retrying /
AuthFailed statuses then carry the last *accepted* version. This is in scope because
the alternative is weakening A1 to excuse a known violation.

## Toolchain, layout, CI

```
verification/
├── HostMonitor.pml      # Promela model (transcription-commented against the C#)
├── README.md            # correspondence rule, property list, memory-model note, how to run
scripts/
├── model-check.sh       # spin -a → gcc pan.c → pan runs (safety + one per LTL property);
                         # verifies spin/gcc presence, prints versions into the log
```

- **CI:** new `model-check` job in `.github/workflows/ci.yml` (ubuntu-latest,
  `apt-get install -y spin gcc`, run `scripts/model-check.sh`). Added to the
  `protect-main` ruleset as a required check (per the standing authorization for
  ruleset edits from PR #7's CI-matrix change). Harness tests ride the existing
  `build-test` matrix — no new job.
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

- **Model-code drift:** Spin re-checks the *model*, not the code. Drift control =
  correspondence rule in README (review checklist) + the sweep harness on the code
  side. Accepted: a semantic code change with a stale model turns nothing red until
  a sweep case or reviewer catches it; the README makes the audit cheap.
- **Probe seams in production code:** ~14 awaited null-checks on the loop's paths.
  Zero measurable cost at 2–60 s polling cadence; the seam is `internal` and invisible
  to the public API.
- **Benign guard-to-publish window:** deliberately specified as-is (I1/A1 encode it),
  matching the code comment's "cheap correctness polish, not load-bearing machinery".
  Tightening it would require publishing under `_gate` — rejected: publishes call
  subscriber code paths and must never run under the lock.
- **Spin on Windows locally:** may end up CI-only; acceptable.
