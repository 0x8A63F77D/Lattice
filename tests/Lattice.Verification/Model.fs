/// Executable specification of HostMonitor's concurrency protocol. PRIMARY
/// design-level check. This module NO LONGER re-encodes the protocol: it is a
/// model of the C# SHELL wrapped around the PRODUCTION decision core
/// (`Lattice.Core.HostMachine.step`). Decision-logic drift between the model and
/// the implementation is therefore structurally impossible — the routing,
/// phase sequencing, attempt counting and auth handling are the real code.
///
/// Primitive anchoring (spec §"Primitive-semantics anchoring"):
///  - one loop MICRO-step = execution of ONE Command from the current batch
///    (fire-and-forget commands apply their side effect; the trailing request
///    command's result is fed back as the next Input; a batch with no request
///    yields EffectOk — the interpreter contract in HostMachine.fs). Env actions
///    may interleave between ANY two micro-steps — shared-memory threads
///    interleave anywhere.
///  - the atomic snapshot lock block is a SINGLE micro-step (SnapshotConfig).
///  - env actions (Start/UpdateConfig/Wake/Dispose) fire between any two steps.
///  - TCS wake = one-shot latch (sticky bit); CTS = monotonic cancel bit +
///    Disposed fault state; volatile flags = plain bits under the SC note in
///    verification/README.md §5.
///  - the DECISION CORE is EXECUTED, not modeled: `feed` runs HostMachine.step
///    and re-queues its batch (Probe commands filtered — they are no-ops with no
///    state change, and two adjacent env windows separated by a no-op are
///    equivalent, so filtering loses no interleavings).
module Lattice.Verification.Model

open Lattice.Core
open Lattice.Core.HostMachine

/// Per-attempt linked CTS state (None → Live → Canceled; disposed back to None).
type CtsState = CtsNone | CtsLive | CtsCanceled | CtsDisposed
/// Publication state of the loop task (Start stores it; DisposeAsync awaits it).
type LoopTask = TaskInitial | TaskStored | TaskRunning | TaskFaulted | TaskDone
/// Which shell wait the loop is currently blocked in.
type WaitKind = WPoll | WBackoff | WPark

/// Environment budgets keep the state space finite (spec bounds).
type Bounds = { updates: int; wakes: int; failures: int; disposes: int }

type S = {
    // ---- gate-protected / volatile shell state ----
    curVersion: int              // _config generation; UpdateConfig increments
    configChanged: bool          // volatile _configChanged
    cts: CtsState                // per-attempt linked _connectionCts
    wake: bool                   // sticky wake latch (_wake TCS completed?)
    started: bool
    loopTask: LoopTask
    outerCanceled: bool          // _cts.Cancel() happened (DisposeAsync)
    outerDisposed: bool          // _cts.Dispose() happened (end of DisposeAsync)
    disposeFlag: bool            // rider C: _disposed idempotency flag
    // ---- the interpreter ----
    core: HostMachine.State      // the PRODUCTION decision core's state
    queue: Command list          // rest of the current batch (Probe filtered out)
    waiting: WaitKind option     // loop blocked in a shell wait
    // ---- attempt bookkeeping the shell owns ----
    attemptVersion: int          // config generation the last SnapshotConfig captured
    connLive: bool               // client connection open
    injectedFail: bool           // failure budget consumed by this attempt
    // ---- observables the properties read ----
    statusState: HostConnectionState
    statusVersion: int
    daemonVersionVintage: int option  // stamp left by the CURRENT attempt (reset at
                                      // SnapshotConfig); I1m: did THIS attempt stamp pre-accept
    logVintage: int option            // same per-attempt stamp discipline for the log
    logReplacedThisConn: bool
    faulted: bool                     // I5 violation marker: any disposed-resource fault
    // ---- env budgets ----
    updatesLeft: int; wakesLeft: int; failsLeft: int; disposesLeft: int
}

let initial (b: Bounds) = {
    curVersion = 0; configChanged = false; cts = CtsNone; wake = false
    started = false; loopTask = TaskInitial
    outerCanceled = false; outerDisposed = false; disposeFlag = false
    core = HostMachine.initial; queue = []; waiting = None
    attemptVersion = -1; connLive = false; injectedFail = false
    statusState = HostConnectionState.Disconnected; statusVersion = 0
    daemonVersionVintage = None; logVintage = None; logReplacedThisConn = false
    faulted = false
    updatesLeft = b.updates; wakesLeft = b.wakes; failsLeft = b.failures; disposesLeft = b.disposes
}

type Action =
    // environment (any interleaving point)
    | EnvStart
    | EnvUpdateConfig
    | EnvWake                    // wake action: RequestRefresh / SetPollingInterval's Wake
                                 // (NOT SetPollingIntervalQuiet — #92's non-waking setter)
    | EnvDispose
    // loop micro-steps
    | ExecCmd                    // execute head command / feed the next input
    | ExecCmdFail                // failure-injected variant of an RPC request
    | ExecAuthRefused            // AuthResult false branch of Authorize
    | ExecUnauthorized           // BoincUnauthorized branch of RunTickRpcs / RefetchState
    | DelayFires                 // a wait's timer completes
    | WakeConsumed               // a wait observes the sticky wake

/// Run the production step; enqueue its batch with Probe commands filtered out.
/// Kept module-visible (not private) so the mutant decorators in Properties.fs
/// can splice a fed transition into their defective encodings.
let feed (input: Input) (s: S) : S =
    let core', cmds = HostMachine.step s.core input
    { s with core = core'
             queue = cmds |> List.filter (function Probe _ -> false | _ -> true) }

/// Which actions are enabled in s. The explorer expands all of them (that IS the
/// nondeterministic interleaving).
let enabled (s: S) : Action list =
    let env =
        [ if not s.started then EnvStart                      // Start only when not started
          if s.updatesLeft > 0 then EnvUpdateConfig
          if s.wakesLeft > 0 then EnvWake
          if s.disposesLeft > 0 then EnvDispose ]
    let loop =
        if not s.started || s.faulted || s.core.phase = Exited then []
        else
            match s.waiting with
            | Some w ->
                [ if w <> WPark then DelayFires
                  if s.wake then WakeConsumed
                  if s.outerCanceled || (w = WPark && s.configChanged) then ExecCmd ]
            | None ->
                match s.queue with
                | [] -> [ ExecCmd ]                           // feeds Started (initial kick only)
                | cmd :: _ ->
                    let isRpc =
                        match cmd with
                        | Connect | Authorize | FetchVersionAndState | RunTickRpcs _ | RefetchState -> true
                        | _ -> false
                    let isAuth = match cmd with Authorize -> true | _ -> false
                    let isTickOrRefetch = match cmd with RunTickRpcs _ | RefetchState -> true | _ -> false
                    let canInject = s.failsLeft > 0 && not s.injectedFail
                    [ ExecCmd
                      if isRpc && canInject then ExecCmdFail
                      if isAuth then ExecAuthRefused
                      if isTickOrRefetch && canInject then ExecUnauthorized ]
    env @ loop |> List.distinct

/// A fire-and-forget command's successor: if it was the trailing command of the
/// batch (no request follows), the interpreter feeds EffectOk (batch-with-no-
/// request contract, HostMachine.fs:50); otherwise the batch simply advances.
let private fireForget (s'': S) : S list =
    if List.isEmpty s''.queue then [ feed EffectOk s'' ] else [ s'' ]

/// Execute the head command `cmd` (rest = the tail of the batch).
/// Cancel classification follows the interpreter contract (HostMachine.fs:52-54):
/// the OUTER token wins — a canceled outer token is Disposal, any other cancel is
/// ConnCanceled. (The design table listed these in the opposite order; outer-first
/// is what the shell actually does and what L3 requires.)
let private execCommand (s: S) (cmd: Command) (rest: Command list) : S list =
    let s' = { s with queue = rest }
    let cancelCheck (onOk: unit -> S list) : S list =
        if s.outerCanceled then [ feed (Faulted Disposal) s' ]
        elif s.cts = CtsCanceled then [ feed (Faulted ConnCanceled) s' ]
        else onOk ()
    match cmd with
    | ObserveDispatch -> [ feed (DispatchObserved s.outerCanceled) s' ]
    | SnapshotConfig ->
        // THE one atomic lock block: snapshot config, clear the flag, create the
        // linked CTS, reset the per-attempt observables — all in one micro-step.
        // hasPassword is nondeterministic (2 successors).
        [ for hp in [ true; false ] ->
            feed (ConfigSnapshotted hp)
                 { s' with attemptVersion = s.curVersion; configChanged = false
                           cts = CtsLive; injectedFail = false
                           logReplacedThisConn = false
                           daemonVersionVintage = None; logVintage = None } ]
    | CreateClient -> fireForget s'
    | Connect -> cancelCheck (fun () -> [ feed EffectOk { s' with connLive = true } ])
    | Authorize -> cancelCheck (fun () -> [ feed (AuthResult true) s' ])
    | FetchVersionAndState -> cancelCheck (fun () -> [ feed FetchOk s' ])
    | PublishStatus(st, _, _, _, stamp) ->
        fireForget { s' with statusState = st; statusVersion = s.attemptVersion
                             daemonVersionVintage =
                                 (if stamp then Some s.attemptVersion else s.daemonVersionVintage) }
    | RunTickRpcs _ ->
        cancelCheck (fun () ->
            [ for m in [ None; Some 1 ] do
                for w in [ false; true ] do
                    yield feed (TickFetched { maxSeqno = m; hasUnknownWorkunit = w }) s' ])
    | PublishMessages replace ->
        fireForget { s' with logVintage = Some s.attemptVersion
                             logReplacedThisConn = s.logReplacedThisConn || replace }
    | RefetchState -> cancelCheck (fun () -> [ feed EffectOk s' ])
    | BuildSnapshot -> fireForget s'
    | PublishSnapshot -> fireForget s'                        // vintage rides statusVersion
    | ObserveConfigChanged -> [ feed (GuardObserved s.configChanged) s' ]
    | ObserveTickGuard -> cancelCheck (fun () -> [ feed (GuardObserved s.configChanged) s' ])
    | WaitPollInterval ->
        // WaitAsync entry (HostMonitor.cs:250-256): a completed latch returns
        // immediately (consumed); otherwise the loop blocks in the wait.
        if s.wake then [ feed WaitEnded { s' with wake = false } ]
        else [ { s' with waiting = Some WPoll } ]
    | WaitBackoff _ ->
        if s.wake then [ feed WaitEnded { s' with wake = false } ]
        else [ { s' with waiting = Some WBackoff } ]
    | ParkForConfigChange ->
        // WaitForConfigChangeAsync (HostMonitor.cs:274-292): if the flag is already
        // set, return WITHOUT consuming the latch; else consume a stale wake at
        // entry and block until a config change (or disposal).
        if s.configChanged then [ feed WaitEnded s' ]
        elif s.wake then [ { s' with wake = false; waiting = Some WPark } ]
        else [ { s' with waiting = Some WPark } ]
    | DisposeConnectionCts -> fireForget { s' with cts = CtsNone }
    | DisposeClient -> fireForget { s' with connLive = false }
    | ExitLoop -> [ { s' with loopTask = TaskDone; outerDisposed = true; waiting = None; queue = [] } ]
    | Probe _ -> [ s' ]                                       // unreachable (filtered); defensive

let step (s: S) (a: Action) : S list =
    match a with
    // ---------------- environment ----------------
    | EnvStart ->
        // Rider C: lock { if started||disposeFlag return; started=true;
        //   token=_cts.Token (safe pre-cancel under the lock); store loop task }
        if s.started || s.disposeFlag then [ s ]              // idempotent no-op
        elif s.outerDisposed then
            // pre-rider defect shape: token read after dispose → faulted task.
            // TARGET: disposeFlag is always set before outerDisposed (EnvDispose),
            // so this branch is unreachable; kept as a fault so a broken EnvDispose
            // encoding cannot hide it (mutant M5 relies on this).
            [ { s with started = true; loopTask = TaskFaulted; faulted = true } ]
        else
            [ { s with started = true; loopTask = TaskStored } ]  // first ExecCmd feeds Started
    | EnvUpdateConfig ->
        // lock { _config=new; _configChanged=true; _connectionCts?.Cancel() } ; Wake()
        let cts' = if s.cts = CtsLive then CtsCanceled else s.cts
        [ { s with curVersion = s.curVersion + 1; configChanged = true; cts = cts'
                   wake = true; updatesLeft = s.updatesLeft - 1 } ]
    | EnvWake ->
        [ { s with wake = true; wakesLeft = s.wakesLeft - 1 } ]
    | EnvDispose ->
        // Rider C: lock { if disposeFlag → (second call: await loop only) ; disposeFlag=true }
        // then Cancel+Wake, await loop, Dispose(_cts). disposeFlag+outerCanceled set
        // atomically here; outerDisposed is set immediately only when the loop never
        // started / already exited (DisposeAsync completes without awaiting a live loop).
        if s.disposeFlag then [ { s with disposesLeft = s.disposesLeft - 1 } ]  // idempotent
        else
            [ { s with disposeFlag = true; outerCanceled = true; wake = true
                       disposesLeft = s.disposesLeft - 1
                       cts = (if s.cts = CtsLive then CtsCanceled else s.cts)
                       outerDisposed = (s.loopTask = TaskInitial || not s.started
                                        || s.core.phase = Exited) } ]

    // ---------------- shell waits ----------------
    | DelayFires ->
        // Sticky latch: the delay CAN win WhenAny while the wake is completed; the
        // latch survives and the next wait ENTRY consumes it.
        match s.waiting with
        | Some w when w <> WPark -> [ { s with waiting = None } |> feed WaitEnded ]
        | _ -> [ s ]
    | WakeConsumed ->
        // WaitAsync: lock { if wake.completed { renew; return true } } — consumes the latch.
        match s.waiting with
        | Some WPoll | Some WBackoff -> [ { s with wake = false; waiting = None } |> feed WaitEnded ]
        | Some WPark ->
            // WaitForConfigChangeAsync: stale wakes are consumed and ignored; only a
            // config change (or disposal) releases the park.
            if s.configChanged then [ { s with wake = false; waiting = None } |> feed WaitEnded ]
            else [ { s with wake = false } ]
        | None -> [ s ]

    // ---------------- loop micro-steps ----------------
    | ExecCmd ->
        match s.waiting with
        | Some w ->
            // wait aborted by disposal, or a park released by a pending config change
            if s.outerCanceled then [ { s with waiting = None } |> feed (Faulted Disposal) ]
            elif w = WPark && s.configChanged then [ { s with waiting = None } |> feed WaitEnded ]
            else [ s ]
        | None ->
            match s.queue with
            | [] ->
                // Empty queue: the very first micro-step feeds Started (Dispatch awaits
                // it); the only other phase reachable with an empty queue is Exited,
                // which disables loop actions. (Fire-forget-terminal batches feed
                // EffectOk at execution time, so they never leave an empty queue here.)
                if s.core.phase = Dispatch then [ feed Started s ] else [ feed EffectOk s ]
            | cmd :: rest -> execCommand s cmd rest

    | ExecCmdFail ->
        match s.queue with
        | _ :: rest ->
            [ feed (Faulted (Failure "err"))
                   { s with queue = rest; injectedFail = true; failsLeft = s.failsLeft - 1 } ]
        | [] -> [ s ]
    | ExecAuthRefused ->
        match s.queue with
        | _ :: rest -> [ feed (AuthResult false) { s with queue = rest } ]
        | [] -> [ s ]
    | ExecUnauthorized ->
        match s.queue with
        | _ :: rest ->
            [ feed (Faulted (Unauthorized "err"))
                   { s with queue = rest; injectedFail = true; failsLeft = s.failsLeft - 1 } ]
        | [] -> [ s ]
