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
/// Conceptual model: this is a pure Mealy machine — an immutable State value plus
/// a total transition function. It executes nothing and holds no resources;
/// "Machine" names the mathematical object, not a runtime engine. Phase enumerates
/// the protocol's interleaving points (the former C# async state machine's
/// continuation points), which is deliberately finer than the user-facing 7-value
/// HostConnectionState lifecycle: guard-adjacency properties (I1) live below the
/// lifecycle's granularity. The projection back onto the lifecycle is pinned by
/// verification property I6 — once a phase's entry batch has drained its publish:
///   Connecting → Connecting; Authorizing → Authorizing;
///   Fetching/AcceptGuard → FetchingState; tick pipeline → Connected;
///   BackoffWaiting/PostBackoffObserve → Retrying; Parked → AuthFailed;
///   Exited → Disconnected; Dispatch/SnapshotWait/TearingDown → trajectory-
///   dependent (they surface the previous publish by design).
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

    /// Mid-poll unauthorized (PollAsync's catch, d3950c2:548-565) — reachable from
    /// BOTH tick RPC phases: TickAwait's batch AND Refetching's get_state, which sits
    /// inside TickAsync's try (d3950c2:619) so PollAsync's catch covers it. One silent
    /// re-auth per successful tick; a second consecutive unauthorized escalates as a
    /// PLAIN failure (the former HostSessionLostException); no password -> park.
    let private unauthorizedMidPoll (s: State) (m: string) : State * Command list =
        if s.reauthedSinceLastSuccess then teardown s (OFailed(m, s.reachedConnected))
        elif not s.hasPassword then teardown s (OAuthFailed refusedPassword)
        else { s with phase = Reauthorizing }, [ Authorize ]

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

        | TickAwait, Faulted (Unauthorized m) -> unauthorizedMidPoll s m

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
                // cursor/firstTick deliberately NOT updated here: they belong to the
                // tick-completion triple at PostBuildGuard (d3950c2:544-546)
                { s with phase = (if info.hasUnknownWorkunit then Refetching else SnapGuard) },
                [ yield Probe ProbePoints.TickBeforeMsgPublish
                  yield PublishMessages s.firstTick     // replace on first tick
                  if info.hasUnknownWorkunit then
                      yield RefetchState
                  else
                      yield Probe ProbePoints.TickBeforeSnapGuard
                      yield ObserveTickGuard ]

        | Refetching, EffectOk ->
            { s with phase = SnapGuard },
            [ Probe ProbePoints.TickBeforeSnapGuard; ObserveTickGuard ]
        // the refetch get_state sits INSIDE TickAsync's try: an unauthorized here
        // routes through the same silent-re-auth/escalate fold as TickAwait's
        | Refetching, Faulted (Unauthorized m) -> unauthorizedMidPoll s m

        | SnapGuard, GuardObserved true -> teardown s OConfigChanged
        | SnapGuard, GuardObserved false ->
            { s with phase = PostBuildGuard },
            [ Probe ProbePoints.TickBeforeBuild; BuildSnapshot; ObserveTickGuard ]

        | PostBuildGuard, GuardObserved true -> teardown s OConfigChanged
        | PostBuildGuard, GuardObserved false ->
            // Tick completed normally: the completion triple — message cursor,
            // firstTick, silent re-auth allowance — updates HERE (d3950c2:544-546),
            // deliberately NOT at MsgGuard: a re-auth retry after a refetch fault
            // must replay with the old cursor and the same replaceLog decision.
            { s with phase = PollObserve
                     lastSeqno = (match s.tick with
                                  | Some i -> defaultArg i.maxSeqno s.lastSeqno
                                  | None -> s.lastSeqno)
                     firstTick = false
                     reauthedSinceLastSuccess = false },
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
        | (SnapshotWait | Connecting | Authorizing | Fetching | AcceptGuard | TickAwait | Reauthorizing | MsgGuard | Refetching | SnapGuard | PostBuildGuard | PollObserve | PollWaiting | PostWaitObserve), Faulted k ->
            faultInAttempt s k
        // Post-teardown waits: only disposal can cancel them; client already closed
        // (d3950c2:377-378, 393-394 catch OperationCanceledException -> break).
        | (BackoffWaiting | PostBackoffObserve | Parked | TearingDown _ | Dispatch), Faulted _ ->
            exitLoop s

        | Exited, _ -> s, [ ExitLoop ]

        // Unexpected (phase, input) pairing = interpreter bug. The loop task must
        // never fault (I5/A5), so settle in Disconnected rather than throw.
        | _, _ -> exitLoop s
