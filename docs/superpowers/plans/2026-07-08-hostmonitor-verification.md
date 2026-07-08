# HostMonitor Formal Verification Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Machine-checked verification of `HostMonitor`'s concurrency protocol: an F# executable spec (primary, exhaustive BFS + liveness), a Promela/Spin model (independent double-check), a C# probe-point interleaving sweep harness, plus three code riders fixing the known pre-accept/lifecycle defects.

**Architecture:** Design layer = two independent encodings of one protocol (F# transition system checked by our own explorer as xUnit tests; Promela checked by Spin in a new CI job). Implementation layer = probe seam in `HostMonitor` + sweep tests freezing the real loop at every interleaving point against every environment action. Spec: `docs/superpowers/specs/2026-07-08-hostmonitor-formal-verification-design.md` (5 Codex review rounds, settled).

**Tech Stack:** F# (new test project `tests/Lattice.Verification`, xUnit), Promela/Spin (+gcc, ubuntu CI), C# (probe seam + sweep in `tests/Lattice.Tests`).

## Global Constraints

- All commits: conventional messages, end with `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.
- `dotnet build -c Release -warnaserror` must stay clean; F# project sets `<WarningLevel>5</WarningLevel>` and `<OtherFlags>--warnaserror</OtherFlags>` — exhaustive-match warnings are build failures.
- Never print/log/format RPC passwords anywhere, including tests.
- **Verification sync rule (CLAUDE.md):** any semantic change to `src/Lattice.Core/HostMonitor.cs` in Tasks 7–10 must update the F# spec / Promela model / probe list in the SAME commit, or the commit message must state why no model change is needed.
- The F# model references `Lattice.Core` types where they exist (`HostConnectionState`); model-only concepts get model-local types.
- Property numbering I1–I5, L1–L3 (spec) is shared verbatim across F#, Promela, harness assertions, and the README cross-reference table.
- No real sleeps in tests; existing `FakeTimeProvider`/`Wait` helpers for the C# harness.
- Existing 139 tests must stay green after every task.

## File Structure

```
tests/Lattice.Verification/
├── Lattice.Verification.fsproj   # F# xUnit project; references Lattice.Core
├── Model.fs                      # state record, action DUs, step function (target protocol)
├── Explorer.fs                   # BFS reachability, trace reconstruction, bottom-SCC liveness
├── Properties.fs                 # I1–I5 safety invariants, L1–L3 liveness, red-first mutant tests
verification/
├── HostMonitor.pml               # Promela double-check model (same properties)
├── README.md                     # correspondence rules, shared-state inventory, assumptions,
│                                 #   memory-model note, F#↔Promela property cross-ref table
scripts/
├── model-check.sh                # spin -a → gcc → pan; safety run + one run per LTL property
.github/workflows/ci.yml          # + model-check job (ubuntu, spin+gcc)
src/Lattice.Core/HostMonitor.cs   # probe seam + riders A/B/C
src/Lattice.Core/MessageLog.cs    # + ReplaceAll
tests/Lattice.Tests/Interleaving/
├── ProbeController.cs            # freeze/release controller for the probe seam
├── SweepTests.cs                 # A1–A5 point×action matrix
├── LifecycleTests.cs             # A6 double-dispose / start-after-dispose / race
├── ReentrancyTests.cs            # reentrant UpdateConfig from StatusChanged handler
```

Model transcribes the TARGET protocol (riders applied). Riders land in Tasks 7–9; the C# harness turns fully green only after them — each rider task carries its own red-first evidence.

---

### Task 1: F# spec project + Model.fs

**Files:**
- Create: `tests/Lattice.Verification/Lattice.Verification.fsproj`
- Create: `tests/Lattice.Verification/Model.fs`
- Modify: `Lattice.sln` (add project)

**Interfaces:**
- Consumes: `Lattice.Core.HostConnectionState` (existing enum: Disconnected, Connecting, Authorizing, FetchingState, Connected, Retrying, AuthFailed).
- Produces: `Model.S` (state record), `Model.Action` (DU), `Model.step : S -> Action -> S list`, `Model.enabled : S -> Action list`, `Model.initial : Bounds -> S`, `Model.Bounds`. Task 2's explorer and Task 3's properties depend on these exact names.

- [ ] **Step 1: Create the project and add to solution**

`tests/Lattice.Verification/Lattice.Verification.fsproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>false</IsPackable>
    <OtherFlags>--warnaserror</OtherFlags>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Model.fs" />
    <Compile Include="Explorer.fs" />
    <Compile Include="Properties.fs" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.1" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Lattice.Core\Lattice.Core.csproj" />
  </ItemGroup>
</Project>
```

Match the xunit/test-sdk versions used by `tests/Lattice.Tests/Lattice.Tests.csproj` (read it first; if they differ from the above, use the repo's versions). Create placeholder empty `Explorer.fs` / `Properties.fs` (`namespace Lattice.Verification` line only) so the project compiles this task.

Run: `dotnet sln Lattice.sln add tests/Lattice.Verification/Lattice.Verification.fsproj`

- [ ] **Step 2: Write Model.fs (the full target-protocol transition system)**

`tests/Lattice.Verification/Model.fs` — complete content:

```fsharp
/// Executable specification of HostMonitor's concurrency protocol (TARGET semantics:
/// riders A/B/C from the 2026-07-08 spec applied). PRIMARY design-level check.
///
/// Primitive anchoring (spec §"Primitive-semantics anchoring"):
///  - the Loop is the C# async state machine: Phase = continuation points (awaits) +
///    lock-block boundaries; nothing else is an interleaving point for the loop itself
///  - lock(_gate) blocks are single atomic steps (one Phase transition)
///  - env actions (UpdateConfig/Wake/Start/Dispose/SetInterval) may fire between ANY
///    two steps — shared-memory threads interleave anywhere
///  - TCS wake = one-shot latch (sticky bit); CTS = monotonic cancel bit + Disposed
///    fault state; volatile flags = plain bits under the SC note in verification/README.md
module Lattice.Verification.Model

open Lattice.Core

/// Continuation/interleaving points of the loop's async state machine.
type Phase =
    | Idle              // before Start stored the loop task
    | Dispatch          // top of RunAsync iteration
    | SnapshotBlock     // about to execute the atomic snapshot lock block
    | AwaitConnect      // ConnectAsync in flight
    | AwaitAuth         // AuthorizeAsync in flight
    | AwaitFetch        // ExchangeVersions+GetState in flight
    | AcceptGuard       // about to read configChanged (pre-Connected guard)
    | PublishConnected  // guard passed; about to publish Connected (writes daemonVersion)
    | TickRpcs          // tick RPCs in flight (GetCcStatus..GetMessages)
    | MsgGuard          // about to read configChanged before message publish
    | MsgPublish        // about to ReplaceAll(first tick)/Append + raise MessagesAdded
    | SnapGuard         // about to read configChanged before snapshot publish
    | SnapPublish       // about to write Snapshot + raise SnapshotUpdated
    | PollWait          // interval wait (delay|wake)
    | Teardown          // finally: dispose linked CTS, dispose client
    | RetryDecide       // dispatcher Failed arm: compute attempt, publish Retrying
    | BackoffWait       // backoff wait (delay|wake)
    | ParkedAuthFailed  // AuthFailed park (only config change releases)
    | Exited            // loop completed; Disconnected published

type CtsState = CtsNone | CtsLive | CtsCanceled | CtsDisposed
type LoopTask = TaskInitial | TaskStored | TaskRunning | TaskFaulted | TaskDone

/// Environment budgets keep the state space finite (spec bounds).
type Bounds = { updates: int; wakes: int; failures: int; disposes: int }

type S = {
    phase: Phase
    // ---- gate-protected shared state ----
    curVersion: int              // _config generation; UpdateConfig increments
    configChanged: bool
    cts: CtsState                // per-attempt linked _connectionCts
    wake: bool                   // sticky wake latch (_wake TCS completed?)
    started: bool
    loopTask: LoopTask
    outerCanceled: bool          // _cts.Cancel() happened (DisposeAsync)
    outerDisposed: bool          // _cts.Dispose() happened (end of DisposeAsync)
    disposeFlag: bool            // rider C: _disposed idempotency flag
    // ---- attempt-local ----
    attemptVersion: int          // config generation this attempt snapshotted
    connLive: bool               // client connection open
    reachedConnected: bool
    firstTickPending: bool       // rider B: next tick is the connection's first
    injectedFail: bool           // this attempt was chosen to fail (consumes failure budget)
    attempt: int                 // dispatcher retry counter (capped by cap())
    // ---- observables the properties read ----
    statusState: HostConnectionState
    statusVersion: int           // vintage carried by last status publish
    daemonVersionVintage: int option  // vintage of _daemonVersion field (rider A: accepted only)
    logVintage: int option       // vintage of message-log CONTENT (None = empty/initial)
    logReplacedThisConn: bool
    faulted: bool                // I5 violation marker: any disposed-resource fault
    // ---- history for L1 (lost wakeup) ----
    wakeSetDuringWait: bool      // a Wake landed while phase was a wait
    waitExitedByDelay: bool      // the wait then exited via delay with wake still pending
    // ---- env budgets ----
    updatesLeft: int; wakesLeft: int; failsLeft: int; disposesLeft: int
}

let cap = 3 // attempt counter cap (bounds the Retrying counter domain)

let initial (b: Bounds) = {
    phase = Idle
    curVersion = 0; configChanged = false; cts = CtsNone; wake = false
    started = false; loopTask = TaskInitial
    outerCanceled = false; outerDisposed = false; disposeFlag = false
    attemptVersion = -1; connLive = false; reachedConnected = false
    firstTickPending = false; injectedFail = false; attempt = 0
    statusState = HostConnectionState.Disconnected; statusVersion = 0
    daemonVersionVintage = None; logVintage = None; logReplacedThisConn = false
    faulted = false
    wakeSetDuringWait = false; waitExitedByDelay = false
    updatesLeft = b.updates; wakesLeft = b.wakes; failsLeft = b.failures; disposesLeft = b.disposes
}

type Action =
    // environment (any interleaving point)
    | EnvStart
    | EnvUpdateConfig
    | EnvWake                   // RequestRefresh / SetPollingInterval's Wake
    | EnvDispose
    // loop micro-steps (one per Phase, plus nondeterministic outcomes)
    | LoopStep                  // deterministic continuation of the current phase
    | LoopStepFail              // failure-injected variant (await faults / RPC throws)
    | DelayFires                // a wait's timer completes
    | WakeConsumed              // a wait observes the sticky wake

let private isWaitPhase p = p = PollWait || p = BackoffWait || p = ParkedAuthFailed

/// Which actions are enabled in s. The explorer expands all of them (that IS the
/// nondeterministic interleaving).
let enabled (s: S) : Action list =
    let env =
        [ if not s.started then EnvStart                      // Start any time (idempotent)
          if s.updatesLeft > 0 then EnvUpdateConfig
          if s.wakesLeft > 0 then EnvWake
          if s.disposesLeft > 0 then EnvDispose ]
    let loop =
        if s.faulted then []                                  // fault is terminal for the loop
        elif s.phase = Idle || s.phase = Exited then []
        elif isWaitPhase s.phase then
            [ if s.phase <> ParkedAuthFailed then DelayFires
              if s.wake then WakeConsumed
              if s.phase = ParkedAuthFailed && s.configChanged then LoopStep
              if s.outerCanceled then LoopStep ]              // waits observe disposal
        else
            [ LoopStep
              // failure injection at the awaits/RPC phases only
              if s.failsLeft > 0 && not s.injectedFail
                 && (s.phase = AwaitConnect || s.phase = AwaitAuth
                     || s.phase = AwaitFetch || s.phase = TickRpcs)
              then LoopStepFail ]
    env @ loop |> List.distinct

// ---- helpers ----
let private clampAttempt a = min a cap
let private exit' (s: S) =
    { s with phase = Exited; loopTask = TaskDone
             statusState = HostConnectionState.Disconnected
             connLive = false; outerDisposed = true }
/// Teardown's destination is computed from the same predicates RunAsync's
/// dispatcher reads (outerCanceled / auth sentinel / configChanged / injectedFail).

let step (s: S) (a: Action) : S list =
    match a with
    // ---------------- environment ----------------
    | EnvStart ->
        // Rider C semantics: lock { if started||disposeFlag return; started=true;
        //   token=_cts.Token (safe: outerDisposed can't be true while lock held pre-cancel);
        //   store loop task }
        if s.started || s.disposeFlag then [ s ]              // idempotent no-op (state unchanged)
        elif s.outerDisposed then
            // pre-rider defect shape: token read after dispose → faulted task.
            // TARGET protocol: disposeFlag is always set before outerDisposed (see
            // EnvDispose), so this branch must be unreachable. Keep it as a fault so
            // a broken EnvDispose encoding cannot hide it (mutant M5 relies on this).
            [ { s with started = true; loopTask = TaskFaulted; faulted = true } ]
        else
            [ { s with started = true; loopTask = TaskStored
                       phase = (if s.phase = Idle then Dispatch else s.phase) } ]
    | EnvUpdateConfig ->
        // lock { _config=new; _configChanged=true; _connectionCts?.Cancel() } ; Wake()
        let cts' = if s.cts = CtsLive then CtsCanceled else s.cts
        [ { s with curVersion = s.curVersion + 1; configChanged = true; cts = cts'
                   wake = true; updatesLeft = s.updatesLeft - 1
                   wakeSetDuringWait = s.wakeSetDuringWait || isWaitPhase s.phase } ]
    | EnvWake ->
        [ { s with wake = true; wakesLeft = s.wakesLeft - 1
                   wakeSetDuringWait = s.wakeSetDuringWait || isWaitPhase s.phase } ]
    | EnvDispose ->
        // Rider C: lock { if disposeFlag → (second call: await loop only) ; disposeFlag=true }
        // then Cancel+Wake, await loop, Dispose(_cts).
        // Model at this granularity: disposeFlag+outerCanceled set atomically here;
        // outerDisposed is set by the loop reaching Exited (await-then-dispose is
        // sequenced after loop completion, so no live step can observe a disposed CTS).
        if s.disposeFlag then [ { s with disposesLeft = s.disposesLeft - 1 } ] // idempotent
        else
            [ { s with disposeFlag = true; outerCanceled = true; wake = true
                       disposesLeft = s.disposesLeft - 1
                       cts = (if s.cts = CtsLive then CtsCanceled else s.cts)
                       outerDisposed = (s.loopTask = TaskInitial || s.phase = Idle
                                        || s.phase = Exited) } ]
            // if the loop never started (or already exited), DisposeAsync completes
            // immediately: _cts is disposed right away. A later EnvStart must then be
            // a no-op via disposeFlag — never a token read (I5).

    // ---------------- loop ----------------
    | DelayFires ->
        match s.phase with
        | PollWait ->
            let s = { s with waitExitedByDelay = s.waitExitedByDelay || s.wake }
            [ { s with phase = (if s.configChanged || s.outerCanceled then Teardown else TickRpcs) } ]
        | BackoffWait ->
            let s = { s with waitExitedByDelay = s.waitExitedByDelay || s.wake }
            if s.outerCanceled then [ exit' s ]
            else [ { s with phase = Dispatch
                            attempt = (if s.configChanged then 0 else s.attempt) } ]
        | _ -> [ s ]
    | WakeConsumed ->
        // WaitAsync: lock { if wake.completed { renew; return true } } — consumes the latch
        match s.phase with
        | PollWait ->
            [ { s with wake = false
                       phase = (if s.configChanged || s.outerCanceled then Teardown else TickRpcs) } ]
        | BackoffWait ->
            let s = { s with wake = false }
            if s.outerCanceled then [ exit' s ]
            else [ { s with phase = Dispatch
                            attempt = (if s.configChanged then 0 else s.attempt) } ]
        | ParkedAuthFailed ->
            // stale wakes are consumed and ignored; only configChanged/disposal release
            [ { s with wake = false } ]
        | _ -> [ s ]
    | LoopStepFail ->
        // an in-flight await faults (network error / RPC exception): attempt fails
        [ { s with injectedFail = true; failsLeft = s.failsLeft - 1; connLive = s.connLive
                   phase = Teardown } ]
    | LoopStep ->
        match s.phase with
        | Idle | Exited -> [ s ]
        | Dispatch ->
            if s.outerCanceled then [ { s with phase = Exited; loopTask = TaskDone
                                               statusState = HostConnectionState.Disconnected
                                               outerDisposed = true } ]
            else [ { s with phase = SnapshotBlock } ]
        | SnapshotBlock ->
            // THE atomic block: read config, clear flag, create linked CTS (one step —
            // correspondence rule 1; mutant M1 splits it and must violate I3)
            [ { s with attemptVersion = s.curVersion; configChanged = false; cts = CtsLive
                       injectedFail = false; reachedConnected = false
                       firstTickPending = true; logReplacedThisConn = false
                       phase = AwaitConnect; connLive = false } ]
        | AwaitConnect ->
            if s.cts = CtsCanceled || s.outerCanceled then [ { s with phase = Teardown } ]
            else
                [ { s with connLive = true
                           statusState = HostConnectionState.Connecting
                           statusVersion = s.attemptVersion; phase = AwaitAuth } ]
        | AwaitAuth ->
            if s.cts = CtsCanceled || s.outerCanceled then [ { s with phase = Teardown } ]
            else
                // nondeterministic: auth ok | refused (AuthFailed park path)
                [ { s with statusState = HostConnectionState.Authorizing
                           statusVersion = s.attemptVersion; phase = AwaitFetch }
                  { s with phase = Teardown; injectedFail = false
                           // route: park (encoded by attempt = -1 sentinel consumed at Teardown)
                           attempt = -1 } ]
        | AwaitFetch ->
            if s.cts = CtsCanceled || s.outerCanceled then [ { s with phase = Teardown } ]
            else
                [ { s with statusState = HostConnectionState.FetchingState
                           statusVersion = s.attemptVersion; phase = AcceptGuard } ]
        | AcceptGuard ->
            // rider B: NO log mutation here anymore. Guard only.
            if s.configChanged then [ { s with phase = Teardown } ]
            else [ { s with phase = PublishConnected } ]
        | PublishConnected ->
            // rider A: daemonVersion field written HERE (accepted attempt only)
            [ { s with daemonVersionVintage = Some s.attemptVersion
                       reachedConnected = true; attempt = 0
                       statusState = HostConnectionState.Connected
                       statusVersion = s.attemptVersion
                       phase = TickRpcs } ]
        | TickRpcs ->
            if s.cts = CtsCanceled || s.outerCanceled then [ { s with phase = Teardown } ]
            else [ { s with phase = MsgGuard } ]
        | MsgGuard ->
            if s.configChanged then [ { s with phase = Teardown } ]   // PollAsync returns via guards
            else [ { s with phase = MsgPublish } ]
        | MsgPublish ->
            // rider B: first tick REPLACES the log (old→new atomic); later ticks append.
            [ { s with logVintage = Some s.attemptVersion
                       logReplacedThisConn = s.logReplacedThisConn || s.firstTickPending
                       firstTickPending = false
                       phase = SnapGuard } ]
        | SnapGuard ->
            if s.configChanged then [ { s with phase = Teardown } ]
            else [ { s with phase = SnapPublish } ]
        | SnapPublish ->
            [ { s with phase = PollWait } ]   // snapshot vintage tracked via statusVersion path (I1 checks guard adjacency structurally)
        | PollWait | BackoffWait -> [ s ]   // waits move via DelayFires/WakeConsumed
        | ParkedAuthFailed ->
            // release ONLY on config change or disposal (L3b); stale wakes are
            // handled by WakeConsumed
            if s.outerCanceled then [ exit' s ]
            elif s.configChanged then [ { s with attempt = 0; phase = Dispatch } ]
            else [ s ]
        | Teardown ->
            // finally: lock { cts.Dispose; cts=null }; client.Dispose. Then dispatcher routes.
            let s = { s with cts = CtsNone; connLive = false }
            if s.outerCanceled then
                [ { s with phase = Exited; loopTask = TaskDone
                           statusState = HostConnectionState.Disconnected
                           outerDisposed = true } ]
            elif s.attempt = -1 then
                // AuthFailed park (publish AFTER teardown — connection provably closed)
                [ { s with attempt = 0; statusState = HostConnectionState.AuthFailed
                           statusVersion = s.attemptVersion; phase = ParkedAuthFailed } ]
            elif s.configChanged || not s.injectedFail then
                // ConfigChanged outcome (incl. PollAsync's guard returns): immediate reconnect
                [ { s with attempt = 0; phase = Dispatch } ]
            else
                // Failed outcome: I4 — post-Connected failure resets, then counts as 1
                let a = clampAttempt (if s.reachedConnected then 1 else s.attempt + 1)
                [ { s with attempt = a; phase = RetryDecide } ]
        | RetryDecide ->
            [ { s with statusState = HostConnectionState.Retrying
                       statusVersion = s.attemptVersion; phase = BackoffWait } ]
```

Note the deliberate asymmetries the properties rely on: `daemonVersionVintage`/`logVintage` are written ONLY in `PublishConnected`/`MsgPublish` (rider semantics — pre-accept phases cannot touch them, making I1's mutation half hold *by construction*, which is exactly what the model must express); `attempt = -1` is the AuthFailed routing sentinel consumed at `Teardown`.

- [ ] **Step 3: Build**

Run: `dotnet build -c Release -warnaserror`
Expected: clean. If F# exhaustive-match warnings fire, the model has an unhandled state×action combination — fix the model, do not suppress the warning.

- [ ] **Step 4: Commit**

```bash
git add tests/Lattice.Verification Lattice.sln
git commit -m "feat(verify): F# executable spec project + HostMonitor protocol model"
```

---

### Task 2: Explorer.fs — BFS reachability + safety checking, red-first via mutants

**Files:**
- Create: `tests/Lattice.Verification/Explorer.fs` (replace placeholder)
- Modify: `tests/Lattice.Verification/Properties.fs` (safety tests + mutant tests)

**Interfaces:**
- Consumes: `Model.S`, `Model.Action`, `Model.step`, `Model.enabled`, `Model.initial`, `Model.Bounds`.
- Produces: `Explorer.Reach` record (`states`, `edges`, `parent`, `initial`); `Explorer.explore : (S -> Action -> S list) -> S -> Reach`; `Explorer.trace : Reach -> S -> string`; `Explorer.checkInvariant : Reach -> string -> (S -> bool) -> unit` (throws with reconstructed action trace on violation). Task 3 adds liveness to the same module.

- [ ] **Step 1: Write a failing smoke test (Properties.fs)**

```fsharp
module Lattice.Verification.Properties

open Xunit
open Lattice.Verification
open Lattice.Verification.Model

let bounds = { updates = 2; wakes = 2; failures = 3; disposes = 2 }

[<Fact>]
let ``exploration terminates and reaches Connected`` () =
    let r = Explorer.explore Model.step (Model.initial bounds)
    Assert.True(r.states.Count > 100)
    Assert.Contains(r.states, fun s -> s.statusState = Lattice.Core.HostConnectionState.Connected)
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Lattice.Verification -c Release --filter "exploration"`
Expected: FAIL — `Explorer.explore` undefined.

- [ ] **Step 3: Implement Explorer.fs**

```fsharp
/// Explicit-state BFS explorer. Small by design: reviewability IS the trust
/// argument; the red-first mutants in Properties.fs exercise the checker itself.
module Lattice.Verification.Explorer

open System.Collections.Generic
open Lattice.Verification.Model

type Reach = {
    states: HashSet<S>
    edges: Dictionary<S, (Action * S) list>
    parent: Dictionary<S, S * Action>
    initial: S
}

let explore (step: S -> Action -> S list) (init: S) : Reach =
    let states = HashSet<S>(HashIdentity.Structural)
    let edges = Dictionary<S, (Action * S) list>(HashIdentity.Structural)
    let parent = Dictionary<S, S * Action>(HashIdentity.Structural)
    let queue = Queue<S>()
    states.Add init |> ignore
    queue.Enqueue init
    while queue.Count > 0 do
        let s = queue.Dequeue()
        let outs =
            [ for a in enabled s do
                for s2 in step s a do
                    if s2 <> s then yield (a, s2) ]   // drop no-op self-loops
        edges[s] <- outs
        for (a, s2) in outs do
            if states.Add s2 then
                parent[s2] <- (s, a)
                queue.Enqueue s2
    { states = states; edges = edges; parent = parent; initial = init }

/// Reconstruct init → s as an action trace for counterexample messages.
let trace (r: Reach) (target: S) : string =
    let rec walk s acc =
        match r.parent.TryGetValue s with
        | true, (p, a) -> walk p ((sprintf "%A" a) :: acc)
        | false, _ -> acc
    let steps = walk target []
    sprintf "trace (%d steps):%s  %s%sfinal: %A"
        steps.Length System.Environment.NewLine
        (String.concat (System.Environment.NewLine + "  ") steps)
        System.Environment.NewLine target

let checkInvariant (r: Reach) (name: string) (ok: S -> bool) : unit =
    match r.states |> Seq.tryFind (ok >> not) with
    | Some bad -> failwithf "INVARIANT %s violated. %s" name (trace r bad)
    | None -> ()
```

Structural equality on records makes dedup free; dropping self-loop no-op edges (idempotent Start etc.) keeps the graph small.

- [ ] **Step 4: Run the smoke test**

Run: `dotnet test tests/Lattice.Verification -c Release --filter "exploration"`
Expected: PASS. Note the state count in the output — expect low tens of thousands at Task 1 bounds. If it exceeds ~1M, the model has an unintended unbounded counter (versions are bounded by `updates`, `attempt` is capped, budgets only decrement — find the leak, do not raise limits).

- [ ] **Step 5: Add safety properties I1–I5 to Properties.fs**

Append to Properties.fs:

```fsharp
let reach = lazy (Explorer.explore Model.step (Model.initial bounds))

// I1 publish half: a guard that observed configChanged never proceeds to its publish.
[<Fact>]
let ``I1 guards bar publishes when a config change is pending`` () =
    for KeyValue(s, outs) in reach.Value.edges do
        let guardIgnored (guardPhase, publishPhase) =
            if s.phase = guardPhase && s.configChanged then
                for (_, s2) in outs do
                    if s2.phase = publishPhase then
                        failwithf "%A ignored configChanged. %s" guardPhase (Explorer.trace reach.Value s2)
        [ Model.AcceptGuard, Model.PublishConnected
          Model.MsgGuard,    Model.MsgPublish
          Model.SnapGuard,   Model.SnapPublish ] |> List.iter guardIgnored

// I1 mutation half: pre-accept phases never touch daemonVersion or the log.
// Vintage discipline: an attempt that has not reached Connected must not have
// stamped ITS OWN version into either observable.
[<Fact>]
let ``I1 no pre-accept mutation of daemonVersion or message log`` () =
    Explorer.checkInvariant reach.Value "I1-mutation" (fun s ->
        s.reachedConnected
        || (s.daemonVersionVintage <> Some s.attemptVersion
            && s.logVintage <> Some s.attemptVersion)
        || s.attemptVersion < 0)

// I2: no connection live during backoff/park/exit paths.
[<Fact>]
let ``I2 connection closed during backoff and park`` () =
    Explorer.checkInvariant reach.Value "I2" (fun s ->
        not (s.phase = Model.BackoffWait || s.phase = Model.ParkedAuthFailed
             || s.phase = Model.RetryDecide || s.phase = Model.Exited)
        || not s.connLive)

// I3: no unabortable stale attempt (round-7's property).
[<Fact>]
let ``I3 no unabortable stale attempt`` () =
    Explorer.checkInvariant reach.Value "I3" (fun s ->
        s.attemptVersion = s.curVersion
        || s.configChanged
        || s.cts = Model.CtsCanceled
        || s.phase = Model.Teardown || s.phase = Model.Dispatch
        || s.phase = Model.SnapshotBlock || s.phase = Model.RetryDecide
        || s.phase = Model.BackoffWait || s.phase = Model.ParkedAuthFailed
        || s.phase = Model.Exited || s.phase = Model.Idle)

// I4: Retrying after a post-Connected failure carries attempt = 1 (round-6's property).
[<Fact>]
let ``I4 attempt counter resets after a connected session`` () =
    for KeyValue(s, outs) in reach.Value.edges do
        if s.phase = Model.Teardown && s.reachedConnected && s.injectedFail
           && not s.outerCanceled && not s.configChanged && s.attempt <> -1 then
            for (_, s2) in outs do
                if s2.phase = Model.RetryDecide && s2.attempt <> 1 then
                    failwithf "I4: attempt=%d after connected session. %s"
                        s2.attempt (Explorer.trace reach.Value s2)

// I5: no reachable disposed-resource fault; lifecycle idempotency by construction.
[<Fact>]
let ``I5 lifecycle safety - no disposed-resource faults`` () =
    Explorer.checkInvariant reach.Value "I5" (fun s -> not s.faulted)
```

- [ ] **Step 6: Run safety tests**

Run: `dotnet test tests/Lattice.Verification -c Release`
Expected: ALL PASS. A failure here is a MODEL bug (the model encodes target semantics): read the printed trace, fix `Model.fs`. Never weaken a property to get green; if a property seems wrong, stop and re-read the spec's property definitions.

- [ ] **Step 7: Red-first — mutant checks (prove the checker can fail)**

Append to Properties.fs. Each mutant is a transcription of a REAL historical bug; each must be DETECTED by its property. Use one shared helper:

```fsharp
/// true when running `check` over the mutant's reachable graph throws.
let private violates (check: Explorer.Reach -> unit) (mutantStep: S -> Action -> S list) =
    let r = Explorer.explore mutantStep (Model.initial bounds)
    try check r; false with _ -> true

module private Mutants =
    open Model
    // M1 (PR#7 round-7): snapshot atomic block split — flag cleared one step early,
    // before the CTS exists.
    let m1SplitSnapshot (s: S) (a: Action) =
        match a, s.phase with
        | LoopStep, Dispatch when not s.outerCanceled ->
            [ { s with phase = SnapshotBlock; configChanged = false } ]
        | _ -> step s a
    // M2 (PR#7 round-6): Failed-after-Connected keeps counting instead of resetting.
    let m2NoAttemptReset (s: S) (a: Action) =
        match a, s.phase with
        | LoopStep, Teardown when s.injectedFail && s.reachedConnected
                                   && not s.outerCanceled && not s.configChanged
                                   && s.attempt <> -1 ->
            [ { s with cts = CtsNone; connLive = false
                       attempt = min (s.attempt + 1) cap; phase = RetryDecide } ]
        | _ -> step s a
    // M3 (PR#7 round-9 P2, pre-rider-A): daemonVersion stamped at fetch time.
    let m3EagerDaemonVersion (s: S) (a: Action) =
        match a, s.phase with
        | LoopStep, AwaitFetch when s.cts <> CtsCanceled && not s.outerCanceled ->
            [ { s with statusVersion = s.attemptVersion
                       daemonVersionVintage = Some s.attemptVersion
                       phase = AcceptGuard } ]
        | _ -> step s a
    // M4 (PR#8 round-2, pre-rider-B): eager log clear before the accept guard.
    let m4EagerLogClear (s: S) (a: Action) =
        match a, s.phase with
        | LoopStep, AwaitFetch when s.cts <> CtsCanceled && not s.outerCanceled ->
            [ { s with statusVersion = s.attemptVersion
                       logVintage = Some s.attemptVersion
                       phase = AcceptGuard } ]
        | _ -> step s a
    // M5 (PR#8 audit, pre-rider-C): dispose without the idempotency flag —
    // start-after-dispose can read a disposed token.
    let m5NoDisposeFlag (s: S) (a: Action) =
        match a with
        | EnvDispose when not s.disposeFlag ->
            [ { s with outerCanceled = true; wake = true
                       disposesLeft = s.disposesLeft - 1
                       cts = (if s.cts = CtsLive then CtsCanceled else s.cts)
                       outerDisposed = (s.loopTask = TaskInitial || s.phase = Idle
                                        || s.phase = Exited) } ]
        | _ -> step s a

[<Fact>]
let ``mutant M1 split snapshot violates I3`` () =
    Assert.True(violates (fun r ->
        Explorer.checkInvariant r "I3" (fun s ->
            s.attemptVersion = s.curVersion || s.configChanged || s.cts = Model.CtsCanceled
            || s.phase = Model.Teardown || s.phase = Model.Dispatch
            || s.phase = Model.SnapshotBlock || s.phase = Model.RetryDecide
            || s.phase = Model.BackoffWait || s.phase = Model.ParkedAuthFailed
            || s.phase = Model.Exited || s.phase = Model.Idle))
        Mutants.m1SplitSnapshot)

[<Fact>]
let ``mutant M2 no attempt reset violates I4`` () =
    let r = Explorer.explore Mutants.m2NoAttemptReset (Model.initial bounds)
    let mutable caught = false
    for KeyValue(s, outs) in r.edges do
        if s.phase = Model.Teardown && s.reachedConnected && s.injectedFail
           && not s.outerCanceled && not s.configChanged && s.attempt <> -1 then
            for (_, s2) in outs do
                if s2.phase = Model.RetryDecide && s2.attempt <> 1 then caught <- true
    Assert.True(caught, "mutant M2 was not detected by I4")

[<Fact>]
let ``mutant M3 eager daemonVersion violates I1-mutation`` () =
    Assert.True(violates (fun r ->
        Explorer.checkInvariant r "I1m" (fun s ->
            s.reachedConnected || s.daemonVersionVintage <> Some s.attemptVersion
            || s.attemptVersion < 0))
        Mutants.m3EagerDaemonVersion)

[<Fact>]
let ``mutant M4 eager log clear violates I1-mutation`` () =
    Assert.True(violates (fun r ->
        Explorer.checkInvariant r "I1m" (fun s ->
            s.reachedConnected || s.logVintage <> Some s.attemptVersion
            || s.attemptVersion < 0))
        Mutants.m4EagerLogClear)

[<Fact>]
let ``mutant M5 no dispose flag violates I5`` () =
    Assert.True(violates (fun r ->
        Explorer.checkInvariant r "I5" (fun s -> not s.faulted))
        Mutants.m5NoDisposeFlag)
```

**Calibration rule for the implementer:** if a mutant does NOT trip its property,
that is a finding — the property (or model observability) is too weak. STOP; fix the
property/model against the spec; never massage the mutant until it "fails right".
If field/guard names drift from Model.fs during implementation, reconcile toward
Model.fs everywhere.

- [ ] **Step 8: Run all, commit**

Run: `dotnet test tests/Lattice.Verification -c Release`
Expected: ALL PASS (a passing mutant test means the mutant WAS detected).

```bash
git add tests/Lattice.Verification
git commit -m "feat(verify): BFS explorer + I1-I5 safety properties + red-first mutants"
```

---

### Task 3: Liveness — bottom-SCC checking + L1–L3

**Files:**
- Modify: `tests/Lattice.Verification/Explorer.fs` (SCC + liveness)
- Modify: `tests/Lattice.Verification/Properties.fs` (L1–L3 + mutants M6–M7)

**Interfaces:**
- Consumes: `Explorer.Reach` from Task 2.
- Produces: `Explorer.bottomSccs : Reach -> S list list`; `Explorer.checkEventually : Reach -> string -> (S -> bool) -> (S -> bool) -> unit` — rule: every bottom SCC (no edges leaving it) must contain no state that is `pending` and not `goal`.

- [ ] **Step 1: Failing test first — L3**

```fsharp
[<Fact>]
let ``L3 disposal terminates the loop`` () =
    Explorer.checkEventually reach.Value "L3"
        (fun s -> s.outerCanceled)
        (fun s -> s.phase = Model.Exited || s.phase = Model.Idle)
```

Run: `dotnet test tests/Lattice.Verification -c Release --filter "L3"` — Expected: FAIL, `checkEventually` undefined.

- [ ] **Step 2: Implement SCC liveness in Explorer.fs**

Liveness rule justification (goes in a comment): finite graph + weak fairness (every
continuously enabled action eventually fires) means every infinite execution settles
into a bottom SCC and takes every internal edge infinitely often; "pending obligation
eventually discharges" therefore reduces to "no bottom SCC contains a
pending-and-not-goal state".

```fsharp
/// Kosaraju SCC (two DFS passes; graph is ~10^4-10^5 states, instant). Iterative —
/// no recursion, stack-safe.
let private sccs (r: Reach) : S list list =
    let succs s = match r.edges.TryGetValue s with | true, es -> es |> List.map snd | _ -> []
    // pass 1: finish order
    let visited = HashSet<S>(HashIdentity.Structural)
    let order = ResizeArray<S>()
    for root in r.states do
        if visited.Add root then
            let st = Stack<S * bool>()
            st.Push(root, false)
            while st.Count > 0 do
                let (v, processed) = st.Pop()
                if processed then order.Add v
                else
                    st.Push(v, true)
                    for w in succs v do
                        if visited.Add w then st.Push(w, false)
    // reverse graph
    let pred = Dictionary<S, ResizeArray<S>>(HashIdentity.Structural)
    for KeyValue(s, es) in r.edges do
        for (_, t) in es do
            match pred.TryGetValue t with
            | true, l -> l.Add s
            | false, _ -> let l = ResizeArray<S>() in l.Add s; pred[t] <- l
    // pass 2: reverse DFS in reverse finish order
    let assigned = HashSet<S>(HashIdentity.Structural)
    let result = ResizeArray<S list>()
    for i in (order.Count - 1) .. -1 .. 0 do
        let root = order[i]
        if assigned.Add root then
            let comp = ResizeArray<S>()
            let st = Stack<S>()
            st.Push root
            comp.Add root
            while st.Count > 0 do
                let v = st.Pop()
                let ps = match pred.TryGetValue v with | true, l -> List.ofSeq l | false, _ -> []
                for w in ps do
                    if assigned.Add w then
                        comp.Add w
                        st.Push w
            result.Add(List.ofSeq comp)
    List.ofSeq result

let bottomSccs (r: Reach) : S list list =
    let succs s = match r.edges.TryGetValue s with | true, es -> es |> List.map snd | _ -> []
    let all = sccs r
    let sccOf = Dictionary<S, int>(HashIdentity.Structural)
    all |> List.iteri (fun i comp -> for s in comp do sccOf[s] <- i)
    all |> List.mapi (fun i comp -> (i, comp))
        |> List.filter (fun (i, comp) ->
            comp |> List.forall (fun s -> succs s |> List.forall (fun t -> sccOf[t] = i)))
        |> List.map snd

let checkEventually (r: Reach) (name: string) (pending: S -> bool) (goal: S -> bool) : unit =
    for comp in bottomSccs r do
        match comp |> List.tryFind (fun s -> pending s && not (goal s)) with
        | Some bad ->
            failwithf "LIVENESS %s: bottom SCC where the obligation never discharges. %s"
                name (trace r bad)
        | None -> ()
```

**Note on the Kosaraju pass-1 transcription:** the iterative post-order above pushes
successors on first visit, which can record a finish order that differs from strict
DFS post-order when a node is re-reachable. For BOTTOM-SCC detection this does not
matter: `bottomSccs` re-validates bottomness against the real edge relation
(`forall` successors stay inside), so a mis-grouped SCC can only cause a FALSE RED
(over-splitting can mark a non-bottom component bottom — investigate any liveness
red by reading the trace; it is either a real violation or an SCC grouping artifact,
and the M6/M7 mutants prove real violations are caught). If a false red appears on
the healthy model, replace pass 1 with an explicit three-color DFS — the signatures
stay identical.

- [ ] **Step 3: L1, L2, L3b in Properties.fs**

```fsharp
// L1 lost wakeup — safety via history variables (spec): a wait never exits by delay
// while a wake that landed during that wait is still unconsumed.
[<Fact>]
let ``L1 no lost wakeup`` () =
    Explorer.checkInvariant reach.Value "L1" (fun s ->
        not (s.wakeSetDuringWait && s.waitExitedByDelay))

// L2 config convergence.
[<Fact>]
let ``L2 config change converges`` () =
    Explorer.checkEventually reach.Value "L2"
        (fun s -> s.configChanged)
        (fun s -> not s.configChanged || s.phase = Model.Exited)

// L3b: AuthFailed has no exit but config change or disposal (edge-shaped safety).
[<Fact>]
let ``L3b AuthFailed parks until config change or disposal`` () =
    for KeyValue(s, outs) in reach.Value.edges do
        if s.phase = Model.ParkedAuthFailed && not s.configChanged && not s.outerCanceled then
            for (a, s2) in outs do
                if s2.phase <> Model.ParkedAuthFailed && s2.phase <> Model.Exited then
                    failwithf "AuthFailed exited via %A without config change/disposal. %s"
                        a (Explorer.trace reach.Value s2)
```

- [ ] **Step 4: Red-first liveness mutants**

```fsharp
module private LivenessMutants =
    open Model
    // M6: deaf waits — the wake latch is never observed (WaitAsync forgets the
    // completed-check): lost wakeups become reachable; L1 must catch it.
    let m6DeafWaits (s: S) (a: Action) =
        match a with
        | WakeConsumed -> []
        | _ -> step s a
    // M7: stuck park — ParkedAuthFailed ignores configChanged; L2 must catch it.
    let m7StuckPark (s: S) (a: Action) =
        match a, s.phase with
        | LoopStep, ParkedAuthFailed -> [ s ]
        | WakeConsumed, ParkedAuthFailed -> [ { s with wake = false } ]
        | _ -> step s a

[<Fact>]
let ``mutant M6 deaf waits violates L1`` () =
    Assert.True(violates (fun r ->
        Explorer.checkInvariant r "L1" (fun s ->
            not (s.wakeSetDuringWait && s.waitExitedByDelay)))
        LivenessMutants.m6DeafWaits)

[<Fact>]
let ``mutant M7 stuck park violates L2`` () =
    Assert.True(violates (fun r ->
        Explorer.checkEventually r "L2" (fun s -> s.configChanged)
            (fun s -> not s.configChanged || s.phase = Model.Exited))
        LivenessMutants.m7StuckPark)
```

M6 note: `enabled` still offers `WakeConsumed`; returning `[]` makes it a dead end
only if no other action is enabled — in wait phases `DelayFires`/env remain, so the
wake sits set while delay exits: exactly the L1 violation. If `enabled`'s guards
prevent the violating path, adjust the mutant to drop the wake bit renewal instead —
but first re-check `enabled` against the code's WaitAsync, because the code CAN exit
by delay with the latch completed-but-unconsumed only if the completed-check is
skipped; the mutant must model precisely that skip.

- [ ] **Step 5: Run everything**

Run: `dotnet test tests/Lattice.Verification -c Release`
Expected: ALL PASS. If L2 fails on the healthy model with a trace ending in a tick
cycle where `configChanged=true` persists: the model's MsgGuard/SnapGuard/PollWait
routing is wrong (guards must route to Teardown) — fix Model.fs, never the property.

- [ ] **Step 6: Commit**

```bash
git add tests/Lattice.Verification
git commit -m "feat(verify): bottom-SCC liveness checking + L1-L3 + liveness mutants"
```

---

### Task 4: Promela double-check model + model-check script

**Files:**
- Create: `verification/HostMonitor.pml`
- Create: `scripts/model-check.sh` (mark executable)

**Interfaces:**
- Consumes: nothing from other tasks (independent encoding of the same spec).
- Produces: `scripts/model-check.sh` exit 0 = all properties hold; Task 5's CI job runs exactly this script. Property names `I1..I5`, `L1..L3` appear verbatim in comments and ltl names for the README cross-ref table (Task 11).

**Environment note (Windows dev machine):** spin/gcc are likely absent locally; write both files, validate syntax-only if spin is unavailable (`spin -a` requires spin). The authoritative runs happen in CI (Task 5). If you want local runs: MSYS2 `pacman -S spin gcc` — do NOT block this task on local tooling.

- [ ] **Step 1: Write verification/HostMonitor.pml**

Same protocol, same bounds, independent encoding. Complete content:

```promela
/* HostMonitor concurrency protocol — Promela double-check model.
 * Independent second encoding of the F# executable spec (tests/Lattice.Verification).
 * Property numbering shared: I1–I5 safety (assertions), L1–L3 liveness (ltl, pan -a -f).
 * Primitive anchoring: lock(_gate) blocks = atomic{}; awaits = statement boundaries;
 * TCS wake = sticky bit with consume-if-completed protocol; CTS = monotonic bits.
 */

#define MAX_UPDATES 2
#define MAX_WAKES   2
#define MAX_FAILS   3
#define ATT_CAP     3

/* phases (loop program counter between interleaving points) */
mtype:phase = { Idle, Dispatch, Snap, Conn, Auth, Fetch, Accept, PubConn,
                Tick, MsgG, MsgP, SnapG, SnapP, PWait, Tear, Retry, BWait, Park, Exited };
/* status observable (mirror of HostConnectionState) */
mtype:st = { Disc, Cing, Aing, Fing, Cted, Rtry, AFail };

mtype:phase ph = Idle;
mtype:st status = Disc;

byte curVersion = 0;        /* env increments */
bool configChanged = false;
byte ctsState = 0;          /* 0 none, 1 live, 2 canceled, 3 disposed */
bool wake = false;
bool started = false;
bool outerCanceled = false;
bool disposeFlag = false;
bool faulted = false;

byte attemptVersion = 255;  /* 255 = none */
bool connLive = false;
bool reachedConnected = false;
bool firstTickPending = false;
bool injectedFail = false;
bool authRefused = false;
byte attempt = 0;

byte statusVersion = 0;
byte daemonVerVintage = 255;   /* 255 = none */
byte logVintage = 255;

/* L1 history */
bool wakeSetDuringWait = false;
bool waitExitedByDelay = false;

byte updatesLeft = MAX_UPDATES;
byte wakesLeft = MAX_WAKES;
byte failsLeft = MAX_FAILS;

#define IN_WAIT (ph == PWait || ph == BWait || ph == Park)

/* I-invariants as a monitor process: checked at every state via timeout-free
 * always-enabled assertion stepping is expensive; instead assert inline at the
 * mutation/publish sites (I1, I4, I5) and via this monitor for I2/I3. */
active proctype monitor()
{
end_mon:
    do
    :: d_step {
         /* I2: no live connection while parked/backing off/exited */
         assert(!((ph == BWait || ph == Park || ph == Retry || ph == Exited) && connLive));
         /* I3: no unabortable stale attempt */
         assert(!( attemptVersion != 255 && attemptVersion != curVersion
                && !configChanged && ctsState != 2
                && (ph == Conn || ph == Auth || ph == Fetch || ph == Accept
                    || ph == PubConn || ph == Tick || ph == MsgG || ph == MsgP
                    || ph == SnapG || ph == SnapP || ph == PWait) ));
         /* I5: no reachable fault */
         assert(!faulted);
       }
    od
}

active proctype env()
{
end_env:
    do
    :: (updatesLeft > 0) -> atomic {
         updatesLeft--;
         curVersion++;
         configChanged = true;
         if :: ctsState == 1 -> ctsState = 2 :: else -> skip fi;
         wake = true;
         if :: IN_WAIT -> wakeSetDuringWait = true :: else -> skip fi
       }
    :: (wakesLeft > 0) -> atomic {
         wakesLeft--;
         wake = true;
         if :: IN_WAIT -> wakeSetDuringWait = true :: else -> skip fi
       }
    :: (!started) -> atomic {          /* Start (rider C: no-op after dispose) */
         if
         :: disposeFlag -> skip
         :: else -> started = true;
                    if :: ph == Idle -> ph = Dispatch :: else -> skip fi
         fi
       }
    :: (!disposeFlag) -> atomic {      /* DisposeAsync (idempotent via disposeFlag) */
         disposeFlag = true;
         outerCanceled = true;
         wake = true;
         if :: ctsState == 1 -> ctsState = 2 :: else -> skip fi
       }
    od
}

active proctype loop()
{
end_loop:
    do
    :: atomic { (ph == Dispatch && outerCanceled) ->
         ph = Exited; status = Disc }
    :: atomic { (ph == Dispatch && !outerCanceled) -> ph = Snap }
    :: atomic { (ph == Snap) ->              /* THE atomic snapshot block (rule 1) */
         attemptVersion = curVersion;
         configChanged = false;
         ctsState = 1;
         injectedFail = false; authRefused = false;
         reachedConnected = false; firstTickPending = true;
         daemonVerVintage = 255; logVintage = 255;   /* per-attempt stamps (I1m) */
         connLive = false;
         ph = Conn }
    /* awaits: cancel/dispose observed, or progress, or injected failure */
    :: atomic { (ph == Conn && (ctsState == 2 || outerCanceled)) -> ph = Tear }
    :: atomic { (ph == Conn && ctsState == 1 && !outerCanceled) ->
         connLive = true; status = Cing; statusVersion = attemptVersion; ph = Auth }
    :: atomic { (ph == Conn && ctsState == 1 && !outerCanceled && failsLeft > 0) ->
         failsLeft--; injectedFail = true; ph = Tear }
    :: atomic { (ph == Auth && (ctsState == 2 || outerCanceled)) -> ph = Tear }
    :: atomic { (ph == Auth && ctsState == 1 && !outerCanceled) ->
         status = Aing; statusVersion = attemptVersion; ph = Fetch }
    :: atomic { (ph == Auth && ctsState == 1 && !outerCanceled) ->
         authRefused = true; ph = Tear }              /* refused password path */
    :: atomic { (ph == Auth && ctsState == 1 && !outerCanceled && failsLeft > 0) ->
         failsLeft--; injectedFail = true; ph = Tear }
    :: atomic { (ph == Fetch && (ctsState == 2 || outerCanceled)) -> ph = Tear }
    :: atomic { (ph == Fetch && ctsState == 1 && !outerCanceled) ->
         status = Fing; statusVersion = attemptVersion; ph = Accept }
         /* NOTE deliberately NO daemonVerVintage/logVintage write here (riders A/B):
          * mutating either at Fetch is the historical bug; I1 assertions below
          * would fire. */
    :: atomic { (ph == Fetch && ctsState == 1 && !outerCanceled && failsLeft > 0) ->
         failsLeft--; injectedFail = true; ph = Tear }
    :: atomic { (ph == Accept) ->
         if
         :: configChanged -> ph = Tear
         :: else -> ph = PubConn
         fi }
    :: atomic { (ph == PubConn) ->
         /* I1 publish half: guard adjacency — assert guard was honored */
         daemonVerVintage = attemptVersion;           /* rider A: accepted only */
         reachedConnected = true;   /* NO attempt reset here: dispatcher owns the
                                     * counter (HostMonitor.cs RunAsync: ReachedConnected ? 1 : n+1) */
         status = Cted; statusVersion = attemptVersion;
         ph = Tick }
    :: atomic { (ph == Tick && (ctsState == 2 || outerCanceled)) -> ph = Tear }
    :: atomic { (ph == Tick && ctsState == 1 && !outerCanceled) -> ph = MsgG }
    :: atomic { (ph == Tick && ctsState == 1 && !outerCanceled && failsLeft > 0) ->
         failsLeft--; injectedFail = true; ph = Tear }
    :: atomic { (ph == MsgG) ->
         if :: configChanged -> ph = Tear :: else -> ph = MsgP fi }
    :: atomic { (ph == MsgP) ->
         /* rider B: first tick replaces, later ticks append; either way accepted-only */
         assert(reachedConnected);                    /* I1 mutation half */
         logVintage = attemptVersion;
         firstTickPending = false;
         ph = SnapG }
    :: atomic { (ph == SnapG) ->
         if :: configChanged -> ph = Tear :: else -> ph = SnapP fi }
    :: atomic { (ph == SnapP) -> ph = PWait }
    /* waits: delay fires, or sticky wake consumed */
    :: atomic { (ph == PWait) ->                      /* delay fires */
         if :: wake -> waitExitedByDelay = true :: else -> skip fi;
         if :: (configChanged || outerCanceled) -> ph = Tear :: else -> ph = Tick fi }
    :: atomic { (ph == PWait && wake) ->              /* wake consumed */
         wake = false;
         if :: (configChanged || outerCanceled) -> ph = Tear :: else -> ph = Tick fi }
    :: atomic { (ph == Tear) ->
         ctsState = 0; connLive = false;
         if
         :: outerCanceled -> ph = Exited; status = Disc
         :: (!outerCanceled && authRefused) ->
              status = AFail; statusVersion = attemptVersion; attempt = 0; ph = Park
         :: (!outerCanceled && !authRefused && (configChanged || !injectedFail)) ->
              attempt = 0; ph = Dispatch
         :: (!outerCanceled && !authRefused && !configChanged && injectedFail) ->
              if
              :: reachedConnected -> attempt = 1     /* I4 */
              :: else -> if :: attempt < ATT_CAP -> attempt++ :: else -> skip fi
              fi;
              ph = Retry
         fi }
    :: atomic { (ph == Retry) ->
         assert(!(reachedConnected && injectedFail) || attempt == 1);   /* I4 */
         status = Rtry; statusVersion = attemptVersion; ph = BWait }
    :: atomic { (ph == BWait) ->                      /* delay fires */
         if :: wake -> waitExitedByDelay = true :: else -> skip fi;
         if
         :: outerCanceled -> ph = Exited; status = Disc
         :: else -> if :: configChanged -> attempt = 0 :: else -> skip fi; ph = Dispatch
         fi }
    :: atomic { (ph == BWait && wake) ->
         wake = false;
         if
         :: outerCanceled -> ph = Exited; status = Disc
         :: else -> if :: configChanged -> attempt = 0 :: else -> skip fi; ph = Dispatch
         fi }
    :: atomic { (ph == Park && wake && !configChanged && !outerCanceled) ->
         wake = false }                               /* stale wakes ignored */
    :: atomic { (ph == Park && (configChanged || outerCanceled)) ->
         if
         :: outerCanceled -> ph = Exited; status = Disc
         :: else -> attempt = 0; ph = Dispatch
         fi }
    od
}

/* L1 lost wakeup — safety via history (checked as invariant): */
ltl L1 { [] !(wakeSetDuringWait && waitExitedByDelay) }
/* L2 config convergence */
ltl L2 { [] (configChanged -> <> (!configChanged || ph == Exited)) }
/* L3 disposal terminates */
ltl L3 { [] (outerCanceled -> <> (ph == Exited)) }
```

- [ ] **Step 2: Write scripts/model-check.sh**

```bash
#!/usr/bin/env bash
# HostMonitor Promela double-check: safety (assertions) + one pan run per LTL property.
# CI entry point (see .github/workflows/ci.yml model-check job). Requires: spin, gcc.
set -euo pipefail
cd "$(dirname "$0")/../verification"

command -v spin >/dev/null || { echo "spin not found"; exit 2; }
command -v gcc  >/dev/null || { echo "gcc not found";  exit 2; }
echo "spin: $(spin -V)"
echo "gcc:  $(gcc --version | head -1)"

run_pan () {
  local desc="$1"; shift
  echo "=== $desc ==="
  gcc -O2 -o pan pan.c
  ./pan "$@" | tee pan.out
  # pan reports violations via 'errors: N' — fail on any nonzero error count,
  # and on unreached-assertion noise stay quiet (exit code alone is not enough:
  # pan exits 0 even when errors are found).
  grep -q "errors: 0" pan.out || { echo "MODEL CHECK FAILED: $desc"; exit 1; }
  rm -f pan pan.out
}

# Safety: assertions + invalid end states (monitor/env are intentional end states —
# they carry end labels, so -q stays sound).
spin -a HostMonitor.pml
run_pan "safety (assertions, I1..I5)" -m100000

# Liveness: one exhaustive run per LTL property, weak fairness.
for P in L1 L2 L3; do
  spin -a -N "$P" HostMonitor.pml
  run_pan "liveness $P (acceptance, weak fairness)" -a -f -m100000
done

echo "ALL MODEL CHECKS PASSED"
```

`chmod +x scripts/model-check.sh` (and `git update-index --chmod=+x scripts/model-check.sh` so the bit survives on Windows).

- [ ] **Step 3: Red-first (CI-side if no local spin)**

Two deliberate breaks, run the script (locally if spin available, otherwise as a
temporary CI commit on the PR branch that Task 5's job runs — push, observe red,
revert; keep the evidence links in the task report):

1. Re-introduce the round-2 bug: in the `ph == Fetch` progress transition add
   `logVintage = attemptVersion;` → the `MsgP` assertion `assert(reachedConnected)`
   does NOT fire for it, but I1's mutation invariant is the F# layer's job; in
   Promela the break must trip the `monitor` I3 or the MsgP assert — instead
   re-introduce it as: move `assert(reachedConnected)` deletion + add eager write;
   the FAIR check: L2 stays green but safety must catch the eager write via a new
   inline assert `assert(logVintage == 255 || reachedConnected || logVintage != attemptVersion)`
   placed in the monitor — simpler and equivalent: add to `monitor()`'s d_step:
   `assert(!(logVintage == attemptVersion && !reachedConnected && attemptVersion != 255));`
   (add this assert as PERMANENT model content now — it is I1's mutation half for
   the log, mirroring the F# property; the break then trips it).
2. Break the snapshot atomicity (round 7): split `ph == Snap` into two atomic blocks
   — `attemptVersion = curVersion` in the FIRST, `configChanged = false; ctsState = 1`
   (and the rest) in the SECOND. An env update landing between them is silently
   erased: stale attempt, no pending flag, live CTS → monitor's I3 assert must fire.
   (Do NOT split as "flag clear first, version read second" — that shape re-reads
   the fresh version and is a semantic no-op; F# mutant M1 encodes the same break.)

Expected: both breaks RED (assertion violated in pan output), then restore, expected
GREEN. If a break stays green, the corresponding assertion is too weak — fix the
model's assertions (and mirror the fix into the F# property if the gap exists there
too), do not proceed on green-by-weakness.

- [ ] **Step 4: Commit**

```bash
git add verification/HostMonitor.pml scripts/model-check.sh
git commit -m "feat(verify): Promela double-check model + spin model-check script"
```

---

### Task 5: CI model-check job + required check

**Files:**
- Modify: `.github/workflows/ci.yml`
- Ruleset `protect-main` (id 18505416) via `gh api` (standing authorization from PR #7).

**Interfaces:**
- Consumes: `scripts/model-check.sh` (Task 4).
- Produces: required check context `model-check` on PRs to main. The F# project needs no CI change — `dotnet test` on the solution already picks it up in `build-test`.

- [ ] **Step 1: Add the job to ci.yml**

Append to `jobs:` in `.github/workflows/ci.yml` (keep existing `build-test` untouched):

```yaml
  model-check:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - name: Install spin
        run: sudo apt-get update && sudo apt-get install -y spin gcc
      - name: Model check (Promela double-check)
        run: bash scripts/model-check.sh
```

- [ ] **Step 2: Push, verify the job runs green on PR #8**

Run: `git add .github/workflows/ci.yml && git commit -m "ci: add Promela model-check job" && git push`
Then: `gh pr checks 8 --watch` — Expected: `model-check` green alongside the three `build-test` legs.

- [ ] **Step 3: Add to protect-main required checks**

Read current ruleset, append the new context, PUT it back (same flow as the PR #7 matrix change):

```bash
gh api repos/0x8A63F77D/Lattice/rulesets/18505416 --jq '.rules[] | select(.type=="required_status_checks") | .parameters.required_status_checks'
# append {"context":"model-check"} to the existing three build-test contexts in the
# PUT payload; keep every other ruleset field identical to the GET response.
```

Verify: `gh api repos/0x8A63F77D/Lattice/rulesets/18505416 --jq '[.rules[] | select(.type=="required_status_checks") | .parameters.required_status_checks[].context]'`
Expected: three `build-test (...)` + `model-check`.

- [ ] **Step 4: Commit ledger note**

Update `.superpowers/sdd/progress.md` (Tasks 1–5 done, CI gate live) and commit with the ci change if not already pushed.

---

### Task 6: Probe seam in HostMonitor

**Files:**
- Modify: `src/Lattice.Core/HostMonitor.cs`
- Create: `tests/Lattice.Tests/Interleaving/ProbeController.cs`

**Interfaces:**
- Produces: `internal static class InterleavePoints` (string constants + `internal static readonly string[] All`); `internal Func<string, Task>? InterleaveProbe` on `HostMonitor`; `ProbeController` test helper: `Task WaitForAsync(string point)` (completes when the loop reaches the frozen point), `void Release()` (releases the current freeze), `void FreezeAt(string point)` (arm one point), `void Disarm()`. Tasks 7–10 depend on these exact names.
- Behavior contract: NO probe point inside a `lock` block; points only at boundaries where environment threads already interleave in production. Zero behavior change when `InterleaveProbe` is null.

**This task is pure seam: existing 139 tests must stay green; no new semantics.**
Per the verification sync rule this commit does not change protocol semantics — say
so in the commit message.

- [ ] **Step 1: Add the points and the seam to HostMonitor.cs**

Add file-scope (same file, below the event args record):

```csharp
/// <summary>
/// Named interleaving points of the actor loop, in loop order. Test-only seam:
/// production leaves <see cref="HostMonitor.InterleaveProbe"/> null. Placement rules
/// (verification/README.md): never inside a lock block; only where environment
/// threads can already interleave in production. Every new shared-state touch point
/// added to HostMonitor MUST add a point here (correspondence rule 3).
/// </summary>
internal static class InterleavePoints
{
    public const string BeforeSnapshot = "attempt.beforeSnapshot";
    public const string AfterSnapshot = "attempt.afterSnapshot";
    public const string BeforeAcceptGuard = "attempt.beforeAcceptGuard";
    public const string BeforeConnectedPublish = "attempt.beforeConnectedPublish";
    public const string TickBeforeMsgGuard = "tick.beforeMsgGuard";
    public const string TickBeforeMsgPublish = "tick.beforeMsgPublish";
    public const string TickBeforeSnapGuard = "tick.beforeSnapGuard";
    public const string TickBeforeBuild = "tick.beforeBuild";
    public const string TickBeforeSnapPublish = "tick.beforeSnapPublish";
    public const string PollBeforeWait = "poll.beforeWait";
    public const string PollAfterWait = "poll.afterWait";
    public const string FinallyEnter = "attempt.finallyEnter";
    public const string AfterCtsDispose = "attempt.afterCtsDispose";
    public const string BeforeRetryPublish = "dispatcher.beforeRetryPublish";
    public const string BeforeParkWait = "dispatcher.beforeParkWait";

    public static readonly string[] All =
    [
        BeforeSnapshot, AfterSnapshot, BeforeAcceptGuard, BeforeConnectedPublish,
        TickBeforeMsgGuard, TickBeforeMsgPublish, TickBeforeSnapGuard, TickBeforeBuild,
        TickBeforeSnapPublish, PollBeforeWait, PollAfterWait, FinallyEnter,
        AfterCtsDispose, BeforeRetryPublish, BeforeParkWait,
    ];
}
```

In `HostMonitor`:

```csharp
    // Test-only interleaving seam (see InterleavePoints). Null in production: the
    // probe call is a single null check on the loop's paths. Set via
    // InternalsVisibleTo by the sweep harness ONLY before Start().
    internal Func<string, Task>? InterleaveProbe;

    private Task ProbeAsync(string point) =>
        InterleaveProbe?.Invoke(point) ?? Task.CompletedTask;
```

Then thread the awaits at exactly these code locations (all OUTSIDE lock blocks):

- `RunAttemptAsync`: `await ProbeAsync(InterleavePoints.BeforeSnapshot)` as the first
  statement; `AfterSnapshot` immediately after the snapshot lock block;
  `BeforeAcceptGuard` right before the pre-Connected `if (_configChanged)`;
  `BeforeConnectedPublish` right after that guard (before `SetStatus(Connected, 0)`);
  in the `finally`: `FinallyEnter` as its first statement (BEFORE the CTS lock
  block; the probe is outside the lock), `AfterCtsDispose` after the CTS lock block
  (before client dispose).
- `PollAsync`: `PollBeforeWait` before `WaitAsync(...)`, `PollAfterWait` after it.
- `TickAsync`: `TickBeforeMsgGuard` before the first `ct.ThrowIfCancellationRequested()`
  message-guard pair; `TickBeforeMsgPublish` after that guard (before the
  `newMessages.Count > 0` block); `TickBeforeSnapGuard` before the second guard pair;
  `TickBeforeBuild` after it (before `SnapshotBuilder.Build`); `TickBeforeSnapPublish`
  after the post-Build recheck (before `Snapshot = snapshot`).
- `RunAsync`: `BeforeRetryPublish` before the `SetStatus(HostConnectionState.Retrying, ...)`;
  `BeforeParkWait` after the AuthFailed `SetStatus` (before `WaitForConfigChangeAsync`).

The `finally` gets `await` statements — it is already an async method's finally, and
the existing `client.DisposeAsync()` await proves that's fine.

- [ ] **Step 2: Write ProbeController**

`tests/Lattice.Tests/Interleaving/ProbeController.cs`:

```csharp
using Lattice.Core;

namespace Lattice.Tests.Interleaving;

/// <summary>
/// Deterministic freeze/release controller for HostMonitor's probe seam. Arm a
/// point with FreezeAt; the loop blocks there; the test observes via WaitForAsync,
/// performs an environment action, then Release()s. Unarmed points pass through.
/// </summary>
internal sealed class ProbeController
{
    private readonly object _sync = new();
    private string? _armed;
    private TaskCompletionSource _reached = NewTcs();
    private TaskCompletionSource _release = NewTcs();

    private static TaskCompletionSource NewTcs() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Probe(string point)
    {
        lock (_sync)
        {
            if (_armed != point)
                return Task.CompletedTask;
            _armed = null;                 // one-shot
            _reached.TrySetResult();
            return _release.Task;
        }
    }

    public void FreezeAt(string point)
    {
        lock (_sync)
        {
            _armed = point;
            _reached = NewTcs();
            _release = NewTcs();
        }
    }

    public Task WaitForAsync(string point) => _reached.Task.WaitAsync(TimeSpan.FromSeconds(10));

    public void Release()
    {
        lock (_sync)
            _release.TrySetResult();
    }

    public void Disarm()
    {
        lock (_sync)
        {
            _armed = null;
            _release.TrySetResult();
        }
    }
}
```

(`WaitForAsync`'s `point` parameter is for call-site readability; the controller is
one-shot single-point so the TCS is unambiguous.)

- [ ] **Step 3: Smoke test the seam**

Add to `tests/Lattice.Tests/Interleaving/SweepTests.cs` (file created now, matrix
filled in Task 10) one test: construct a monitor with `FakeGuiRpcClient` scripted for
a clean connect (reuse the patterns from `HostMonitorStateMachineTests`), set
`monitor.InterleaveProbe = controller.Probe`, `FreezeAt(InterleavePoints.BeforeAcceptGuard)`,
`Start()`, `await controller.WaitForAsync(...)`, assert `monitor.Status.State` is
`FetchingState` (frozen pre-accept), `Release()`, then wait (existing `Wait` helper)
for `Connected`.

Run: `dotnet test -c Release` (full suite)
Expected: new test PASS, all 139 existing tests PASS (seam is inert when null).

- [ ] **Step 4: Commit**

```bash
git add src/Lattice.Core/HostMonitor.cs tests/Lattice.Tests/Interleaving
git commit -m "feat(verify): interleaving probe seam + controller (no semantic change; model sync not required)"
```

---

### Task 7: Rider A — `_daemonVersion` attempt-local

**Files:**
- Modify: `src/Lattice.Core/HostMonitor.cs` (`RunAttemptAsync`)
- Test: `tests/Lattice.Tests/Interleaving/SweepTests.cs`

**Interfaces:** none new. Model sync: F#/Promela already encode the TARGET (write at
PublishConnected) — no model change; commit message says so.

- [ ] **Step 1: Failing test**

```csharp
[Fact]
public async Task FailedAttemptDoesNotPollute_DaemonVersion()
{
    // First connection succeeds with version 8.0; then the connection breaks and
    // the reconnect attempt reaches exchange_versions returning 9.9 but FAILS at
    // get_state. The Retrying status must still carry 8.0 (last ACCEPTED version),
    // not 9.9 (unaccepted attempt's version).
    // Script FakeGuiRpcClient: connect#1 ok (ExchangeVersions -> 8.0), first tick ok;
    // then fail the next GetResults to force Retrying; connect#2: ExchangeVersions
    // -> 9.9, GetState throws. Assert on the SECOND Retrying status event:
    // status.DaemonVersion is 8.0.
}
```

Write it fully with the fake's scriptable hooks (same style as
`HostMonitorStateMachineTests`' reconnect tests — collect `StatusChanged` events into
a list, use `Wait.Until` helpers). Run:
`dotnet test -c Release --filter FailedAttemptDoesNotPollute_DaemonVersion`
Expected: FAIL — current code stamps 9.9 from the failed attempt.

- [ ] **Step 2: Fix**

In `RunAttemptAsync`, change:

```csharp
            SetStatus(HostConnectionState.FetchingState, attempt);
            _daemonVersion = await client.ExchangeVersionsAsync(connCt).ConfigureAwait(false);
```

to:

```csharp
            SetStatus(HostConnectionState.FetchingState, attempt);
            // Attempt-local until accepted: a failed attempt must not pollute
            // Status publishes with an unaccepted daemon version (I1 mutation half;
            // PR #7 round-9 P2).
            VersionInfo daemonVersion = await client.ExchangeVersionsAsync(connCt).ConfigureAwait(false);
```

and immediately after the accept guard (before `SetStatus(HostConnectionState.Connected, 0)`):

```csharp
            _daemonVersion = daemonVersion;
```

- [ ] **Step 3: Run — test passes, full suite green**

`dotnet test -c Release` — Expected: ALL PASS.

- [ ] **Step 4: Commit**

```bash
git add src/Lattice.Core/HostMonitor.cs tests/Lattice.Tests/Interleaving/SweepTests.cs
git commit -m "fix(core): daemon version stays attempt-local until Connected (I1; model already encodes target)"
```

---

### Task 8: Rider B — message-log structural fix (ReplaceAll on first tick)

**Files:**
- Modify: `src/Lattice.Core/MessageLog.cs` (+`ReplaceAll`)
- Modify: `src/Lattice.Core/HostMonitor.cs` (delete `_lastSeqno` field; PollAsync/TickAsync threading; doc comments)
- Test: `tests/Lattice.Tests/Interleaving/SweepTests.cs`, plus update any existing tests that assert the old clear-on-reconnect behavior (`HostMonitorPollingTests` — read them; reconnect message tests will need their expectations updated to retention semantics).

**Interfaces:**
- Produces: `internal void ReplaceAll(IReadOnlyList<Message> items)` on `MessageLog`.
- **Deliberate behavior change (flag in PR body):** `Messages` now retains old
  content until the new connection's first tick (was: cleared at connection start).
  `MessagesAdded` semantics unchanged: first tick raises the full refetched batch.

- [ ] **Step 1: Failing tests (two)**

```csharp
[Fact]
public async Task AbortedAttemptNeverDestroysMessageLog()
{
    // Connect #1, tick once -> log has messages. UpdateConfig to a host whose
    // connect BLOCKS until canceled (scripted). Freeze the RECONNECT attempt at
    // InterleavePoints.BeforeAcceptGuard (i.e. after get_state, pre-accept),
    // UpdateConfig AGAIN mid-attempt, Release -> attempt aborts.
    // Assert: monitor.Messages still contains connection #1's messages THROUGHOUT
    // (poll Messages after each stage) — never empty, never cleared.
}

[Fact]
public async Task FirstTickReplacesLogAtomically()
{
    // Connect #1 with messages A,B (seqno 1,2). Reconnect (break the connection);
    // connection #2's daemon restarted: GetMessages(0) returns C (seqno 1).
    // After reconnect's first tick: Messages == [C] exactly (replaced, not appended),
    // and the MessagesAdded event carried [C].
    // Also: before that first tick completes, Messages still == [A, B] (retention).
}
```

Write both fully against the fake's hooks. Run: expected FAIL — current code clears
eagerly (first test sees an empty log after the aborted attempt) and appends after
reset rather than replacing… (second test's retention assertion fails: log is empty
during reconnect).

- [ ] **Step 2: MessageLog.ReplaceAll**

Read `src/Lattice.Core/MessageLog.cs` (ring buffer, internal lock, cap ctor param,
`Append`/`Snapshot`/`Clear`). Add, following its existing lock discipline exactly:

```csharp
    /// <summary>
    /// Atomically replaces the entire retained buffer with <paramref name="items"/>
    /// (capped to capacity, keeping the newest). Readers see either the old content
    /// or the new — never an intermediate empty state.
    /// </summary>
    internal void ReplaceAll(IReadOnlyList<Message> items)
    {
        lock (_lock)
        {
            _items.Clear();
            int skip = Math.Max(0, items.Count - _capacity);
            for (int i = skip; i < items.Count; i++)
                _items.Add(items[i]);
        }
    }
```

(Adapt member names to the actual field names in the file — keep semantics: clear +
refill inside ONE lock hold.)

- [ ] **Step 3: HostMonitor changes**

1. Delete the `private int _lastSeqno;` field.
2. In `RunAttemptAsync`, DELETE the whole block:
   `_lastSeqno = 0; _messages.Clear();` and its comment (the seqno-reset comment
   moves to PollAsync, rewritten below).
3. `PollAsync` gains per-connection cursor state and passes it through:

```csharp
    private async Task PollAsync(IGuiRpcClient client, HostConfig config, CcState state,
                                 CancellationToken connCt, CancellationToken ct)
    {
        bool reauthedSinceLastSuccess = false;
        // Per-connection message cursor. Starts at 0 for every new connection: a
        // freshly (re)started daemon's seqno counter may have reset, so the first
        // tick refetches the daemon's current buffer from scratch and REPLACES the
        // retained log (old messages stay visible until that instant — same
        // keep-last-known retention doctrine as Snapshot). Field-free by design:
        // per-connection state must not outlive the connection (I1 mutation half).
        int lastSeqno = 0;
        bool firstTick = true;
        while (true)
        {
            try
            {
                (state, lastSeqno) = await TickAsync(client, config, state, lastSeqno,
                                                     replaceLog: firstTick, connCt).ConfigureAwait(false);
                firstTick = false;
                reauthedSinceLastSuccess = false;
            }
            ...
```

(rest of PollAsync unchanged). `TickAsync` signature and message section become:

```csharp
    private async Task<(CcState State, int LastSeqno)> TickAsync(
        IGuiRpcClient client, HostConfig config, CcState state,
        int lastSeqno, bool replaceLog, CancellationToken ct)
```

and the message block:

```csharp
        IReadOnlyList<Message> newMessages = await client.GetMessagesAsync(lastSeqno, ct).ConfigureAwait(false);

        await ProbeAsync(InterleavePoints.TickBeforeMsgGuard).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (_configChanged)
            return (state, lastSeqno);
        await ProbeAsync(InterleavePoints.TickBeforeMsgPublish).ConfigureAwait(false);
        if (newMessages.Count > 0)
            lastSeqno = newMessages.Max(m => m.Seqno);
        if (replaceLog)
        {
            // First tick of this connection: atomically swap old-connection content
            // for the daemon's current buffer (may be empty — a genuinely empty
            // daemon buffer replaces the log with empty).
            _messages.ReplaceAll(newMessages);
            if (newMessages.Count > 0)
                RaiseSafe(MessagesAdded, new MessagesAddedEventArgs(HostId, newMessages));
        }
        else if (newMessages.Count > 0)
        {
            _messages.Append(newMessages);
            RaiseSafe(MessagesAdded, new MessagesAddedEventArgs(HostId, newMessages));
        }
```

All `return state;` statements in TickAsync become `return (state, lastSeqno);`.
4. Update the `Messages` and `MessagesAdded` XML doc comments: retention until first
   tick replaces (mirror the PollAsync comment above).

- [ ] **Step 4: Reconcile existing tests, run everything**

`grep -n "Clear\|_lastSeqno\|seqno" tests/Lattice.Tests -r` — update reconnect-message
tests in `HostMonitorPollingTests` whose expectations encode clear-at-connect (the
retention semantics change what `Messages` shows mid-reconnect; the POST-first-tick
expectations are unchanged). Run: `dotnet test -c Release` — Expected: ALL PASS
including both Step-1 tests.

- [ ] **Step 5: Commit (model sync note)**

```bash
git add src/Lattice.Core tests/Lattice.Tests
git commit -m "fix(core): message log replaced atomically on first tick; per-connection cursor field removed

Structural fix for the pre-accept log-clear defect (PR #8 rounds 2-4):
no user-visible mutation is expressible before Connected + first data.
Behavior change: Messages retains last-known content across reconnects
until the new connection's first tick (Snapshot's retention doctrine).
Model sync: F#/Promela already encode this target (MsgPublish writes
logVintage post-accept only); probe list unchanged."
```

---

### Task 9: Rider C — lifecycle hardening (A6)

**Files:**
- Modify: `src/Lattice.Core/HostMonitor.cs` (`Start`, `DisposeAsync`, new `_disposed` flag)
- Test: `tests/Lattice.Tests/Interleaving/LifecycleTests.cs` (create)

- [ ] **Step 1: Failing tests (A6, red-first — confirm the suspected defects are real)**

`tests/Lattice.Tests/Interleaving/LifecycleTests.cs`:

```csharp
using Lattice.Core;
using Lattice.Tests.Fakes;

namespace Lattice.Tests.Interleaving;

public class LifecycleTests
{
    // build a monitor the way HostMonitorStateMachineTests does (FakeGuiRpcClient
    // factory + FakeTimeProvider + 5s interval); reuse its helper if one exists.

    [Fact]
    public async Task DoubleDisposeDoesNotThrow()
    {
        var monitor = CreateMonitor(out _);
        monitor.Start();
        await monitor.DisposeAsync();
        await monitor.DisposeAsync();   // was: ObjectDisposedException from _cts.Cancel()
    }

    [Fact]
    public async Task DisposeWithoutStartThenStartIsInert()
    {
        var monitor = CreateMonitor(out _);
        await monitor.DisposeAsync();
        monitor.Start();                // was: loop task faults reading _cts.Token
        await Task.Delay(50);           // give a would-be faulted task time to surface
        Assert.Equal(HostConnectionState.Disconnected, monitor.Status.State);
        await monitor.DisposeAsync();   // still idempotent afterwards
    }

    [Fact]
    public async Task ConcurrentStartAndDisposeSettleDisconnected()
    {
        var monitor = CreateMonitor(out _);
        var start = Task.Run(monitor.Start);
        var dispose = Task.Run(async () => await monitor.DisposeAsync());
        await Task.WhenAll(start, dispose);
        await monitor.DisposeAsync();   // idempotent regardless of the race outcome
        Assert.Equal(HostConnectionState.Disconnected, monitor.Status.State);
    }
}
```

Run: `dotnet test -c Release --filter LifecycleTests`
Expected: `DoubleDisposeDoesNotThrow` FAILS with `ObjectDisposedException`;
`DisposeWithoutStartThenStartIsInert` FAILS (faulted task / ODE — observe and record
the actual failure mode in the task report; if a suspected defect does NOT reproduce,
STOP and re-examine before "fixing" — the spec's claims must be corrected if wrong).

- [ ] **Step 2: Fix (single `_gate` discipline)**

```csharp
    private bool _disposed;   // new field, guarded by _gate
```

`Start`:

```csharp
    public void Start()
    {
        lock (_gate)
        {
            if (_started || _disposed)
                return;
            _started = true;
            // Token captured under _gate: DisposeAsync sets _disposed under this
            // same lock BEFORE it ever cancels/disposes _cts, so a Start that got
            // here holds a token read strictly before any dispose (I5).
            CancellationToken token = _cts.Token;
            _loop = Task.Run(() => RunAsync(token), CancellationToken.None);
        }
    }
```

`DisposeAsync`:

```csharp
    public async ValueTask DisposeAsync()
    {
        bool first;
        Task loop;
        lock (_gate)
        {
            first = !_disposed;
            _disposed = true;
            loop = _loop;
        }
        if (first)
        {
            _cts.Cancel();
            Wake();
        }
        try { await loop.ConfigureAwait(false); }
        catch { /* the loop reports failures via Status, never by throwing */ }
        if (first)
            _cts.Dispose();
    }
```

(A second concurrent DisposeAsync awaits the same loop task and skips
Cancel/Dispose; a Start serialized after DisposeAsync's lock block sees `_disposed`
and is inert; a Start serialized before it publishes `_loop` inside the same lock,
so DisposeAsync's read picks up the real task. `Task.Run` no longer reads
`_cts.Token` inside the lambda.)

- [ ] **Step 3: Run — all green**

`dotnet test -c Release` — Expected: ALL PASS (three lifecycle tests + full suite).

- [ ] **Step 4: Commit**

```bash
git add src/Lattice.Core/HostMonitor.cs tests/Lattice.Tests/Interleaving/LifecycleTests.cs
git commit -m "fix(core): idempotent DisposeAsync; Start is inert after dispose (I5/A6; model already encodes target via disposeFlag)"
```

---

### Task 10: Sweep matrix (A1–A5) + reentrancy pin

**Files:**
- Modify: `tests/Lattice.Tests/Interleaving/SweepTests.cs`
- Create: `tests/Lattice.Tests/Interleaving/ReentrancyTests.cs`

**Interfaces:** consumes `InterleavePoints.All`, `ProbeController`, and the fake's
scriptable hooks. No production changes in this task (riders landed in 7–9); if any
sweep case goes red here, that is a REAL pre-existing bug — stop, root-cause, fix as
its own commit with the sweep case as the regression test (spec process step 2).

- [ ] **Step 1: The sweep scaffold (xUnit theory over point × action)**

```csharp
public enum EnvAction { UpdateConfig, Dispose, RequestRefresh }

public static IEnumerable<object[]> SweepCases() =>
    from point in InterleavePoints.All
    from action in new[] { EnvAction.UpdateConfig, EnvAction.Dispose, EnvAction.RequestRefresh }
    select new object[] { point, action };

[Theory]
[MemberData(nameof(SweepCases))]
public async Task SweepPointTimesAction(string point, EnvAction action)
{
    // Arrange: fake scripted for connect + one full tick with 2 messages; a SECOND
    // config (different port) whose connect blocks until canceled then succeeds
    // instantly on the new config's generation tag. Recording subscribers collect
    // (event, payload, configGeneration) tuples; the fake stamps each client with
    // the generation of the config it was built for (factory closure over
    // HostRegistry-style current config — reuse the pattern from
    // HostMonitorStateMachineTests' UpdateConfig tests).
    // Act: FreezeAt(point); Start(); await WaitForAsync(point) — points not on the
    // first pass (e.g. BeforeRetryPublish needs a failure) get their own scripted
    // preamble: see the routing table below. Perform `action`. Release().
    // Run to quiescence (Wait.Until on the expected terminal condition).
    // Assert the global invariants for this (point, action):
    //   A1  (UpdateConfig): no event with the OLD generation is raised after the
    //       point's guard, per the doctrine table below; message log intact at every
    //       pre-first-tick point; post-quiescence: log/status reflect the NEW config
    //       (self-healing arm).
    //   A2  (all actions): when a Retrying/AuthFailed/Disconnected status arrives,
    //       the old client's DisposeAsync has completed (fake records it).
    //   A3  (UpdateConfig): monitor reaches Connecting on the NEW generation within
    //       bounded fake time (cancel reached the CTS / flag observed).
    //   A5  (Dispose): loop task completes non-faulted (await monitor.DisposeAsync()
    //       returns; internal _loop.IsFaulted is false via InternalsVisibleTo),
    //       final status Disconnected, client disposed.
    //   RequestRefresh: no stale publish possible (no config change) — assert
    //       instead: loop makes progress (next tick happens) and nothing faults.
}
```

Doctrine table (drives the per-point A1 expectation — encode as a switch over the
point constant):

| Frozen point | UpdateConfig lands ⇒ |
|---|---|
| BeforeSnapshot / AfterSnapshot | new config picked up by this or next attempt; no old-generation publish at all |
| BeforeAcceptGuard | NO Connected publish for old generation; log intact |
| BeforeConnectedPublish | Connected(old gen) MAY be published (benign in-gap; guard already passed) — then teardown + reconnect; assert convergence |
| TickBeforeMsgGuard / TickBeforeSnapGuard | no message/snapshot publish (guards catch) |
| TickBeforeMsgPublish / TickBeforeBuild / TickBeforeSnapPublish | publish MAY occur with old gen (in-gap) — assert post-quiescence convergence (self-healing: new-gen connect + first tick replace) |
| PollBeforeWait / PollAfterWait | PollAsync returns via flag; reconnect on new gen |
| FinallyEnter / AfterCtsDispose | teardown proceeds; reconnect on new gen |
| BeforeRetryPublish / BeforeParkWait | Retrying/AuthFailed publish carries the OLD attempt's vintage (correct: it describes that attempt); reconnect resets attempt=0 on release |

Points needing a preamble script: `BeforeRetryPublish` (script one failing connect
first), `BeforeParkWait` (script a refused password). Fold the preamble into the
Arrange via a small `ScriptFor(point)` local function so the Theory body stays one
path.

- [ ] **Step 2: Implement, run the matrix**

Run: `dotnet test -c Release --filter SweepPointTimesAction`
Expected: 45 cases PASS. Debugging reds: a red here after Tasks 7–9 means either a
sweep-expectation transcription error (check the doctrine table against the spec's
A1 arms) or a REAL bug — distinguish by reading which assertion fired; for real bugs
follow the stop-root-cause-fix-own-commit rule.

- [ ] **Step 3: Reentrancy pin**

`tests/Lattice.Tests/Interleaving/ReentrancyTests.cs`:

```csharp
[Fact]
public async Task ReentrantUpdateConfigFromStatusChangedIsSafe()
{
    // Subscriber calls monitor.UpdateConfig(newConfig) synchronously from INSIDE the
    // Connected StatusChanged dispatch (one-shot flag so it fires once). Assert:
    // no deadlock (test completes), no fault, monitor converges to Connected on the
    // new generation, and no old-generation snapshot/message publish occurs after
    // the reentrant call returns (same A1 doctrine as an in-gap env action at
    // BeforeConnectedPublish's successor point).
}

[Fact]
public async Task ReentrantRequestRefreshFromSnapshotUpdatedIsSafe()
{
    // Subscriber calls RequestRefresh from SnapshotUpdated (one-shot). Assert: the
    // wake is consumed by the next wait (an extra tick happens promptly under fake
    // time), loop keeps running, nothing faults.
}
```

Run: `dotnet test -c Release --filter Reentrancy` — Expected: PASS (contract holds
on current code; these are pins, not red-first).

- [ ] **Step 4: Commit**

```bash
git add tests/Lattice.Tests/Interleaving
git commit -m "test(verify): interleaving sweep matrix (A1-A5) + reentrancy pins"
```

---

### Task 11: verification/README.md + ledger + PR body

**Files:**
- Create: `verification/README.md`
- Modify: `.superpowers/sdd/progress.md`, PR #8 body (via `gh pr edit`)

- [ ] **Step 1: Write verification/README.md**

Sections (content sources in parentheses — transcribe, do not re-derive):

1. **What is verified where** — the three-artifact table from the spec's
   Architecture section + the property cross-reference table:

   | Property | F# (Properties.fs test name) | Promela (assert/ltl) | Harness (test) |
   |---|---|---|---|
   | I1 publish | `I1 guards bar publishes…` | Accept/MsgG/SnapG routing + MsgP assert | Sweep A1 arms |
   | I1 mutation | `I1 no pre-accept mutation…` | monitor d_step assert (logVintage) + MsgP assert | `AbortedAttemptNeverDestroysMessageLog` |
   | I2 | `I2 connection closed…` | monitor d_step assert | Sweep A2 |
   | I3 | `I3 no unabortable stale attempt` | monitor d_step assert | Sweep A3 |
   | I4 | `I4 attempt counter resets…` | Retry-phase assert | `FailedAttempt…` + sweep preamble case |
   | I5 | `I5 lifecycle safety…` | monitor assert(!faulted) | LifecycleTests (A6) |
   | L1 | `L1 no lost wakeup` | ltl L1 | (covered by design layer; harness indirectly via reentrancy/wake tests) |
   | L2 | `L2 config change converges` | ltl L2 | Sweep A3 convergence arm |
   | L3 | `L3 disposal terminates…` + `L3b…` | ltl L3 | Sweep A5 |

2. **Correspondence rules 1–3** (spec, verbatim) + the rule that a red/green
   DISAGREEMENT between F# and Promela encodings is itself a finding that blocks.
3. **Shared-state inventory** — one row per `HostMonitor` field. Transcribe every
   field from the final HostMonitor.cs of Task 9 with: protection regime
   (`_gate` / volatile / Volatile.R-W / loop-confined) and modeled-or-excluded with
   justification addressing VINTAGE/VISIBILITY, not just data races (spec
   correspondence rule 2 — include `_lastSeqno`'s deletion note: per-connection
   state became a local precisely so it cannot be a field-level hazard).
4. **Assumptions** (spec, verbatim): subscriber termination; reentrancy contract
   (in-contract reentrant calls / out-of-contract lifecycle blocking);
   SnapshotBuilder purity; client confinement; Status/Snapshot cross-field tearing.
5. **Memory-model note** (spec, verbatim SC justification).
6. **How to run**: `dotnet test tests/Lattice.Verification` (F#, any OS);
   `scripts/model-check.sh` (spin+gcc; ubuntu CI or MSYS2 locally).
7. **Verification sync rule** (CLAUDE.md, verbatim) + probe-point placement rules.

- [ ] **Step 2: Update ledger + PR**

`.superpowers/sdd/progress.md`: task-by-task summary, red-first evidence links
(commit SHAs where each expected-red was observed), state counts from the explorer,
pan run stats. PR #8 body: append an "Implementation" section — three-artifact
summary, the two behavior changes to flag (message retention semantics;
DisposeAsync idempotency), CI additions (`model-check` required).

- [ ] **Step 3: Full local gate, push, Codex round**

```bash
dotnet build -c Release -warnaserror && dotnet test -c Release
git add verification/README.md .superpowers/sdd/progress.md
git commit -m "docs(verify): verification README (correspondence, inventory, assumptions, cross-ref)"
git push
gh pr comment 8 --body "@codex review"
```

Verify 👀 ack (memory: wait-for-codex-review — filter your own reply-reviews when
polling; results may arrive as an issue comment). Triage per the standing severity
delegation; merge only after Codex + all four required checks green.

---

## Execution notes

- Tasks 1→2→3 are strictly sequential (same project); 4 is independent of 1–3;
  5 needs 4; 6 needs nothing but should follow 5 to keep CI green throughout;
  7/8/9 need 6 (they use sweep-style tests); 10 needs 7–9; 11 last.
- Subagent workers: give each task this plan section plus the spec file path; the
  spec is the tie-breaker on any ambiguity, and the property definitions in the spec
  override any transcription drift in this plan.
- The F# `Model.fs` in Task 1 is the single most review-worthy artifact of the PR:
  when in doubt about a transition, open `src/Lattice.Core/HostMonitor.cs` (post-
  rider TARGET is what the model encodes — riders land later; the model deliberately
  runs ahead of the code between Tasks 1 and 9, which is why harness reds in that
  window are EXPECTED and listed per-rider).
