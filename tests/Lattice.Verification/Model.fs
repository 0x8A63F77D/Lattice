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
