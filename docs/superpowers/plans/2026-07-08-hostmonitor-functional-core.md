# HostMonitor Functional Core Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Promote the F# executable spec to the production decision core (`Lattice.Core.Machine`, pure `HostMachine.step`); reduce `HostMonitor.cs` to a C# interpreter shell; rewrite `tests/Lattice.Verification` to explore the production core.

**Architecture:** Command-interpreter split per spec `docs/superpowers/specs/2026-07-08-hostmonitor-functional-core-design.md`. The core is a total pure function `step : State -> Input -> State * Command list`; the shell executes commands (I/O, locks, events, waits), classifies exceptions into `Faulted` inputs, and never makes routing decisions. The verification wrapper models the shell primitives and drives the same `step`.

**Tech Stack:** .NET 10, F# (new `src/Lattice.Core.Machine`), C# (`Lattice.Core`), xUnit.

**Hard constraints (from spec §2 — a violation means STOP and report, not work around):**
- Zero observable semantic change. The untouched C# test suite (193 tests incl. the 45-case interleaving sweep, lifecycle, reentrancy) is the parity oracle. **No file under `tests/Lattice.Tests/` may be modified.**
- `HostMonitor`/`HostMonitorManager` public API frozen; test-visible internals frozen: `InterleavePoints` (names + positions), `InterleaveProbe`, `_loop`, `MessageCapacity`, `HostMonitor.BackoffDelay(int)`.
- Verification sync rule: Task 4 (HostMonitor.cs) and Task 5 (README/pml/CLAUDE.md) land in ONE commit.
- Every commit builds with `-warnaserror` and passes the full suite.

**Authoritative parity reference:** the pre-rewrite `src/Lattice.Core/HostMonitor.cs` at commit `d3950c2` (read it with `git show d3950c2:src/Lattice.Core/HostMonitor.cs` once Task 4 has begun rewriting the working copy).

---

## Task 1: Scaffold `Lattice.Core.Machine`, move `HostConnectionState`

**Files:**
- Create: `src/Lattice.Core.Machine/Lattice.Core.Machine.fsproj`
- Create: `src/Lattice.Core.Machine/HostConnectionState.fs`
- Modify: `src/Lattice.Core/ConnectionStatus.cs` (delete the enum, keep the record)
- Modify: `src/Lattice.Core/Lattice.Core.csproj` (add project reference)
- Modify: `Lattice.sln` (add project via `dotnet sln add`)

- [ ] **Step 1: Create the fsproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <RootNamespace>Lattice.Core</RootNamespace>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <!-- Warn on incomplete pattern matches as errors: the step function must be total -->
    <WarningsAsErrors>$(WarningsAsErrors);FS0025</WarningsAsErrors>
  </PropertyGroup>

  <ItemGroup>
    <Compile Include="HostConnectionState.fs" />
  </ItemGroup>

</Project>
```

(`HostMachine.fs` is added to the `<Compile>` list in Task 2, AFTER `HostConnectionState.fs` — F# compile order matters.)

- [ ] **Step 2: Create `HostConnectionState.fs`**

Move the enum verbatim from `ConnectionStatus.cs` (same namespace, same member order/values, same doc semantics):

```fsharp
namespace Lattice.Core

/// Connection lifecycle of one host. UI mapping: Connecting/Authorizing/FetchingState
/// all render as "Connecting…" (FetchingState may show "Fetching state from {host}…");
/// Retrying with Attempt >= 5 renders as "Unreachable"; AuthFailed is terminal until
/// the host's credentials are updated.
type HostConnectionState =
    /// Not started, or stopped by disposal.
    | Disconnected = 0
    /// Opening the TCP connection.
    | Connecting = 1
    /// Running the auth1/auth2 handshake (skipped instantly for empty passwords).
    | Authorizing = 2
    /// Fetching exchange_versions and the full get_state join tables.
    | FetchingState = 3
    /// Polling steadily; snapshots flow.
    | Connected = 4
    /// Waiting out an exponential backoff before reconnecting. Never gives up.
    | Retrying = 5
    /// The daemon refused the password. Terminal until UpdateConfig.
    | AuthFailed = 6
```

- [ ] **Step 3: Delete the enum from `ConnectionStatus.cs`**

Remove the `public enum HostConnectionState { ... }` block (lines 5–27) including its doc comment. Keep the `ConnectionStatus` record untouched.

- [ ] **Step 4: Wire references**

```bash
dotnet sln Lattice.sln add src/Lattice.Core.Machine/Lattice.Core.Machine.fsproj
dotnet add src/Lattice.Core/Lattice.Core.csproj reference src/Lattice.Core.Machine/Lattice.Core.Machine.fsproj
```

- [ ] **Step 5: Build + full test run**

Run: `dotnet build Lattice.sln -c Release -warnaserror && dotnet test Lattice.sln -c Release --no-build`
Expected: build clean; 193 tests (Lattice.Tests) + 18 (Lattice.Verification) pass. If any consumer fails to resolve `HostConnectionState`, the reference wiring is wrong — fix, don't cast.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "refactor(core): extract Lattice.Core.Machine; move HostConnectionState

Scaffolding for the functional-core restructure (spec
2026-07-08-hostmonitor-functional-core-design.md). Pure mechanical move:
the enum keeps its namespace, values, and docs; ConnectionStatus record
stays in C#. No HostMonitor semantics touched — no model change needed.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

## Task 2: `HostMachine` — the pure transition core

**Files:**
- Create: `src/Lattice.Core.Machine/HostMachine.fs` (add to fsproj `<Compile>` AFTER HostConnectionState.fs)
- Create: `tests/Lattice.Verification/MachinePins.fs` (add to fsproj `<Compile>` FIRST, before Model.fs)

The full source below is the controller-authored design. Transcribe it exactly; where F# syntax needs adjustment to compile, preserve semantics and flag the adjustment in your report. The parity comments reference `git show d3950c2:src/Lattice.Core/HostMonitor.cs` line numbers — keep them.

- [ ] **Step 1: Write the pin tests first (they fail: module doesn't exist)**

`tests/Lattice.Verification/MachinePins.fs`:

```fsharp
/// Direct pins on HostMachine routing decisions. These are documentation-grade
/// spot checks; exhaustive coverage is the wrapper exploration in Properties.fs.
module Lattice.Verification.MachinePins

open Xunit
open Lattice.Core
open Lattice.Core.HostMachine

let private tick = { maxSeqno = Some 3; hasUnknownWorkunit = false }

[<Theory>]
[<InlineData(1, 1.0)>]
[<InlineData(2, 2.0)>]
[<InlineData(4, 8.0)>]
[<InlineData(6, 32.0)>]
[<InlineData(7, 60.0)>]
[<InlineData(99, 60.0)>]
let ``backoff schedule matches HostMonitor.BackoffDelay`` (attempt: int) (seconds: float) =
    Assert.Equal(System.TimeSpan.FromSeconds seconds, backoffDelay attempt)

[<Fact>]
let ``post-Connected failure resets the attempt counter to 1 (I4)`` () =
    let s = { initial with phase = TearingDown(OFailed("boom", true)); attempt = 7 }
    let s', cmds = step s EffectOk
    Assert.Equal(1, s'.attempt)
    Assert.Contains(cmds, function
        | PublishStatus(HostConnectionState.Retrying, 1, Some _, Some "boom", false) -> true
        | _ -> false)

[<Fact>]
let ``pre-Connected failure counts up`` () =
    let s = { initial with phase = TearingDown(OFailed("boom", false)); attempt = 2 }
    let s', _ = step s EffectOk
    Assert.Equal(3, s'.attempt)

[<Fact>]
let ``accept guard bars Connected when a config change is pending (I1)`` () =
    let s = { initial with phase = AcceptGuard }
    let s', cmds = step s (GuardObserved true)
    Assert.True(match s'.phase with TearingDown OConfigChanged -> true | _ -> false)
    Assert.DoesNotContain(cmds, function PublishStatus(HostConnectionState.Connected, _, _, _, _) -> true | _ -> false)

[<Fact>]
let ``second consecutive mid-poll unauthorized escalates to the Failed path`` () =
    let s = { initial with phase = TickAwait; reachedConnected = true
                           reauthedSinceLastSuccess = true; hasPassword = true }
    let s', _ = step s (Faulted(Unauthorized "expired"))
    Assert.True(match s'.phase with TearingDown(OFailed("expired", true)) -> true | _ -> false)

[<Fact>]
let ``first mid-poll unauthorized triggers one silent re-auth`` () =
    let s = { initial with phase = TickAwait; reachedConnected = true; hasPassword = true }
    let s', cmds = step s (Faulted(Unauthorized "expired"))
    Assert.Equal(Reauthorizing, s'.phase)
    Assert.Equal<Command list>([ Authorize ], cmds)

[<Fact>]
let ``mid-poll unauthorized without a password parks`` () =
    let s = { initial with phase = TickAwait; reachedConnected = true; hasPassword = false }
    let s', _ = step s (Faulted(Unauthorized "expired"))
    Assert.True(match s'.phase with TearingDown(OAuthFailed "The host refused the password.") -> true | _ -> false)

[<Fact>]
let ``first tick replaces the log, second appends`` () =
    let s = { initial with phase = MsgGuard; tick = Some tick; firstTick = true }
    let s', cmds = step s (GuardObserved false)
    Assert.Contains(cmds, function PublishMessages true -> true | _ -> false)
    Assert.False(s'.firstTick)
    Assert.Equal(3, s'.lastSeqno)
    let s2 = { s' with phase = MsgGuard; tick = Some { tick with maxSeqno = None } }
    let _, cmds2 = step s2 (GuardObserved false)
    Assert.Contains(cmds2, function PublishMessages false -> true | _ -> false)
```

Note: `Assert.Contains(list, predicate)` needs `System.Collections.Generic` overload — if xUnit's overload resolution fights F# lambdas, use `Assert.True(cmds |> List.exists (function ... ))` instead; same meaning.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Lattice.Verification 2>&1 | tail -20`
Expected: compile FAILURE (`HostMachine` not defined).

- [ ] **Step 3: Implement `HostMachine.fs`**

```fsharp
namespace Lattice.Core

/// Interleaving-point names shared with the C# shell's InterleavePoints class
/// (which aliases these as consts) and the interleaving sweep harness.
[<RequireQualifiedAccess>]
module ProbePoints =
    [<Literal>]
    let BeforeSnapshot = "attempt.beforeSnapshot"
    [<Literal>]
    let AfterSnapshot = "attempt.afterSnapshot"
    [<Literal>]
    let BeforeAcceptGuard = "attempt.beforeAcceptGuard"
    [<Literal>]
    let BeforeConnectedPublish = "attempt.beforeConnectedPublish"
    [<Literal>]
    let TickBeforeMsgGuard = "tick.beforeMsgGuard"
    [<Literal>]
    let TickBeforeMsgPublish = "tick.beforeMsgPublish"
    [<Literal>]
    let TickBeforeSnapGuard = "tick.beforeSnapGuard"
    [<Literal>]
    let TickBeforeBuild = "tick.beforeBuild"
    [<Literal>]
    let TickBeforeSnapPublish = "tick.beforeSnapPublish"
    [<Literal>]
    let PollBeforeWait = "poll.beforeWait"
    [<Literal>]
    let PollAfterWait = "poll.afterWait"
    [<Literal>]
    let FinallyEnter = "attempt.finallyEnter"
    [<Literal>]
    let AfterCtsDispose = "attempt.afterCtsDispose"
    [<Literal>]
    let BeforeRetryPublish = "dispatcher.beforeRetryPublish"
    [<Literal>]
    let BeforeParkWait = "dispatcher.beforeParkWait"

/// The pure decision core of HostMonitor: all phase sequencing, guard routing,
/// attempt counting, and auth handling live here. The C# shell executes Commands
/// and feeds Inputs back; it makes no routing decisions of its own.
///
/// This module is public so Lattice.Core and Lattice.Verification can consume it,
/// but it is an implementation detail of Lattice.Core, not a supported API.
///
/// Interpreter contract (the shell and the verification wrapper both obey it):
///  - step is total: every (phase, input) pair returns; unexpected pairs fall
///    through to a safe loop exit (the loop task must never fault — I5/A5).
///  - a Command batch contains zero or more fire-and-forget commands and at most
///    one trailing request command; the request's result is the next Input.
///  - a batch with no request command yields EffectOk as the next Input.
///  - if executing ANY command throws, the shell skips the rest of the batch and
///    feeds Faulted (classified by exception TYPE only: OperationCanceledException
///    with the outer token canceled -> Disposal; other OCE -> ConnCanceled;
///    BoincUnauthorizedException -> Unauthorized; anything else -> Failure).
module HostMachine =

    /// Exception classification produced by the shell. Routing by phase/history
    /// is the core's job; the shell only looks at the exception type.
    type FailureKind =
        | Disposal
        | ConnCanceled
        | Unauthorized of message: string
        | Failure of message: string

    /// How an attempt ended, carried through the Teardown phase — the former
    /// AttemptOutcome (HostMonitor.cs d3950c2:319-339).
    type Outcome =
        | ODisposal
        | OConfigChanged
        | OAuthFailed of error: string
        | OFailed of error: string * reachedConnected: bool

    /// Payload of one tick's RPC batch, reduced to what routing needs.
    /// maxSeqno = Some m when the fetch returned messages (m = their max seqno).
    type TickInfo = { maxSeqno: int option; hasUnknownWorkunit: bool }

    /// Each phase awaits exactly one Input kind (named by what it waits for).
    type Phase =
        | Dispatch          // awaiting DispatchObserved
        | SnapshotWait      // awaiting ConfigSnapshotted
        | Connecting        // awaiting EffectOk (factory+Connecting publish+connect ran)
        | Authorizing       // awaiting AuthResult (or EffectOk when no password)
        | Fetching          // awaiting FetchOk
        | AcceptGuard       // awaiting GuardObserved (plain read — d3950c2:464-466)
        | TickAwait         // awaiting TickFetched (or Faulted Unauthorized -> re-auth)
        | Reauthorizing     // awaiting AuthResult (silent re-auth, d3950c2:548-565)
        | MsgGuard          // awaiting GuardObserved (throw-then-read — d3950c2:593-596)
        | Refetching        // awaiting EffectOk (get_state refetch — d3950c2:617-619)
        | SnapGuard         // awaiting GuardObserved (throw-then-read — d3950c2:624-627)
        | PostBuildGuard    // awaiting GuardObserved (post-build recheck — d3950c2:639-641)
        | PollObserve       // awaiting GuardObserved (pre-wait check — d3950c2:567-568)
        | PollWaiting       // awaiting WaitEnded
        | PostWaitObserve   // awaiting GuardObserved (post-wait check — d3950c2:572-573)
        | TearingDown of Outcome  // awaiting EffectOk (teardown batch ran)
        | BackoffWaiting    // awaiting WaitEnded
        | PostBackoffObserve // awaiting GuardObserved (d3950c2:395-396)
        | Parked            // awaiting WaitEnded (config change releases)
        | Exited

    type State = {
        phase: Phase
        attempt: int                      // dispatcher retry counter
        hasPassword: bool                 // from the attempt's config snapshot
        reachedConnected: bool            // Connected published this attempt
        firstTick: bool                   // rider B: next tick replaces the log
        lastSeqno: int                    // per-connection message cursor
        reauthedSinceLastSuccess: bool    // one silent re-auth per successful tick
        tick: TickInfo option             // pending tick payload (set at TickFetched)
    }

    type Input =
        | Started
        | DispatchObserved of disposalRequested: bool
        | ConfigSnapshotted of hasPassword: bool
        | EffectOk
        | AuthResult of authorized: bool
        | FetchOk
        | TickFetched of TickInfo
        | GuardObserved of configChanged: bool
        | WaitEnded
        | Faulted of FailureKind

    type Command =
        | Probe of point: string
        | ObserveDispatch                 // read the outer token's IsCancellationRequested
        | SnapshotConfig                  // THE one atomic lock block (rule 1): snapshot
                                          // config, clear _configChanged, create linked CTS
        | CreateClient                    // factory only (throws fold to Failure — d3950c2:433-436)
        | Connect
        | Authorize
        | FetchVersionAndState            // exchange_versions + get_state into attempt locals
        | PublishStatus of status: HostConnectionState * attempt: int
                         * backoff: System.TimeSpan option * error: string option
                         * stampDaemonVersion: bool  // rider A: true ONLY at Connected
        | RunTickRpcs of lastSeqno: int   // cc_status/results/transfers/messages
        | PublishMessages of replaceLog: bool
        | RefetchState
        | BuildSnapshot                   // pure build into an attempt local
        | PublishSnapshot                 // volatile write + SnapshotUpdated
        | ObserveConfigChanged            // plain volatile read of _configChanged
        | ObserveTickGuard                // connCt.ThrowIfCancellationRequested, THEN read
        | WaitPollInterval                // WaitAsync(interval, outer ct)
        | WaitBackoff of System.TimeSpan  // WaitAsync(delay, outer ct)
        | ParkForConfigChange             // WaitForConfigChangeAsync(outer ct)
        | DisposeConnectionCts            // lock { _connectionCts?.Dispose(); null }
        | DisposeClient                   // swallow failures (d3950c2:513-517)
        | ExitLoop

    /// Backoff schedule: 1, 2, 4, 8, 16, 32 then capped at 60 seconds.
    let backoffDelay (attempt: int) : System.TimeSpan =
        System.TimeSpan.FromSeconds(min 60.0 (2.0 ** float (attempt - 1)))

    let initial = {
        phase = Dispatch; attempt = 0; hasPassword = false
        reachedConnected = false; firstTick = true; lastSeqno = 0
        reauthedSinceLastSuccess = false; tick = None }

    let private refusedPassword = "The host refused the password."

    /// The attempt's finally block (d3950c2:501-518): CTS then client, probes between.
    let private teardown (s: State) (o: Outcome) : State * Command list =
        { s with phase = TearingDown o },
        [ Probe ProbePoints.FinallyEnter
          DisposeConnectionCts
          Probe ProbePoints.AfterCtsDispose
          DisposeClient ]

    /// Loop exit: publish Disconnected once, stop (d3950c2:398).
    let private exitLoop (s: State) : State * Command list =
        { s with phase = Exited },
        [ PublishStatus(HostConnectionState.Disconnected, 0, None, None, false); ExitLoop ]

    let private toDispatch (s: State) attempt : State * Command list =
        { s with phase = Dispatch; attempt = attempt }, [ ObserveDispatch ]

    let private toFetch (s: State) : State * Command list =
        { s with phase = Fetching },
        [ PublishStatus(HostConnectionState.FetchingState, s.attempt, None, None, false)
          FetchVersionAndState ]

    /// Faults while the attempt's client/CTS may be live: tear down first, then
    /// route — the same fold RunAttemptAsync's catch set performed (d3950c2:475-500).
    let private faultInAttempt (s: State) (k: FailureKind) : State * Command list =
        match k with
        | Disposal -> teardown s ODisposal
        | ConnCanceled -> teardown s OConfigChanged
        | Unauthorized m -> teardown s (OAuthFailed m)
        | Failure m -> teardown s (OFailed(m, s.reachedConnected))

    let step (s: State) (input: Input) : State * Command list =
        match s.phase, input with

        // ---------------- dispatcher ----------------
        | Dispatch, Started -> s, [ ObserveDispatch ]
        | Dispatch, DispatchObserved true -> exitLoop s
        | Dispatch, DispatchObserved false ->
            { s with phase = SnapshotWait },
            [ Probe ProbePoints.BeforeSnapshot; SnapshotConfig ]

        // ---------------- one attempt, cradle to grave ----------------
        | SnapshotWait, ConfigSnapshotted hasPassword ->
            // per-attempt state resets exactly as the old attempt scope did:
            // reachedConnected/connected (d3950c2:440), firstTick+lastSeqno+reauthed
            // (PollAsync locals, d3950c2:530-538), tick payload cleared
            { s with hasPassword = hasPassword
                     reachedConnected = false; firstTick = true; lastSeqno = 0
                     reauthedSinceLastSuccess = false; tick = None
                     phase = Connecting },
            [ Probe ProbePoints.AfterSnapshot
              CreateClient                     // factory BEFORE the Connecting publish (d3950c2:443-445)
              PublishStatus(HostConnectionState.Connecting, s.attempt, None, None, false)
              Connect ]

        | Connecting, EffectOk ->
            // Authorizing is published even for empty passwords ("skipped instantly")
            { s with phase = Authorizing },
            [ yield PublishStatus(HostConnectionState.Authorizing, s.attempt, None, None, false)
              if s.hasPassword then yield Authorize ]

        | Authorizing, EffectOk -> toFetch s            // empty password: no auth RPC
        | Authorizing, AuthResult true -> toFetch s
        | Authorizing, AuthResult false ->
            teardown s (OAuthFailed refusedPassword)    // d3950c2:448-450, 485-487

        | Fetching, FetchOk ->
            { s with phase = AcceptGuard },
            [ Probe ProbePoints.BeforeAcceptGuard; ObserveConfigChanged ]

        | AcceptGuard, GuardObserved true -> teardown s OConfigChanged   // d3950c2:465-466
        | AcceptGuard, GuardObserved false ->
            // rider A: daemon version stamped ONLY here; dispatcher owns the
            // attempt counter (no reset here — d3950c2:467-471 + Model.fs PublishConnected)
            { s with phase = TickAwait; reachedConnected = true },
            [ Probe ProbePoints.BeforeConnectedPublish
              PublishStatus(HostConnectionState.Connected, 0, None, None, true)
              RunTickRpcs s.lastSeqno ]                 // tick immediately on entry (d3950c2:521)

        // ---------------- steady-state tick ----------------
        | TickAwait, TickFetched info ->
            { s with tick = Some info; phase = MsgGuard },
            [ Probe ProbePoints.TickBeforeMsgGuard; ObserveTickGuard ]

        // one silent re-auth per successful tick; a second consecutive unauthorized
        // escalates as a PLAIN failure (the former HostSessionLostException,
        // d3950c2:548-565); no password -> park immediately
        | TickAwait, Faulted (Unauthorized m) ->
            if s.reauthedSinceLastSuccess then teardown s (OFailed(m, s.reachedConnected))
            elif not s.hasPassword then teardown s (OAuthFailed refusedPassword)
            else { s with phase = Reauthorizing }, [ Authorize ]

        | Reauthorizing, AuthResult true ->
            { s with reauthedSinceLastSuccess = true; phase = TickAwait },
            [ RunTickRpcs s.lastSeqno ]                 // retry the tick, same cursor
        | Reauthorizing, AuthResult false ->
            teardown s (OAuthFailed refusedPassword)

        | MsgGuard, GuardObserved true -> teardown s OConfigChanged
        | MsgGuard, GuardObserved false ->
            match s.tick with
            | None -> exitLoop s                        // unreachable; defensive (never fault)
            | Some info ->
                let lastSeqno = defaultArg info.maxSeqno s.lastSeqno
                { s with lastSeqno = lastSeqno; firstTick = false
                         phase = (if info.hasUnknownWorkunit then Refetching else SnapGuard) },
                [ yield Probe ProbePoints.TickBeforeMsgPublish
                  yield PublishMessages s.firstTick     // pre-update value: replace on first tick
                  if info.hasUnknownWorkunit then
                      yield RefetchState
                  else
                      yield Probe ProbePoints.TickBeforeSnapGuard
                      yield ObserveTickGuard ]

        | Refetching, EffectOk ->
            { s with phase = SnapGuard },
            [ Probe ProbePoints.TickBeforeSnapGuard; ObserveTickGuard ]

        | SnapGuard, GuardObserved true -> teardown s OConfigChanged
        | SnapGuard, GuardObserved false ->
            { s with phase = PostBuildGuard },
            [ Probe ProbePoints.TickBeforeBuild; BuildSnapshot; ObserveTickGuard ]

        | PostBuildGuard, GuardObserved true -> teardown s OConfigChanged
        | PostBuildGuard, GuardObserved false ->
            { s with phase = PollObserve },
            [ Probe ProbePoints.TickBeforeSnapPublish; PublishSnapshot; ObserveConfigChanged ]

        | PollObserve, GuardObserved true -> teardown s OConfigChanged   // d3950c2:567-568
        | PollObserve, GuardObserved false ->
            { s with phase = PollWaiting },
            [ Probe ProbePoints.PollBeforeWait; WaitPollInterval ]

        | PollWaiting, WaitEnded ->
            { s with phase = PostWaitObserve },
            [ Probe ProbePoints.PollAfterWait; ObserveConfigChanged ]

        | PostWaitObserve, GuardObserved true -> teardown s OConfigChanged
        | PostWaitObserve, GuardObserved false ->
            { s with phase = TickAwait }, [ RunTickRpcs s.lastSeqno ]

        // ---------------- teardown routing (the dispatcher's outcome switch) ----------------
        | TearingDown o, EffectOk ->
            match o with
            | ODisposal -> exitLoop s
            | OConfigChanged -> toDispatch s 0          // reconnect now, counter reset
            | OAuthFailed err ->
                // publish AFTER teardown: refused-password connection provably closed
                // for the whole parked wait (d3950c2:370-381)
                { s with attempt = 0; phase = Parked },
                [ PublishStatus(HostConnectionState.AuthFailed, 0, None, Some err, false)
                  Probe ProbePoints.BeforeParkWait
                  ParkForConfigChange ]
            | OFailed(err, reached) ->
                // I4: a post-Connected failure resets the counter, then counts as 1
                let attempt = if reached then 1 else s.attempt + 1
                let delay = backoffDelay attempt
                { s with attempt = attempt; phase = BackoffWaiting },
                [ Probe ProbePoints.BeforeRetryPublish
                  PublishStatus(HostConnectionState.Retrying, attempt, Some delay, Some err, false)
                  WaitBackoff delay ]

        | Parked, WaitEnded -> toDispatch s 0           // only a config change releases

        | BackoffWaiting, WaitEnded ->
            { s with phase = PostBackoffObserve }, [ ObserveConfigChanged ]
        | PostBackoffObserve, GuardObserved cc ->
            toDispatch s (if cc then 0 else s.attempt)  // d3950c2:395-396

        // ---------------- fault folding ----------------
        // In-attempt (client/CTS may be live, incl. the poll wait — the connection
        // stays open while polling): tear down, then route.
        | (SnapshotWait | Connecting | Authorizing | Fetching | AcceptGuard
           | TickAwait | Reauthorizing | MsgGuard | Refetching | SnapGuard
           | PostBuildGuard | PollObserve | PollWaiting | PostWaitObserve), Faulted k ->
            faultInAttempt s k
        // Post-teardown waits: only disposal can cancel them; client already closed
        // (d3950c2:377-378, 393-394 catch OperationCanceledException -> break).
        | (BackoffWaiting | PostBackoffObserve | Parked | TearingDown _ | Dispatch), Faulted _ ->
            exitLoop s

        | Exited, _ -> s, [ ExitLoop ]

        // Unexpected (phase, input) pairing = interpreter bug. The loop task must
        // never fault (I5/A5), so settle in Disconnected rather than throw.
        | _, _ -> exitLoop s
```

- [ ] **Step 4: Add both files to their fsproj `<Compile>` lists**

`Lattice.Core.Machine.fsproj`: `HostConnectionState.fs` then `HostMachine.fs`.
`Lattice.Verification.fsproj`: `MachinePins.fs` FIRST (before `Model.fs`), and add a project reference to `src/Lattice.Core.Machine/Lattice.Core.Machine.fsproj`.

- [ ] **Step 5: Run the pins**

Run: `dotnet test tests/Lattice.Verification 2>&1 | tail -10`
Expected: PASS — 18 old + 13 new (6 theory cases + 7 facts). All 193 C# tests untouched.

- [ ] **Step 6: Full gate + commit**

Run: `dotnet build Lattice.sln -c Release -warnaserror && dotnet test Lattice.sln -c Release --no-build`
Expected: clean.

```bash
git add -A
git commit -m "feat(core): HostMachine — pure transition core for HostMonitor

The concurrency protocol (phase sequencing, guard routing, attempt
counting, auth handling, tick pipeline) as a total pure function
step : State -> Input -> State * Command list, grown from the verified
executable spec. Not yet wired into production; direct routing pins
included. HostMonitor.cs untouched — no model change needed (the model
migrates onto this core in the next commit).

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

## Task 3: Verification rewrite — the wrapper explores the production core

**Files:**
- Rewrite: `tests/Lattice.Verification/Model.fs` (becomes the shell/environment model driving `HostMachine.step`)
- Modify: `tests/Lattice.Verification/Explorer.fs` (only the `isLoopAction` case list)
- Rewrite: `tests/Lattice.Verification/Properties.fs` (same 18 test names, predicates re-expressed; mutants ported)

**The 18 test names must not change** (they are referenced by verification/README.md's cross-reference table). Property MEANINGS must not weaken — the 7 mutants are the empirical check.

- [ ] **Step 1: Rewrite `Model.fs` as the shell model**

Design (implement exactly; keep the module name `Lattice.Verification.Model` and type name `S`):

```fsharp
/// Shell model: the environment (UpdateConfig/Wake/Start/Dispose) and the shell
/// primitives (TCS wake latch, CTS states, volatile flag, interpreter command
/// queue) around the PRODUCTION HostMachine.step. The decision core is no longer
/// modeled — it is executed. Drift surface: this file vs the real C# shell,
/// pinned by tests/Lattice.Tests/Interleaving.
module Lattice.Verification.Model

open Lattice.Core
open Lattice.Core.HostMachine

type CtsState = CtsNone | CtsLive | CtsCanceled | CtsDisposed
type LoopTask = TaskInitial | TaskStored | TaskRunning | TaskFaulted | TaskDone
type WaitKind = WPoll | WBackoff | WPark

type Bounds = { updates: int; wakes: int; failures: int; disposes: int }

type S = {
    // ---- gate-protected / volatile shell state (as before) ----
    curVersion: int
    configChanged: bool
    cts: CtsState
    wake: bool
    started: bool
    loopTask: LoopTask
    outerCanceled: bool
    outerDisposed: bool
    disposeFlag: bool
    // ---- the interpreter ----
    core: HostMachine.State
    queue: Command list          // rest of the current batch (probes filtered out)
    waiting: WaitKind option     // loop blocked in a shell wait
    // ---- attempt bookkeeping the shell owns ----
    attemptVersion: int          // config generation the last SnapshotConfig captured
    connLive: bool
    injectedFail: bool           // failure budget consumed by this attempt
    // ---- observables ----
    statusState: HostConnectionState
    statusVersion: int
    daemonVersionVintage: int option
    logVintage: int option
    logReplacedThisConn: bool
    faulted: bool
    // ---- env budgets ----
    updatesLeft: int; wakesLeft: int; failsLeft: int; disposesLeft: int
}
```

`initial b` mirrors the old one: core = `HostMachine.initial`, queue = `[]`, waiting = None, statusState = Disconnected, attemptVersion = -1, everything else as the old `initial`.

Actions:

```fsharp
type Action =
    | EnvStart | EnvUpdateConfig | EnvWake | EnvDispose        // unchanged semantics
    | ExecCmd                    // execute the head command / feed the next input
    | ExecCmdFail                // failure-injected variant of an RPC request
    | ExecAuthRefused            // AuthResult false branch of Authorize
    | ExecUnauthorized           // mid-tick BoincUnauthorized branch of RunTickRpcs
    | DelayFires | WakeConsumed  // wait completions (unchanged semantics)
```

Central helper — feeding an input to the production core:

```fsharp
/// Run the production step, enqueue its batch with Probe commands filtered
/// (probes are no-ops with no state change; two adjacent env windows separated
/// by a no-op are equivalent, so filtering loses no interleavings).
let private feed (s: S) (input: Input) : S =
    let core', cmds = HostMachine.step s.core input
    { s with core = core'
             queue = cmds |> List.filter (function Probe _ -> false | _ -> true) }
```

`enabled s` rules:
- env actions exactly as the old model (same budget guards, same Start gating).
- loop actions only when `s.started && not s.faulted && s.core.phase <> Exited`:
  - `waiting = Some w` → `DelayFires` (not for WPark), `WakeConsumed` (if wake), plus `ExecCmd` enabled when the wait is releasable without the latch (WPark with configChanged; any wait with outerCanceled) — mirror the old model's wait arms.
  - `waiting = None && queue = []` → `ExecCmd` (feeds the phase's pending input — only happens at start: feeds `Started`).
  - `waiting = None && queue <> []` → `ExecCmd`; plus `ExecCmdFail` when head is an RPC request (`Connect | Authorize | FetchVersionAndState | RunTickRpcs | RefetchState`) and `failsLeft > 0 && not injectedFail`; plus `ExecAuthRefused` when head is `Authorize`; plus `ExecUnauthorized` when head is `RunTickRpcs` and `failsLeft > 0 && not injectedFail`.

`step s action` — the command execution table (ExecCmd on head `cmd`; fire-and-forget commands pop the head and apply their effect; request commands pop, compute the Input, and `feed`):

| Command | Effect on wrapper state | Input produced |
|---|---|---|
| (queue empty, first run) | — | `feed s Started` |
| `ObserveDispatch` | — | `DispatchObserved s.outerCanceled` |
| `SnapshotConfig` | ONE atomic step: `attemptVersion <- curVersion; configChanged <- false; cts <- CtsLive; injectedFail <- false; logReplacedThisConn <- false; daemonVersionVintage <- None; logVintage <- None` | `ConfigSnapshotted hasPassword` — **branch nondeterministically on hasPassword ∈ {true, false}** (two successor states) |
| `CreateClient` | — (fire-forget) | — |
| `Connect` | if `cts = CtsCanceled` → `feed (Faulted ConnCanceled)`; elif `outerCanceled` → `feed (Faulted Disposal)`; else `connLive <- true`, `feed EffectOk` | |
| `Authorize` | same cancel checks; else `feed (AuthResult true)` (ExecAuthRefused: `feed (AuthResult false)`; ExecCmdFail: `injectedFail <- true; failsLeft--; feed (Faulted (Failure "err"))`) | |
| `FetchVersionAndState` | same cancel checks; else `feed FetchOk` (ExecCmdFail as above) | |
| `PublishStatus(st, _, _, _, stamp)` | `statusState <- st; statusVersion <- attemptVersion; if stamp then daemonVersionVintage <- Some attemptVersion` (fire-forget) | — |
| `RunTickRpcs _` | cancel checks; else **branch**: `feed (TickFetched { maxSeqno = None; hasUnknownWorkunit = w })` and `feed (TickFetched { maxSeqno = Some 1; hasUnknownWorkunit = w })` for w ∈ {false, true} — 4 successors (ExecCmdFail / ExecUnauthorized: budgeted faults `Failure "err"` / `Unauthorized "err"`) | |
| `PublishMessages replace` | `logVintage <- Some attemptVersion; logReplacedThisConn <- logReplacedThisConn || replace` (fire-forget) | — |
| `RefetchState` | cancel checks; else `feed EffectOk` (ExecCmdFail as above) | |
| `BuildSnapshot` | — (fire-forget) | — |
| `PublishSnapshot` | — (fire-forget; snapshot vintage rides statusVersion as before) | — |
| `ObserveConfigChanged` | — | `GuardObserved s.configChanged` |
| `ObserveTickGuard` | if `cts = CtsCanceled` → `feed (Faulted ConnCanceled)`; elif `outerCanceled` → `feed (Faulted Disposal)`; else | `GuardObserved s.configChanged` |
| `WaitPollInterval` | enter wait: if `wake` then consume latch (`wake <- false`) and `feed WaitEnded` immediately (WaitAsync entry-consume, d3950c2:250-256); else `waiting <- Some WPoll` | |
| `WaitBackoff _` | same entry-consume; else `waiting <- Some WBackoff` | |
| `ParkForConfigChange` | if `configChanged` then `feed WaitEnded` (keep the latch — WaitForConfigChangeAsync returns before consuming when the flag is already set, d3950c2:279-289); elif `wake` then `wake <- false` and `waiting <- Some WPark`; else `waiting <- Some WPark` | |
| `DisposeConnectionCts` | `cts <- CtsNone` (the lock block) | — |
| `DisposeClient` | `connLive <- false` | — |
| `ExitLoop` | `loopTask <- TaskDone; outerDisposed <- true; waiting <- None` | — (loop actions disabled once phase = Exited) |

Wait completions (`waiting = Some w`):
- `DelayFires` (WPoll/WBackoff only): `waiting <- None`, `feed WaitEnded` (latch survives — sticky).
- `WakeConsumed` (wake set): `wake <- false`; for WPark: stale wake, stay waiting (no feed) — unless `configChanged`, in which case release: `waiting <- None`, `feed WaitEnded`. For WPoll/WBackoff: `waiting <- None`, `feed WaitEnded`.
- `ExecCmd` on a wait with `outerCanceled`: `waiting <- None`, `feed (Faulted Disposal)`.
- `ExecCmd` on WPark with `configChanged`: `waiting <- None`, `feed WaitEnded`.

Env actions: port `EnvStart`/`EnvUpdateConfig`/`EnvWake`/`EnvDispose` from the old Model.fs verbatim (same budgets, same rider-C dispose encoding, same Start-after-outerDisposed fault branch that M5 relies on), adjusting only: `EnvStart` stores the loop task and (if not yet run) leaves `queue = []` so the first `ExecCmd` feeds `Started`; `EnvUpdateConfig` cancels `cts` when `CtsLive` exactly as before.

- [ ] **Step 2: Update `Explorer.fs` `isLoopAction`**

```fsharp
let private isLoopAction = function
    | ExecCmd | ExecCmdFail | ExecAuthRefused | ExecUnauthorized
    | DelayFires | WakeConsumed -> true
    | EnvStart | EnvUpdateConfig | EnvWake | EnvDispose -> false
```

Nothing else in Explorer.fs changes.

- [ ] **Step 3: Rewrite `Properties.fs`**

Same `bounds`, same 18 test names. Re-expressed predicates:

- `exploration terminates and reaches Connected`: unchanged shape; also print/assert the state count is > 100 and note the total in the task report.
- **I1 publish** (edge-shaped guard check): for every state whose `waiting = None`, head of `queue` is `ObserveConfigChanged`/`ObserveTickGuard`, `configChanged = true`, and the *paired publish* is identifiable from `core.phase` (`AcceptGuard` → `PublishStatus(Connected...)`; `MsgGuard` → `PublishMessages`; `SnapGuard`/`PostBuildGuard` → `PublishSnapshot`): every `ExecCmd` successor's `queue` must not contain that publish command.
- **I1 mutation**: unchanged meaning: `core.reachedConnected || (daemonVersionVintage <> Some attemptVersion && logVintage <> Some attemptVersion) || attemptVersion < 0`.
- **I2**: `core.phase ∈ {BackoffWaiting, PostBackoffObserve, Parked, Exited} → not connLive`. (These phases begin strictly after the teardown batch ran — same window the old `RetryDecide`/`BackoffWait`/`Park`/`Exited` covered.)
- **I3**: `attemptVersion = curVersion || configChanged || cts = CtsCanceled ||` core.phase ∈ safe set `{Dispatch, SnapshotWait, TearingDown _, BackoffWaiting, PostBackoffObserve, Parked, Exited}` `|| not started`.
- **I4** (edge-shaped): for edges where `ExecCmd` completes the teardown batch of `TearingDown (OFailed(_, true))` (head = last teardown command, queue becomes the retry batch): successor `core.attempt = 1`. Equivalent simpler check: every reachable state with `core.phase = BackoffWaiting` whose queue still contains the `PublishStatus(Retrying, n, ...)` command has `n = 1` when the teardown outcome had `reachedConnected = true` — implement whichever is cleaner, but it must turn red under mutant M2.
- **I5**: `not faulted` — unchanged.
- **L1/L2/L3/L3b**: same `checkEventually` calls; "loop not running" discharge is `not started` (the old `Idle`) or `core.phase = Exited`. L3b: from any state with `core.phase = Parked`, `not configChanged`, `not outerCanceled`: successors stay `Parked` (or the wake-consumption self-transition), never elsewhere.
- **Mutants** (all 7 keep their test names; each must still be detected):
  - M1 split snapshot: mutant `step` executes `SnapshotConfig` in TWO wrapper micro-steps (version capture first, flag-clear+CTS-create in a later step) → I3 red.
  - M2 no attempt reset: decorate the core: `let m2 s a = ...` intercept the wrapper's `TearingDown(OFailed(_, true))` completion and override the successor's `core.attempt` to `min (attempt+1) ...` (i.e. keep counting) and patch the queued `PublishStatus(Retrying, ...)` attempt likewise → I4 red.
  - M3 eager daemonVersion: wrapper stamps `daemonVersionVintage <- Some attemptVersion` when executing `FetchVersionAndState` → I1-mutation red.
  - M4 eager log clear: wrapper stamps `logVintage <- Some attemptVersion` when executing `FetchVersionAndState` → I1-mutation red.
  - M5 no dispose flag: env mutation, port verbatim → I5 red.
  - M6 deaf waits: wrapper drops every wake consumption (no `WakeConsumed`, no entry-consume in `WaitPollInterval`/`WaitBackoff`) → L1 red.
  - M7 stuck park: `WPark` ignores `configChanged` (release only via `outerCanceled`) → L2 red.

- [ ] **Step 4: Run the verification suite**

Run: `dotnet test tests/Lattice.Verification 2>&1 | tail -15`
Expected: PASS, 31 tests (18 spec + 13 pins). Record the healthy-exploration state count in your report (baseline was 27,510; the wrapper has finer granularity — same order of magnitude up to ~10x is acceptable; more means the canonicalization leaked an unbounded value: STOP and find it).

- [ ] **Step 5: Empirically re-verify one mutant is load-bearing**

Temporarily comment out the `logVintage <- Some attemptVersion` line in the wrapper's `PublishMessages` handler, run, and confirm `mutant M4 eager log clear violates I1-mutation` STILL PASSES but `I1 no pre-accept mutation...` still passes too — then instead break the core check: temporarily make the wrapper stamp `logVintage` in `FetchVersionAndState` (un-mutated path) and confirm `I1 no pre-accept mutation of daemonVersion or message log` FAILS. Revert. This proves the property reads the wrapper fields the mutants write.

Run: `dotnet test tests/Lattice.Verification --filter "I1" 2>&1 | tail -5` (with the temporary break in place)
Expected: FAIL on the invariant test; then revert and re-run: PASS.

- [ ] **Step 6: Full gate + commit**

Run: `dotnet build Lattice.sln -c Release -warnaserror && dotnet test Lattice.sln -c Release --no-build`
Expected: clean; 193 C# tests still untouched and green.

```bash
git add -A
git commit -m "test(verify): executable spec explores the production HostMachine core

Model.fs becomes the shell model: environment actions + shell primitives
(wake latch, CTS, volatile flag, interpreter queue) around the REAL
HostMachine.step — decision-logic drift between model and implementation
is now structurally impossible. Properties I1-I5/L1-L3 keep their names
and meanings; all 7 mutants ported and still red-first (M2-M4 as
step/effect decorators simulating the historical defects). New coverage:
the silent re-auth path and the refetch branch are now explored.
Exploration: <STATE COUNT> states. HostMonitor.cs untouched.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

(Fill `<STATE COUNT>` with the real number.)

---

## Task 4: HostMonitor becomes the interpreter shell (NO commit — pairs with Task 5)

**Files:**
- Rewrite: `src/Lattice.Core/HostMonitor.cs` (keep everything listed under KEEP; replace `RunAsync`/`RunAttemptAsync`/`PollAsync`/`TickAsync`/`AttemptResult`/`AttemptOutcome`/`HostAuthException`/`HostSessionLostException` with the interpreter)

**KEEP verbatim (fields, protection regimes, doc comments):** class doc, `MessagesAddedEventArgs`, all fields (`_clientFactory`, `_time`, `_cts`, `_gate`, `_config`, `_configChanged`, `_connectionCts`, `_pollingIntervalSeconds`, `_wake`, `_loop`, `_started`, `_disposed`, `_daemonVersion`, `_status`, `_snapshot`, `_messages`, `MessageCapacity`, `InterleaveProbe`), ctor, `HostId`, `Status`, `Snapshot`, `Messages`, all three events, `Start`, `RequestRefresh`, `UpdateConfig`, `SetPollingInterval`, `DisposeAsync`, `NewWake`, `Wake`, `WaitAsync`, `WaitForConfigChangeAsync`, `RaiseSafe`, `SetStatus`, `ProbeAsync`.

**`InterleavePoints`** stays but its consts alias the machine:

```csharp
internal static class InterleavePoints
{
    public const string BeforeSnapshot = ProbePoints.BeforeSnapshot;
    // ... all 15, same names ...
    public static readonly string[] All = [ /* same order as before */ ];
}
```

**`BackoffDelay`** delegates: `internal static TimeSpan BackoffDelay(int attempt) => HostMachine.backoffDelay(attempt);`

- [ ] **Step 1: Write the interpreter**

Structure (complete the switch — every `HostMachine.Command` case; C# sees F# DU cases as nested types, match with `is` patterns):

```csharp
private async Task RunAsync(CancellationToken ct)
{
    var state = HostMachine.initial;
    HostMachine.Input input = HostMachine.Input.Started;

    // Attempt-scoped resources and payloads, owned by the interpreter. The core
    // decides; these execute. Reset at SnapshotConfig.
    IGuiRpcClient? client = null;
    HostConfig config = _config;              // overwritten by SnapshotConfig
    CancellationToken connCt = default;
    VersionInfo? fetchedVersion = null;
    CcState? ccState = null;
    CcStatus? ccStatus = null;
    IReadOnlyList<Result> results = [];
    IReadOnlyList<FileTransfer> transfers = [];
    IReadOnlyList<Message> newMessages = [];
    HostSnapshot? builtSnapshot = null;

    async Task<HostMachine.Input> ExecuteAsync(HostMachine.Command cmd)
    {
        switch (cmd)
        {
            case HostMachine.Command.Probe p:
                await ProbeAsync(p.point).ConfigureAwait(false);
                return HostMachine.Input.EffectOk;   // fire-and-forget: caller ignores

            case var c when c.IsObserveDispatch:
                return HostMachine.Input.NewDispatchObserved(ct.IsCancellationRequested);

            case var c when c.IsSnapshotConfig:
                lock (_gate)
                {
                    config = _config;
                    _configChanged = false;
                    _connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    connCt = _connectionCts.Token;
                }
                return HostMachine.Input.NewConfigSnapshotted(config.Password.Length > 0);

            case var c when c.IsCreateClient:
                client = _clientFactory();
                return HostMachine.Input.EffectOk;

            case var c when c.IsConnect:
                await client!.ConnectAsync(config.Address, config.Port, connCt).ConfigureAwait(false);
                return HostMachine.Input.EffectOk;

            case var c when c.IsAuthorize:
                return HostMachine.Input.NewAuthResult(
                    await client!.AuthorizeAsync(config.Password, connCt).ConfigureAwait(false));

            case var c when c.IsFetchVersionAndState:
                fetchedVersion = await client!.ExchangeVersionsAsync(connCt).ConfigureAwait(false);
                ccState = await client.GetStateAsync(connCt).ConfigureAwait(false);
                return HostMachine.Input.FetchOk;

            case HostMachine.Command.PublishStatus ps:
                if (ps.stampDaemonVersion)
                    _daemonVersion = fetchedVersion;
                SetStatus((HostConnectionState)ps.status, ps.attempt,
                          ps.backoff is { } b && FSharpOption<TimeSpan>.get_IsSome(b) ... );
                // NOTE: F# option interop — write a tiny static helper:
                //   static T? FromOption<T>(FSharpOption<T>? o) where T : struct
                // and use it for backoff/error. NextAttemptAt = _time.GetUtcNow() + delay.
                return HostMachine.Input.EffectOk;

            case HostMachine.Command.RunTickRpcs t:
                ccStatus = await client!.GetCcStatusAsync(connCt).ConfigureAwait(false);
                results = await client.GetResultsAsync(ct: connCt).ConfigureAwait(false);
                transfers = await client.GetFileTransfersAsync(connCt).ConfigureAwait(false);
                newMessages = await client.GetMessagesAsync(t.lastSeqno, connCt).ConfigureAwait(false);
                HashSet<string> known = [.. ccState!.Workunits.Select(w => w.Name)];
                return HostMachine.Input.NewTickFetched(new HostMachine.TickInfo(
                    maxSeqno: newMessages.Count > 0
                        ? FSharpOption<int>.Some(newMessages.Max(m => m.Seqno))
                        : FSharpOption<int>.None,
                    hasUnknownWorkunit: results.Any(r => !known.Contains(r.WorkunitName))));

            case HostMachine.Command.PublishMessages pm:
                if (pm.replaceLog)
                {
                    _messages.ReplaceAll(newMessages);
                    if (newMessages.Count > 0)
                        RaiseSafe(MessagesAdded, new MessagesAddedEventArgs(HostId, newMessages));
                }
                else if (newMessages.Count > 0)
                {
                    _messages.Append(newMessages);
                    RaiseSafe(MessagesAdded, new MessagesAddedEventArgs(HostId, newMessages));
                }
                return HostMachine.Input.EffectOk;

            case var c when c.IsRefetchState:
                ccState = await client!.GetStateAsync(connCt).ConfigureAwait(false);
                return HostMachine.Input.EffectOk;

            case var c when c.IsBuildSnapshot:
                builtSnapshot = SnapshotBuilder.Build(
                    HostId, config.DisplayName, _time.GetUtcNow(),
                    ccState!, ccStatus!, results, transfers);
                return HostMachine.Input.EffectOk;

            case var c when c.IsPublishSnapshot:
                Snapshot = builtSnapshot;
                RaiseSafe(SnapshotUpdated, builtSnapshot!);
                return HostMachine.Input.EffectOk;

            case var c when c.IsObserveConfigChanged:
                return HostMachine.Input.NewGuardObserved(_configChanged);

            case var c when c.IsObserveTickGuard:
                connCt.ThrowIfCancellationRequested();
                return HostMachine.Input.NewGuardObserved(_configChanged);

            case var c when c.IsWaitPollInterval:
                await WaitAsync(TimeSpan.FromSeconds(_pollingIntervalSeconds), ct).ConfigureAwait(false);
                return HostMachine.Input.WaitEnded;

            case HostMachine.Command.WaitBackoff wb:
                await WaitAsync(wb.Item, ct).ConfigureAwait(false);
                return HostMachine.Input.WaitEnded;

            case var c when c.IsParkForConfigChange:
                await WaitForConfigChangeAsync(ct).ConfigureAwait(false);
                return HostMachine.Input.WaitEnded;

            case var c when c.IsDisposeConnectionCts:
                lock (_gate)
                {
                    _connectionCts?.Dispose();
                    _connectionCts = null;
                }
                return HostMachine.Input.EffectOk;

            case var c when c.IsDisposeClient:
                if (client is not null)
                {
                    try { await client.DisposeAsync().ConfigureAwait(false); }
                    catch { /* ignored: dispose failures do not affect the state machine */ }
                    client = null;
                }
                return HostMachine.Input.EffectOk;

            default: // ExitLoop handled by the driver
                return HostMachine.Input.EffectOk;
        }
    }

    try
    {
        while (true)
        {
            var (next, commands) = HostMachine.step(state, input);   // F# tuple interop
            state = next;
            input = HostMachine.Input.EffectOk;    // default when the batch has no request
            bool exit = false;
            foreach (var cmd in commands)
            {
                if (cmd.IsExitLoop) { exit = true; break; }
                try
                {
                    input = await ExecuteAsync(cmd).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    input = Classify(ex, ct);
                    break;                          // skip the rest of the batch
                }
            }
            if (exit)
                break;
        }
    }
    finally
    {
        // Defense in depth, NOT decision logic: the core always commands teardown
        // before waits (verified: I2), so these are no-ops on every verified path.
        // They exist so an unexpected shell exception can never leak a client —
        // BOINC daemons allow very few concurrent GUI RPC connections.
        lock (_gate)
        {
            _connectionCts?.Dispose();
            _connectionCts = null;
        }
        if (client is not null)
        {
            try { await client.DisposeAsync().ConfigureAwait(false); }
            catch { /* ignored */ }
        }
    }
}

private static HostMachine.Input Classify(Exception ex, CancellationToken outerCt) => ex switch
{
    OperationCanceledException when outerCt.IsCancellationRequested =>
        HostMachine.Input.NewFaulted(HostMachine.FailureKind.Disposal),
    OperationCanceledException =>
        HostMachine.Input.NewFaulted(HostMachine.FailureKind.ConnCanceled),
    BoincUnauthorizedException u =>
        HostMachine.Input.NewFaulted(HostMachine.FailureKind.NewUnauthorized(u.Message)),
    _ => HostMachine.Input.NewFaulted(HostMachine.FailureKind.NewFailure(ex.Message)),
};
```

Interop notes for the implementer:
- F# DU case testing from C#: compiler generates `IsProbe`/`IsPublishStatus` bool properties and nested case classes with lowercase field names as properties (`p.point`, `ps.attempt`). Verify actual generated names with one quick compile; adjust patterns accordingly.
- `HostMachine.step` returns `Tuple<HostMachine.State, FSharpList<HostMachine.Command>>`; `FSharpList<T>` is `IEnumerable<T>`.
- Give `HostMachine.TickInfo` construction its generated ctor. `FSharpOption<int>` via `FSharpOption<int>.Some(...)` / `.None`.
- If interop friction gets ugly, add small `[<CompiledName>]`/helper members on the F# side rather than reflection tricks on the C# side — but do NOT change the machine's semantics.
- Preserve the top-of-file doc comments that explain the architecture; rewrite them to describe the interpreter contract (the block from `HostMachine.fs` may be summarized, pointing there as authoritative).

- [ ] **Step 2: Build and run the FULL suite (the parity gate)**

Run: `dotnet build Lattice.sln -c Release -warnaserror && dotnet test Lattice.sln -c Release --no-build 2>&1 | tail -20`
Expected: 193 C# + 31 F# all green, **zero modifications under tests/**. Failures here are parity bugs: fix the shell/core against the d3950c2 reference; NEVER adjust a test. If a test seems to demand a semantic change, STOP and report.

- [ ] **Step 3: Verify no test files changed**

Run: `git status --short tests/Lattice.Tests`
Expected: empty output.

Do NOT commit yet — Task 5 rides in the same commit (verification sync rule).

---

## Task 5: Documentation sync + the combined commit

**Files:**
- Modify: `verification/README.md` (§1 table, §2 rules, §3 inventory, §7)
- Modify: `verification/HostMonitor.pml` (comments only)
- Modify: `CLAUDE.md` (solution structure + verification sync rule note)
- Modify: `.superpowers/sdd/progress.md` (ledger)

- [ ] **Step 1: README §1** — F# row becomes: "F# shell model + exhaustive explorer driving the PRODUCTION `HostMachine.step` (`src/Lattice.Core.Machine`)"; add a sentence: the design layer now executes the real decision core, so model-code drift is possible only in the shell, which the harness layer pins.

- [ ] **Step 2: README §2 correspondence rules** — rule 1 now binds `HostMachine.Command.SnapshotConfig` to exactly one `lock (_gate)` block in the shell's interpreter; rule 2 unchanged in force, inventory below rewritten; rule 3 unchanged (probe points are now emitted by the core as `Probe` commands — a new shared-state touch needs a new `ProbePoints` literal + core emission + `InterleavePoints` alias).

- [ ] **Step 3: README §3 inventory rewrite** — one row per current `HostMonitor` field. Fields and regimes are UNCHANGED from the pre-rewrite table except: `_daemonVersion` row's "written only in `RunAttemptAsync`" becomes "written only by the interpreter's `PublishStatus(stampDaemonVersion=true)` execution, on the loop thread"; add one row: the interpreter's attempt-scoped locals (`client`, `connCt`, `fetchedVersion`, tick payloads, `builtSnapshot`) — single-writer loop-confined method locals, structurally incapable of cross-thread exposure (the `_lastSeqno` deletion note generalizes: per-connection state now lives in `HostMachine.State` + interpreter locals, not fields); note `HostMachine.State state` itself is a loop-local: modeled — it IS the core the F# layer explores. Update the `_lastSeqno` note's last sentence to mention `HostMachine.State.lastSeqno`.

- [ ] **Step 4: README §7** — after the verbatim CLAUDE.md quote, add: "Since the functional-core restructure (2026-07-08), the F#-spec leg of this rule is discharged by construction for decision logic: the spec executes `HostMachine.step` itself. Changes to the C# shell (locks, waits, probe placement, event raising) still owe wrapper-model and probe-list updates; changes to `HostMachine` are automatically covered but still owe a Promela update when they change the protocol."

- [ ] **Step 5: pml comment touch-ups** — update the header comment ("Independent second encoding of the F# executable spec" → "of the production HostMachine core (which the F# layer executes directly)"); fix the four `HostMonitor.cs:NNN` line references (WaitAsync entry-consume ×2, WaitForConfigChange entry ×1, RunAsync dispatcher ×1) to the new locations or replace with method-name references (preferred: "HostMonitor.cs WaitAsync entry-consume" — line numbers rot). NO semantic edits; also note near the top that the pml abstains from the post-build recheck (PostBuildGuard) — it models the three load-bearing guards; the recheck is polish (see the old d3950c2:633-638 comment, now in HostMachine).

- [ ] **Step 6: CLAUDE.md** — solution structure block gains:

```
├── Lattice.Core.Machine/    # Pure F# decision core for HostMonitor (HostMachine.step). No I/O, no deps.
```

and under "Verification sync rule" append one sentence: "The F# executable spec executes the production `HostMachine.step` directly (functional-core restructure 2026-07-08); the spec-sync leg is by-construction for decision logic, still manual for shell changes."

- [ ] **Step 7: Ledger** — append task completion lines to `.superpowers/sdd/progress.md` (tasks 1–5, commits, state count, judgment calls encountered).

- [ ] **Step 8: Full gate**

Run: `dotnet build Lattice.sln -c Release -warnaserror && dotnet test Lattice.sln -c Release --no-build 2>&1 | tail -5`
Expected: all green.

- [ ] **Step 9: The combined commit (Tasks 4+5)**

```bash
git add -A
git commit -m "refactor(core): HostMonitor as interpreter shell over HostMachine

The C# class keeps its public surface, fields, protection regimes,
shell primitives (wake latch, WaitAsync/WaitForConfigChangeAsync,
RaiseSafe/SetStatus) and all 15 probe points, but every routing decision
now comes from the pure HostMachine.step the verifier explores.
RunAttemptAsync/PollAsync/TickAsync and the HostAuthException/
HostSessionLostException routing signals dissolve into the interpreter.

Verification sync (CLAUDE.md rule, maximal case): the F# spec already
executes this exact core (previous commit); README correspondence rules,
shared-state inventory, and probe-point notes updated in this commit.
Promela model: comments only — the protocol is semantically unchanged,
which is the entire point of the restructure; pml stays the independent
N-version double-check of the same protocol.

Parity oracle: tests/Lattice.Tests untouched (193 tests incl. the
45-case interleaving sweep, lifecycle, and reentrancy pins) and green.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

## Task 6: Final verification, push, PR

- [ ] **Step 1: verification-before-completion** — full clean build + test from scratch (`dotnet build Lattice.sln -c Release -warnaserror && dotnet test Lattice.sln -c Release --no-build`); `git status` clean; skim `git log --oneline main..HEAD` for message quality.
- [ ] **Step 2: Push branch, open PR** against `main`. PR body: spec link, the three behavior-preservation arguments (untouched parity oracle, model-executes-production-core, pml agreement), the judgment-calls list from spec §9 for user review, state-count note, and the ride-along files note. End with the standard generation footer.
- [ ] **Step 3: Request Codex review; WAIT for the result** (memory: wait-for-codex-review). Do not merge before Codex posts and all 4 required checks are green. Address findings via superpowers:receiving-code-review.

---

## Self-review notes (controller)

- Spec coverage: §4.1→Task 1/2, §4.2→Task 2, §4.3→Task 4, §4.4→Task 3, §4.5→Task 5, §5 commits→Tasks 1–5 (spec's "commit 2" split into Tasks 1–3's three commits — all green, sync rule unaffected), §6 gates→every task, §2 frozen surfaces→Task 4 KEEP list.
- Type consistency: `TickInfo.maxSeqno`/`hasUnknownWorkunit` used consistently in pins/wrapper/shell; `Outcome` constructors `ODisposal/OConfigChanged/OAuthFailed/OFailed` consistent across Tasks 2–3; probe names identical to the existing 15.
- Known intentional deltas vs the old model (documented, not drift): wrapper explores hasPassword, silent re-auth, and refetch branches (new coverage); PostBuildGuard modeled in F# (executes the core) but not in pml (documented in Task 5 Step 5).
