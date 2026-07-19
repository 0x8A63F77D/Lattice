# HostMonitor formal verification

This directory (plus `tests/Lattice.Verification/` and the C# interleaving
harness in `tests/Lattice.Tests/Interleaving/`) is the machine-checked
verification of `Lattice.Core.HostMonitor`'s concurrency protocol. Design:
`docs/superpowers/specs/2026-07-08-hostmonitor-formal-verification-design.md`.

## 1. What is verified where

| Layer | Artifact | Question it answers | Bug class it catches |
|---|---|---|---|
| Design (primary) | F# shell model + exhaustive explicit-state explorer driving the PRODUCTION `HostMachine.step` (`src/Lattice.Core.Machine`; xUnit, all platforms) | Is the protocol itself correct? | design errors, guard placement, missing transitions (compile-time), lost wakeups |
| Design (double-check) | Promela model + Spin (exhaustive, LTL + weak fairness) | Does an independent formalization agree? | transcription/encoding bugs in either model; authoritative for liveness |
| Implementation | C# probe-point sweep tests (xUnit, in the normal suite) | Does the code faithfully implement the protocol? | r7-class transplant races, guard misplacement in code |

Since the functional-core restructure, the design layer executes the real
decision core (`HostMachine.step`) rather than a hand-transcribed copy of it, so
model-code drift is structurally impossible for the decision logic itself. The
remaining drift surface is the F# *shell* model (locks, waits, wake latch,
event raising) versus the real C# shell — which the implementation harness layer
pins.

### Property cross-reference

| Property | F# (`tests/Lattice.Verification/Properties.fs` test name) | Promela (assert/ltl) | Harness (`tests/Lattice.Tests/Interleaving/` test) |
|---|---|---|---|
| I1 publish | `` `I1 guards bar publishes when a config change is pending` `` | Accept/MsgG/SnapG routing + `MsgP` assert | `SweepPointTimesAction` (UpdateConfig arm, `AssertA1Doctrine` per-point cases) |
| I1 mutation | `` `I1 no pre-accept mutation of daemonVersion or message log` `` | `monitor()` `d_step` assert (`logVintage`) + `MsgP` assert | `AbortedAttemptNeverDestroysMessageLog`, `FirstTickReplacesLogAtomically` |
| I2 | `` `I2 connection closed during backoff and park` `` | `monitor()` `d_step` assert | `SweepPointTimesAction` (`AssertA2TerminalStatusesFollowClientDisposal`, all cases) |
| I3 | `` `I3 no unabortable stale attempt` `` | `monitor()` `d_step` assert | `SweepPointTimesAction` (UpdateConfig arm, `AssertA3NewGenerationConnected`) |
| I4 | `` `I4 attempt counter resets after a connected session` `` | `Retry`-phase assert | `FailedAttemptDoesNotPollute_DaemonVersion` + sweep preamble case (`BeforeRetryPublish` arm of `AssertA1Doctrine`, asserts `Attempt == 1`) |
| I5 | `` `I5 lifecycle safety - no disposed-resource faults` `` | `monitor()` `assert(!faulted)` | `LifecycleTests` (A6): `DoubleDisposeDoesNotThrow`, `DisposeWithoutStartThenStartIsInert`, `ConcurrentStartAndDisposeSettleDisconnected` |
| I6 | `` `I6 published status is coherent with the core phase` `` | `monitor()` `d_step` asserts (status/phase coherence; mapping offset one phase vs the F# encoding — deliberate N-version divergence) | (covered indirectly: `HostMonitorStateMachineTests` pins the published status sequences) |
| L1 | `` `L1 no lost wakeup` `` | `ltl L1` | (covered by design layer; harness indirectly via `ReentrancyTests` and the `RequestRefresh` sweep arm's wake-consumption assertions) |
| L2 | `` `L2 config change converges` `` | `ltl L2` | Sweep A3 convergence arm (`SweepPointTimesAction`, UpdateConfig branch's post-quiescence reconnect/snapshot/log assertions) |
| L3 | `` `L3 disposal terminates the loop` `` + `` `L3b AuthFailed parks until config change or disposal` `` | `ltl L3` | Sweep A5 (`SweepPointTimesAction`, `AssertA5DisposeOutcomeAsync`) |

Two deliberate encoding notes, not defects:

- **I5 pml slot:** the Promela `assert(!faulted)` is a permanent slot — `faulted`
  is never set to true anywhere in the `.pml` encoding. The lifecycle-fault
  class (double-dispose, start-after-dispose) is owned by the F# model's `M5`
  mutant (`mutant M5 no dispose flag violates I5`) and by the C# harness's A6
  `LifecycleTests`. The pml assert exists so the property list stays
  symmetric across both design encodings, not because pml independently
  proves it.
- **`authRefused` vs. `attempt = -1`:** the Promela model routes a refused
  password to `ParkedAuthFailed`/`Park` via a dedicated `authRefused` boolean;
  the F# model routes the same event via an `attempt = -1` sentinel consumed
  at `Teardown`. This is a deliberate N-version divergence — two independent
  encodings of the same routing decision, not a drift to reconcile.

## 2. Correspondence rules

The bridge between the two design artifacts and the code, human-checked at
review time for any PR touching `HostMonitor.cs`:

1. The `HostMachine.Command.SnapshotConfig` execution — snapshot config, clear
   `_configChanged`, and create the linked CTS — must run in **exactly one**
   `lock (_gate)` block in the interpreter (`HostMonitor.cs` `RunAsync`). More
   generally, every atomic step in the model that touches `_gate`-protected state
   must correspond to exactly one `lock (_gate)` block in the code. Splitting one
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
3. Probe points are emitted by the core as `Probe` commands, so a new shared-state
   touch point requires a new `ProbePoints` literal (`HostMachine.fs`), its emission
   from the relevant `step` case, and the matching `InterleavePoints` alias
   (`HostMonitor.cs`). The harness drifts too, not just the model: adding a
   write/read of shared state between two existing probe points without adding a
   probe there is a review failure.
4. **Domain-vocabulary discipline** (guards conceptual drift; user review
   2026-07-09): `HostMachine.Phase` names the protocol's interleaving points —
   operational vocabulary inherited from the parity round, a recorded debt, not
   a style to extend. Any change that adds, removes, or renames a phase must,
   in the SAME commit: (a) extend I6's phase→lifecycle projection in both
   design encodings AND the `HostMachine.fs` doc table (a phase must either
   project onto one `HostConnectionState` or be explicitly justified as
   trajectory-dependent — a phase with neither is a review failure); and
   (b) prefer a domain-shaped name where parity allows, justifying any new
   operationally-named phase in the commit message.

A **red/green disagreement** between the F# encoding and the Promela encoding on
the same property is itself a finding — a transcription bug in one of the two
encodings — and blocks. The two design artifacts share one property list
(I1–I6, L1–L3) and both must pass.

## 3. Shared-state inventory

One row per `HostMonitor` field (`src/Lattice.Core/HostMonitor.cs`), in
declaration order. "Modeled" means represented (directly or as an abstracted
proxy) in both the F# and Promela encodings; exclusion justifications address
vintage/visibility per correspondence rule 2, not merely data-race absence.

| Field | Protection regime | Modeled / excluded, with justification |
|---|---|---|
| `_clientFactory` | readonly-immutable | Excluded: construction-time delegate, invoked only from the loop thread by the interpreter's `CreateClient` command; no shared mutable state of its own. |
| `_time` | readonly-immutable | Excluded: time is abstracted in the model (nondeterministic completion choice for waits/delays per the primitive-semantics anchoring), not the field reference itself. |
| `_cts` | readonly-immutable reference; its cancel/dispose state is `_gate`-guarded via the `_disposed` test-and-set in `DisposeAsync` | Modeled: represented via `disposeFlag`/`outerCanceled`/`ctsState` transitions. I5 (lifecycle safety, cancel-after-dispose / token-after-dispose) is exactly about this field's state machine. |
| `_gate` | n/a — this is the synchronization primitive itself | Excluded: not domain state; it is what `atomic {}` / lock blocks in the model represent, not a modeled variable. |
| `_config` | `_gate` (written in `UpdateConfig`; snapshotted in the interpreter's `SnapshotConfig` lock block) | Modeled: `curVersion` (current config generation) / `attemptVersion` (the generation an in-flight attempt snapshotted). I3's abortability property is exactly about the vintage gap between these two. |
| `_configChanged` | `volatile` | Modeled directly as `configChanged`. Central to I1's guard discipline and I3. |
| `_connectionCts` | `_gate` (created/canceled/disposed all under `_gate`, per the field's own doc comment in `HostMonitor.cs`) | Modeled: `ctsState` (None/Live/Canceled/Disposed). |
| `_pollingIntervalSeconds` | `volatile` | Excluded from the model's state proper (the interval *value* participates in no invariant). Two setters touch it: `SetPollingInterval`'s only shared-state effect — a wake — is folded into the wake action already modeled; `SetPollingIntervalQuiet` (issue #92, the setter `HostMonitorManager.ApplyCadence` uses) writes only the volatile value and deliberately does NOT wake, so it contributes no interleaving surface at all. The waking setter's surface is L1's, covered by the `RequestRefresh` sweep arm; not independently swept (per the design's Toolchain section). |
| `_wake` | `_gate` (read/replaced under `_gate` in `Wake`/`WaitAsync`/`WaitForConfigChangeAsync`) | Modeled: sticky `wake` bit with the real TCS protocol (consume-if-completed-else-subscribe). L1's subject. |
| `_loop` | `_gate`-guarded write in `Start`; `_gate`-guarded read in `DisposeAsync`; `internal` solely so the A5 sweep assertion can read task completion/fault state post-hoc | Modeled: `loopTask` publication variable (Initial/Stored/TokenRead/Running/Exited/Faulted) — load-bearing for I5/A6 per the design (without it, Spin would prove I5 vacuously). |
| `_started` | `_gate` (test-and-set in `Start`) | Modeled: `started`. |
| `_disposed` | `_gate`-guarded test-and-set in `DisposeAsync`; checked under the same `_gate` acquisition in `Start` (see both methods' comments in `HostMonitor.cs`) | Modeled: `disposeFlag`. Idempotency of `DisposeAsync` and inertness of `Start` after dispose are I5. |
| `_daemonVersion` | single-writer loop-confined (written only by the interpreter's `PublishStatus(stampDaemonVersion=true)` execution, on the loop thread, at the accepted `Connected` publish; read only by `SetStatus`, also loop-confined) | Modeled: `daemonVersionVintage` / `daemonVerVintage`. Single-writer does NOT exclude it — this is precisely the field the design's I1-mutation rider fixes (PR #7 round-9 P2): a failed attempt must not pollute later publishes with an unaccepted version. Vintage discipline, not race safety, is the reason it's modeled. |
| `_status` | `Volatile.Read/Write` (property-backed field behind the public `Status` property) | Modeled: `status` (7-value mtype) / `statusVersion`. |
| `_snapshot` | `Volatile.Read/Write` (property-backed field behind the public `Snapshot` property) | Modeled indirectly via the `SnapG`/`SnapP` guard-then-publish phase pair; the snapshot *value* itself is out of scope (assumption: `SnapshotBuilder.Build` is pure). `Status`/`Snapshot` are two independent volatiles — deliberate cross-field tearing, see §4. |
| `MessageCapacity` (const) | readonly-immutable (compile-time constant) | Excluded: not shared mutable state. |
| `InterleaveProbe` | test-only seam; `null` in production, set once before `Start()` by the harness | Excluded from the concurrency model itself — it *is* the probe seam the harness uses to observe the model's interleaving points, not domain state under verification. |
| `_messages` (`MessageLog`) | single-writer loop-confined (only the loop thread calls `ReplaceAll`/`Append`); `MessageLog` has its own internal lock guarding `Snapshot()` reads against those writes, so there is no data race | Modeled: `logVintage`. This is the canonical example in correspondence rule 2 — Codex's PR #8 round-2 finding caught exactly this field being excluded on the (correct) single-writer race argument while hiding a real pre-accept-clear defect. Modeled under I1's guard discipline because the mutation is user-visible, not because it races. |

**Interpreter attempt-scoped locals (no inventory rows):** the interpreter's
`RunAsync` holds the per-attempt resources and payloads as method locals —
`client`, `config`, `connCt`, `fetchedVersion`, the tick payloads (`ccState`,
`ccStatus`, `results`, `transfers`, `projectStatuses`, `newMessages`) and `builtSnapshot` — plus
the `HostMachine.State` loop-local that carries the decision core's state. All
are single-writer, loop-confined method locals, structurally incapable of
cross-thread exposure, so none earns an inventory row. The machine `State` in
particular IS the modeled core: the F# design layer explores it directly rather
than re-representing it, so there is nothing here to reconcile against a proxy.

**`_lastSeqno` deletion note:** an earlier revision of `HostMonitor` held a
`_lastSeqno` field. Commit `0dc8c80` ("message log replaced atomically on
first tick; per-connection cursor field removed") deleted it: at that commit the
message cursor became a `PollAsync` local threaded through `TickAsync` (like
`state` already was), and since the functional-core restructure it lives in the
pure core's `HostMachine.State.lastSeqno` — a loop-local `State` value threaded
through `step`, never a field — because it is per-connection state and
per-connection state must not outlive the connection (I1 mutation half). Its
deletion is precisely why it needs no inventory row of its own — it is now
structurally incapable of being a field-level hazard, not merely excluded by
argument.

## 4. Assumptions

Outside what either verification layer checks (verbatim from the design):

Event subscribers terminate (a blocking subscriber stalls the actor thread;
liveness properties are conditional on this); subscribers may synchronously
**reenter** `UpdateConfig`/`RequestRefresh`/`SetPollingInterval`/`SetPollingIntervalQuiet`
from a handler — this is IN-contract and needs no new model states: publishes run
outside `_gate`, a reentrant `UpdateConfig`/`RequestRefresh`/`SetPollingInterval`
call's effects (flag/CTS/wake, all `_gate`-serialized) are a deterministic special
case of the concurrent environment action already modeled at that boundary (the
harness pins this with a targeted reentrant-`UpdateConfig`-from-`StatusChanged` case),
and `SetPollingIntervalQuiet` (issue #92) only writes the excluded volatile interval
value — no wake, no `_gate` — so it has no modeled effect at all; what
subscribers must NOT do is synchronously block on the monitor's own
lifecycle (e.g. `DisposeAsync().AsTask().Wait()` from a handler deadlocks the
loop by construction — documented as out-of-contract, Codex PR #8 round-4);
`SnapshotBuilder.Build` is pure (no shared state); the `IGuiRpcClient` is
confined to its attempt's scope (structural, but assumed rather than
modeled); `Status`/`Snapshot` are two independent volatiles — readers may
observe a fresh `Status` with a stale `Snapshot` (deliberate snapshot-
retention semantics; M2c ViewModels must tolerate this cross-field tearing).

## 5. Memory-model note

The model assumes sequential consistency. Justification for why C#'s weaker
guarantees suffice at this granularity: all multi-writer state is either
`_gate`-protected (full fences), `volatile` (`_configChanged`, acquire/release
— the flag is only ever written `false→true` by `Env` and reset under
`_gate`, so a stale read can only delay, never un-happen, an observation;
every delayed-read path re-converges via the canceled CTS, whose
`ThrowIfCancellationRequested` pairs with `Cancel` internally), or
`Volatile.Read/Write` published (`_status`, `_snapshot`).

## 6. How to run

- **F# design spec** (primary; any OS — the explorer drives the production
  `HostMachine.step` core directly): `dotnet test tests/Lattice.Verification`
- **Promela double-check** (needs `spin` + a C compiler): `scripts/model-check.sh`.
  Runs `spin -a` → `gcc pan.c` → `pan` (safety sweep plus one run per LTL
  property). CI runs this on `ubuntu-latest` as the `model-check` job in
  `.github/workflows/ci.yml` (installs `spin` and `gcc` via `apt-get`).
  Locally, this needs Spin's Windows binary + MSYS2 gcc (or WSL/Linux); if
  local setup fights back, CI is the gate.
- **C# interleaving harness**: rides the normal suite —
  `dotnet test Lattice.sln -c Release --no-build` (part of the `build-test`
  matrix, all three platforms).

## 7. Verification sync rule

From `CLAUDE.md` (hard workflow contract, verbatim):

> **Verification sync rule (hard workflow contract):** any semantic change to
> `src/Lattice.Core/HostMonitor.cs` must update, in the SAME commit: the F#
> executable spec (`tests/Lattice.Verification/`), the Promela model
> (`verification/HostMonitor.pml`), and the shared-state inventory + probe-point
> list. All code here is AI-written, so model-code sync is the author's
> obligation at write time — never deferred to review. A commit touching
> HostMonitor semantics without touching the verification artifacts must state
> in its message why no model change is needed.

Since the functional-core restructure (2026-07-08), the F#-spec leg of this rule
is discharged by construction for decision logic: the spec executes
`HostMachine.step` itself. Changes to the C# shell (locks, waits, probe
placement, event raising) still owe shell-model and probe-list updates; changes
to `HostMachine` are automatically covered by the F# layer but still owe a
Promela update when they change the protocol.

Probe-point placement rules (from the `InterleavePoints` doc comment in
`HostMonitor.cs`, and correspondence rule 3 above):

- Never inside a `lock` block — the controller thread takes the same lock,
  guaranteed deadlock.
- Only at locations where environment threads can already interleave in
  production — for shared-memory threads that is anywhere in the loop's
  straight-line code; probes do not create impossible interleavings.
- Every new shared-state touch point added to `HostMonitor` MUST add a probe
  point (`internal static class InterleavePoints`) — adding a write/read of
  shared state between two existing probe points without adding one there is
  a review failure.
