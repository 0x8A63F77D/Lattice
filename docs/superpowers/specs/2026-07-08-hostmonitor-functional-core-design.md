# HostMonitor functional core / imperative shell restructure

Date: 2026-07-08
Status: approved direction (user decision 2026-07-08, memory `fsharp-for-functional-domains`);
detailed design authored autonomously in the dedicated restructure session — judgment
calls recorded in §9.

## 1. Problem

`src/Lattice.Core/HostMonitor.cs` (~650 lines) interleaves two things:

1. **The concurrency protocol** — phase sequencing, guard discipline, attempt
   counting, backoff, auth routing, first-tick log replacement, re-auth
   escalation. This is exactly what the F# executable spec
   (`tests/Lattice.Verification/Model.fs`) and the Promela model encode.
2. **Async I/O plumbing** — sockets via `IGuiRpcClient`, the background `Task`
   loop, `lock (_gate)` blocks, the TCS wake latch, CTS lifetimes, event raising.

Because (1) exists twice — once in C#, once in the verification models — every
semantic change pays the model-sync tax (CLAUDE.md verification sync rule), and
drift between them is caught only by discipline plus the C# sweep harness.

**Goal: the model stops being a transcription of the code and becomes the code.**
A single pure F# transition function is both what the verifier explores and what
production executes. Decision-logic drift between model and implementation
becomes structurally impossible; the remaining drift surface shrinks to the thin
shell, which the existing C# interleaving harness already pins.

## 2. Goals and non-goals

Goals:

- Pure F# transition function (`step : State -> Input -> State * Command list`)
  as the production decision core, grown from `Model.fs`.
- C# `HostMonitor` reduced to an interpreter shell: executes `Command`s
  (I/O, locks, events, waits), feeds `Input`s back to `step`.
- `tests/Lattice.Verification` rewritten to explore the **production** `step`
  wrapped in an environment model (shell primitives: wake latch, CTS, volatile
  flag, budgets). Properties I1–I5 / L1–L3 and all 7 mutants stay, and mutants
  stay red-first.

Non-goals / frozen surfaces (any violation must stop the work and be surfaced):

- **Zero semantic change.** Observable behavior — status publish sequences,
  event payloads and ordering, message-log retention/replacement semantics,
  backoff schedule, re-auth escalation, disposal semantics — is bit-identical.
- **Public API frozen** for `HostMonitor` and `HostMonitorManager` (M2c is being
  planned against it in a parallel session): `Start` / `RequestRefresh` /
  `UpdateConfig` / `SetPollingInterval` / `DisposeAsync`, events
  `StatusChanged` / `SnapshotUpdated` / `MessagesAdded`, properties
  `HostId` / `Status` / `Snapshot` / `Messages`.
- **Test-visible internals frozen**: `InterleavePoints` (all 15 names and their
  semantic positions), `InterleaveProbe`, `_loop`, `MessageCapacity`,
  `HostMonitor.BackoffDelay(int)` (may delegate to the core).
- **Existing C# tests are the parity oracle and must not be modified**:
  `HostMonitorStateMachineTests`, `HostMonitorPollingTests`,
  `HostMonitorManagerTests`, and the whole `Interleaving/` harness (sweep
  matrix, lifecycle, reentrancy) pass unchanged against the new implementation.
  A required test edit = a semantic change = stop and surface.
- Promela model unchanged except comment/line-reference touch-ups: it is the
  independent N-version encoding of the *same protocol*, and the protocol does
  not change.

## 3. Approaches considered

1. **Extract pure decision helpers, keep C# control flow** (backoff math,
   outcome routing as static functions). Low risk, but the protocol's control
   flow — the part the models encode — stays duplicated in C#. Does not meet
   the decision's goal. Rejected.
2. **Structured shell**: keep `RunAsync`/`RunAttemptAsync`/`PollAsync` method
   skeletons, delegate each branch decision to F#. Halfway house: C# still owns
   sequencing, so sequencing can still drift. Rejected.
3. **Command interpreter (chosen)**: the F# core owns *all* phase sequencing
   and routing; C# is a flat interpreter loop plus primitives. This is the only
   variant where the verifier and production run the same transition function,
   which is the point of the whole exercise.

## 4. Architecture

### 4.1 New project: `src/Lattice.Core.Machine/` (F#)

- Root namespace **`Lattice.Core`** (types appear to C# exactly where they used
  to live); assembly/project name `Lattice.Core.Machine`.
- References: FSharp.Core only. No GuiRpc, no Lattice.Core (dependency direction:
  `Lattice.Core` → `Lattice.Core.Machine`).
- Contents:
  - `HostConnectionState` — the existing enum **moves here** from
    `ConnectionStatus.cs`, same namespace `Lattice.Core`, same member order and
    doc comments (source-compatible for every consumer; `ConnectionStatus`
    record stays in C# because it carries `VersionInfo`).
  - `module HostMachine` — the pure core (single file, `HostMachine.fs`):
    `Phase`, `State`, `Input`, `Command`, `initial`, `step`, `backoffDelay`.
- `InternalsVisibleTo`: none needed — the machine's surface is used by
  `Lattice.Core` and `Lattice.Verification`; keep it `public` but document it
  as an implementation detail of Lattice.Core, not a supported API (XML doc +
  README note). (Judgment call §9-J4.)

### 4.2 The core vocabulary (medium-granularity sketch; implementer refines)

Design rule: **every phase of `Model.fs` is a core state; every effect the
model treats as part of a transition is a `Command`; every await result or
volatile-flag observation is an `Input`.** Guard reads are effects
(`ObserveFlags` → `Flags(configChanged, disposalRequested)`), so guard
placement — the thing I1 is about — lives in the core, and the shell cannot
reorder it.

```fsharp
type Phase =                       // ≈ Model.fs Phase, payload-carrying where useful
    | Dispatch | SnapshotBlock | AwaitConnect | AwaitAuth | AwaitFetch
    | AcceptGuard | PublishConnected
    | TickRpcs | MsgGuard | MsgPublish of TickData | Refetch of TickData
    | SnapGuard of TickData | SnapPublish of TickData
    | PollWait | Teardown of Outcome | RetryDecide of error: string
    | BackoffWait | ParkedAuthFailed | Exited

type State = {
    phase: Phase
    attempt: int                   // dispatcher counter (unbounded; delay capped)
    reachedConnected: bool
    firstTick: bool
    lastSeqno: int
    reauthedSinceLastSuccess: bool
    hasPassword: bool }

type Input =
    | Started
    | ConfigSnapshotted of hasPassword: bool
    | EffectOk                     // connect / authorize-skip / dispose done…
    | AuthResult of authorized: bool
    | FetchOk
    | TickFetched of maxSeqno: int option * hasNewMessages: bool * hasUnknownWorkunit: bool
    | RefetchOk
    | Flags of configChanged: bool * disposalRequested: bool
    | WaitEnded of configChanged: bool * disposalRequested: bool
    | Faulted of FailureKind       // shell folds exceptions into this

type FailureKind =                 // shell classifies the exception TYPE only;
    | Disposal                     //   routing by phase/history is core logic
    | ConnCanceled                 // linked CTS canceled (UpdateConfig)
    | AuthRefused of message: string       // BoincUnauthorizedException
    | Failure of message: string           // everything else

type Command =
    | SnapshotConfig               // the ONE atomic lock block (rule 1)
    | CreateClientAndConnect | Authorize | FetchVersionAndState
    | PublishStatus of HostConnectionState * attempt: int
                     * backoff: TimeSpan option * error: string option
                     * stampDaemonVersion: bool   // rider A: true only at Connected
    | RunTickRpcs of lastSeqno: int
    | PublishMessages of replaceLog: bool
    | RefetchState
    | BuildAndPublishSnapshot
    | ObserveFlags
    | WaitPollInterval | WaitBackoff of TimeSpan | ParkForConfigChange
    | TeardownAttempt              // dispose linked CTS + client (also in shell finally)
    | ExitLoop
```

Semantics ported 1:1 from today's `HostMonitor.cs` (authoritative list the
implementer must check off):

- Dispatcher routing incl. `ReachedConnected ? 1 : attempt+1` reset rule and
  config-change short-circuit after backoff (I4).
- The atomic snapshot block stays ONE shell lock block driven by ONE command
  (correspondence rule 1 / mutant M1).
- Accept guard → Connected publish adjacency; daemon version stamped only on
  the accepted publish (rider A / I1-mutation).
- Tick pipeline: msg guard → publish (first tick replaces, `ReplaceAll` even
  when empty; `MessagesAdded` only when non-empty) → seqno cursor from max →
  conditional refetch → snap guard → build (+ post-build recheck) → publish
  (rider B / I1).
- Silent re-auth: one per successful tick; second consecutive unauthorized
  escalates to the generic Failed path (today's `HostSessionLostException`
  becomes plain core routing — the exception type disappears); re-auth
  refusal or empty password → AuthFailed park.
- Auth skip on empty password; pre-Connected `BoincUnauthorizedException` →
  AuthFailed park; OCE with outer token → Disposal; other OCE → ConfigChanged.
- AuthFailed park released only by config change or disposal (L3b); publish
  after teardown.
- `backoffDelay`: 1,2,4,…, capped 60 s (shell keeps `BackoffDelay` delegating
  to it for the pinned tests).

### 4.3 The shell (`HostMonitor.cs` after)

Keeps, unchanged: all fields and their protection regimes (`_gate`, `_cts`,
`_connectionCts`, `_wake`, `_started`, `_disposed`, `_loop`, `_config`,
`_configChanged`, `_pollingIntervalSeconds`, `_status`, `_snapshot`,
`_messages`), public API, `Wake`/`WaitAsync`/`WaitForConfigChangeAsync`,
`RaiseSafe`/`SetStatus`, `InterleavePoints` + probe seam, ctor.

Replaces `RunAsync`/`RunAttemptAsync`/`PollAsync`/`TickAsync` with one
interpreter loop:

```csharp
private async Task RunAsync(CancellationToken ct)
{
    var state = HostMachine.initial;
    Input input = Input.Started;
    while (true)
    {
        var (next, commands) = HostMachine.step(state, input);
        state = next;
        foreach cmd => input = await ExecuteAsync(cmd, ...); // try/catch → Input.Faulted
        if (state.phase.IsExited) break;
    }
}
```

- `ExecuteAsync` owns per-attempt resources (client, linked CTS) in interpreter
  locals; a **defensive** `try/finally` around the attempt scope still disposes
  the client on any unexpected shell exception — defense in depth, not decision
  logic (I2 already proves the core always commands teardown before waits).
- Exception classification (`FailureKind`) is type-only; all routing stays in
  the core.
- Probe calls keep their current 15 semantic positions, now anchored to command
  boundaries (e.g. `BeforeAcceptGuard` fires before executing the accept-phase
  `ObserveFlags`; `FinallyEnter`/`AfterCtsDispose` inside `TeardownAttempt`).
  The sweep matrix passing unchanged is the proof of correct placement.

### 4.4 Verification restructure (`tests/Lattice.Verification`)

`Model.fs` becomes the **shell model**: an environment wrapper around the
production `HostMachine.step`.

- Wrapper state `S` = env/shell primitives (curVersion, configChanged, cts,
  wake, started, loopTask, outerCanceled/outerDisposed, disposeFlag, budgets)
  + observables (statusState/statusVersion, daemonVersionVintage, logVintage,
  logReplacedThisConn, faulted) + `core: HostMachine.State`
  + `pending: Command list` (the interpreter's command queue).
- Loop micro-step = execute ONE pending command against wrapper state (this is
  where observables/vintage stamps are written and where `Flags`/`WaitEnded`
  observations are read from env state), then feed the resulting `Input` to
  the production `step` when the queue drains. Env actions interleave between
  micro-steps — same granularity as today's model and as the real shell.
- Tick payloads canonicalized to a bounded alphabet (`hasNewMessages` and
  `hasUnknownWorkunit` explored nondeterministically; seqno domain {0,1}), so
  the refetch branch is explored, and state count stays near the ~27.5k
  baseline (sanity-assert > 100 states as today; report the new count).
- Properties I1–I5 / L1–L3: same meanings, predicates re-expressed over the
  wrapper state. **Mutant redness is the check that re-expression didn't
  weaken anything**; reviewers empirically re-run reds (memory:
  subagent-model-selection).
- Mutants: M1/M5/M6/M7 mutate env/wait primitives (wrapper-level, as today);
  M2/M3/M4 become *step decorators* that corrupt the core's output the way the
  historical bugs did (attempt not reset; eager daemon-version stamp; eager
  log stamp). All 7 must fail their property.
- `Explorer.fs` unchanged (generic over `step`).

### 4.5 Docs and metadata

- `verification/README.md`: §1 layer table (F# spec column now "production
  core + shell model"), §2 correspondence rules re-grounded (rule 1 binds the
  `SnapshotConfig` command to one lock block; rule 2 inventory rewritten for
  the new field list incl. the machine-state local; rule 3 probe list
  unchanged), §3 inventory rewrite, §7 note that the sync rule's F#-spec leg
  is now discharged by construction for decision logic (shell changes still
  owe model+probe updates).
- `verification/HostMonitor.pml`: comments referencing `HostMonitor.cs` line
  numbers / method names updated; no semantic change (state why in commit).
- `CLAUDE.md`: solution-structure block gains `Lattice.Core.Machine`; one-line
  description of the functional-core split under the Core bullet.
- `.superpowers/sdd/progress.md`: ledger entries per task (this branch takes
  the ride-along `.mcp.json` + ledger edits from the main checkout, per the
  coordination note — noted in the ledger so the M2c session doesn't duplicate).

## 5. Commit plan (each commit green: build `-warnaserror` + full test suite)

1. `docs: functional-core restructure spec` + ride-along `.mcp.json` + ledger.
2. `feat(core): Lattice.Core.Machine — pure HostMachine transition core`
   - New F# project (+ sln, referenced by `Lattice.Core`), `HostConnectionState`
     moved in full (added to Machine AND removed from `ConnectionStatus.cs` in
     this commit — leaving both would be an ambiguous-type compile error),
     `HostMachine` implemented, `Model.fs`/`Properties.fs` rewritten as the
     shell model over the production step, mutants ported, all 18+ verification
     tests green, 193 C# tests untouched and green.
   - Does not touch `HostMonitor.cs` semantics → sync rule not triggered;
     commit message still records the model-refactor rationale.
3. `refactor(core): HostMonitor as interpreter shell over HostMachine`
   - `HostMonitor.cs` rewrite;
     README §§1–3/7 rewrite; pml comment touch-ups; CLAUDE.md update — same
     commit, satisfying the sync rule maximally; message states the pml has no
     semantic delta and why.
4. (if needed) review-fix commits from Codex rounds.

Gate before PR: local `dotnet build -warnaserror` + `dotnet test` (full sln),
plus CI's `model-check` job on push. PR merges only after Codex Review posts
(memory: wait-for-codex-review).

## 6. Testing strategy

- **Parity oracle**: the untouched 193-test C# suite + 45-case sweep matrix +
  lifecycle/reentrancy pins. These encode today's observable semantics,
  including event ordering and message-retention behavior.
- **Design layer**: rewritten F# spec explores the production core exhaustively;
  7 red-first mutants prove the properties still bite.
- **Double-check layer**: Promela unchanged ⇒ still proves the same protocol;
  its agreement with the F# layer is now agreement with production code.
- No new C# tests expected; if the interpreter needs a new seam, that is a
  design smell to surface, not to paper over.

## 7. Risks

- **Event-sequence parity** (biggest): status publishes interleave with client
  calls in a specific order (factory → Connecting publish → connect). The
  command vocabulary must reproduce it exactly; the state-machine tests pin
  most of it, sweep pins the racy parts.
- **F#-from-C# friction**: DU consumption in the shell switch — mitigated by
  giving `Command`/`Input` named fields and keeping the shell switch flat.
- **State-space blowup** in the wrapper (command queue + payload alphabet):
  bounded by budgets; assert exploration size stays same order of magnitude.
- **Property re-expression weakening**: mitigated by mutants + empirical-red
  review rule.

## 8. Milestone fit

Pre-M2c infrastructure hardening; no UI impact. M2c consumes the same public
surface (coordination: whichever branch lands first, the other rebases).

## 9. Judgment calls made autonomously (for user review at PR time)

- **J1 — naming**: project `Lattice.Core.Machine`, module `HostMachine`, root
  namespace `Lattice.Core`. "Protocol" was rejected (collides with the GuiRpc
  wire-protocol layer's vocabulary).
- **J2 — `HostConnectionState` moves assemblies** (same namespace). Source
  compatible; Lattice.Core is not a published package (M1 ships GuiRpc only).
  Alternative (duplicate enum + mapping) rejected as a drift surface.
- **J3 — guard reads are commands** (`ObserveFlags`), not input decorations:
  keeps guard placement in the verified core; costs one interpreter round-trip
  per guard, irrelevant next to network RPCs.
- **J4 — machine surface is `public` but documented non-API** (no
  InternalsVisibleTo daisy chain across three consumers).
- **J5 — `HostSessionLostException`/`HostAuthException` disappear**: they were
  intra-method routing signals; the interpreter routes via `Input`/`FailureKind`
  instead. Not a semantic change (both are `internal`).
- **J6 — refetch branch modeled** in the wrapper via a nondeterministic
  `hasUnknownWorkunit` bit (it was previously abstracted away entirely); the
  alternative (keep it unmodeled) left a core branch unexplored by design.
- **J7 — functional-idiom bar** (constraint from dispatch): controller reviews
  all subagent F# for needless `mutable`/imperative loops; `Explorer.fs`'s
  imperative BFS/SCC kernels stay imperative by design (documented exemption).
