module Lattice.Verification.Properties

open Xunit
open Lattice.Core
open Lattice.Core.HostMachine
open Lattice.Verification
open Lattice.Verification.Model

let bounds = { updates = 2; wakes = 2; failures = 3; disposes = 2 }

[<Fact>]
let ``exploration terminates and reaches Connected`` () =
    let r = Explorer.explore Model.step (Model.initial bounds)
    Assert.True(r.states.Count > 100)
    Assert.Contains(r.states, fun s -> s.statusState = HostConnectionState.Connected)

let reach = lazy (Explorer.explore Model.step (Model.initial bounds))

let private isLoopAction = function
    | ExecCmd | ExecCmdFail | ExecAuthRefused | ExecUnauthorized
    | DelayFires | WakeConsumed -> true
    | EnvStart | EnvUpdateConfig | EnvWake | EnvDispose -> false

// I1 publish half: a guard that observed a pending config change never proceeds to
// the publish it gates. The gated publish is determined by the core phase the guard
// command runs in (AcceptGuard→Connected, MsgGuard→messages, Snap/PostBuild→snapshot).
[<Fact>]
let ``I1 guards bar publishes when a config change is pending`` () =
    let r = reach.Value
    for KeyValue(s, outs) in r.edges do
        if s.waiting = None && s.configChanged then
            match s.queue with
            | (ObserveConfigChanged | ObserveTickGuard) :: _ ->
                let paired : (Command -> bool) option =
                    match s.core.phase with
                    | AcceptGuard ->
                        Some (function
                            | PublishStatus(HostConnectionState.Connected, _, _, _, _) -> true
                            | _ -> false)
                    | MsgGuard -> Some (function PublishMessages _ -> true | _ -> false)
                    | SnapGuard | PostBuildGuard -> Some (function PublishSnapshot -> true | _ -> false)
                    | _ -> None                               // Poll/PostWait/PostBackoff: no publish
                match paired with
                | Some isPub ->
                    for (a, s2) in outs do
                        if a = ExecCmd && (s2.queue |> List.exists isPub) then
                            failwithf "%A ignored configChanged. %s" s.core.phase (Explorer.trace r s2)
                | None -> ()
            | _ -> ()

// I1 mutation half: an attempt that has not reached Connected must not have stamped
// ITS OWN version into daemonVersion or the message log.
[<Fact>]
let ``I1 no pre-accept mutation of daemonVersion or message log`` () =
    Explorer.checkInvariant reach.Value "I1-mutation" (fun s ->
        s.core.reachedConnected
        || (s.daemonVersionVintage <> Some s.attemptVersion
            && s.logVintage <> Some s.attemptVersion)
        || s.attemptVersion < 0)

// I2: no connection live during backoff/park/exit paths.
[<Fact>]
let ``I2 connection closed during backoff and park`` () =
    Explorer.checkInvariant reach.Value "I2" (fun s ->
        match s.core.phase with
        | BackoffWaiting | PostBackoffObserve | Parked | Exited -> not s.connLive
        | _ -> true)

// I3: no unabortable stale attempt (round-7's property).
[<Fact>]
let ``I3 no unabortable stale attempt`` () =
    Explorer.checkInvariant reach.Value "I3" (fun s ->
        s.attemptVersion = s.curVersion
        || s.configChanged
        || s.cts = CtsCanceled
        || not s.started
        || (match s.core.phase with
            | Dispatch | SnapshotWait | TearingDown _ | BackoffWaiting
            | PostBackoffObserve | Parked | Exited -> true
            | _ -> false))

// I4: Retrying after a post-Connected failure carries attempt = 1 (round-6's property).
// Anchor: the edge where teardown's last command runs (head DisposeClient, empty rest)
// and the interpreter feeds EffectOk → the OFailed(_, reached=true) routing.
[<Fact>]
let ``I4 attempt counter resets after a connected session`` () =
    let r = reach.Value
    for KeyValue(s, outs) in r.edges do
        match s.core.phase, s.queue with
        | TearingDown (OFailed(_, true)), [ DisposeClient ] ->
            for (a, s2) in outs do
                if a = ExecCmd && s2.core.phase = BackoffWaiting then
                    if s2.core.attempt <> 1 then
                        failwithf "I4: attempt=%d after connected session. %s"
                            s2.core.attempt (Explorer.trace r s2)
                    let badPublish =
                        s2.queue |> List.exists (function
                            | PublishStatus(HostConnectionState.Retrying, n, _, _, _) -> n <> 1
                            | _ -> false)
                    if badPublish then
                        failwithf "I4: Retrying publish carries attempt<>1. %s" (Explorer.trace r s2)
        | _ -> ()

// I5: no reachable disposed-resource fault; lifecycle idempotency by construction.
[<Fact>]
let ``I5 lifecycle safety - no disposed-resource faults`` () =
    Explorer.checkInvariant reach.Value "I5" (fun s -> not s.faulted)

// I6: the machine, projected onto the published status, IS the 7-value connection
// lifecycle (HostConnectionState). Once a phase's entry batch has drained its
// publish, the published status is determined by the phase. Trajectory-dependent
// phases (Dispatch, SnapshotWait, TearingDown) are unconstrained: they surface the
// previous publish by design (e.g. AuthFailed stays visible through the reconnect
// dispatch until Connecting is published).
let private i6Coherent (s: S) =
    s.queue |> List.exists (function PublishStatus _ -> true | _ -> false)
    || (let expected =
            match s.core.phase with
            | Connecting -> Some HostConnectionState.Connecting
            | Authorizing -> Some HostConnectionState.Authorizing
            | Fetching | AcceptGuard -> Some HostConnectionState.FetchingState
            | TickAwait | Reauthorizing | MsgGuard | Refetching | SnapGuard
            | PostBuildGuard | PollObserve | PollWaiting | PostWaitObserve ->
                Some HostConnectionState.Connected
            | BackoffWaiting | PostBackoffObserve -> Some HostConnectionState.Retrying
            | Parked -> Some HostConnectionState.AuthFailed
            | Exited -> Some HostConnectionState.Disconnected
            | Dispatch | SnapshotWait | TearingDown _ -> None
        match expected with
        | Some st -> s.statusState = st
        | None -> true)

[<Fact>]
let ``I6 published status is coherent with the core phase`` () =
    Explorer.checkInvariant reach.Value "I6" i6Coherent

/// true when running `check` over the mutant's reachable graph throws.
let private violates (check: Explorer.Reach -> unit) (mutantStep: S -> Action -> S list) =
    let r = Explorer.explore mutantStep (Model.initial bounds)
    try check r; false with _ -> true

module private Mutants =
    open Model

    // M1 (PR#7 round-7): snapshot atomic block split — config VERSION captured in one
    // lock (creating the CTS), the flag cleared in a LATER one. An UpdateConfig landing
    // between them is silently erased: stale attemptVersion, no pending flag, live CTS.
    // The split's two halves are keyed on the linked CTS: a fresh SnapshotWait always has
    // cts=CtsNone (teardown disposed the previous one); the version-capture half creates
    // the CTS, so a non-None cts at SnapshotConfig means "second half".
    let m1SplitSnapshot (s: S) (a: Action) : S list =
        match a, s.waiting, s.queue with
        | ExecCmd, None, (SnapshotConfig :: _) when s.cts = CtsNone ->
            // first lock: capture version + create CTS; DO NOT clear the flag; stay put.
            [ { s with attemptVersion = s.curVersion; cts = CtsLive } ]   // queue unchanged
        | ExecCmd, None, (SnapshotConfig :: rest) ->
            // second lock: clear the flag (erasing any intervening UpdateConfig), keep the
            // stale attemptVersion, re-establish the live CTS, then complete the snapshot.
            [ for hp in [ true; false ] ->
                { s with queue = rest; configChanged = false; cts = CtsLive
                         injectedFail = false; logReplacedThisConn = false
                         daemonVersionVintage = None; logVintage = None }
                |> feed (ConfigSnapshotted hp) ]
        | _ -> Model.step s a

    // M2 (PR#7 round-6): Failed-after-Connected keeps counting instead of resetting to 1.
    // Decorate the teardown→BackoffWaiting routing: bump the attempt to old+1 and patch the
    // queued Retrying publish likewise.
    let m2NoAttemptReset (s: S) (a: Action) : S list =
        match a, s.waiting, s.queue, s.core.phase with
        | ExecCmd, None, [ DisposeClient ], TearingDown (OFailed(_, true)) ->
            Model.step s a
            |> List.map (fun s2 ->
                if s2.core.phase = BackoffWaiting then
                    let bumped = s.core.attempt + 1
                    { s2 with core = { s2.core with attempt = bumped }
                              queue = s2.queue |> List.map (function
                                  | PublishStatus(HostConnectionState.Retrying, _, bo, err, st) ->
                                      PublishStatus(HostConnectionState.Retrying, bumped, bo, err, st)
                                  | c -> c) }
                else s2)
        | _ -> Model.step s a

    // M3 (PR#7 round-9 P2, pre-rider-A): daemonVersion stamped at fetch time instead of at
    // the accepted Connected publish. The success branch of FetchVersionAndState advances
    // the core to AcceptGuard.
    let m3EagerDaemonVersion (s: S) (a: Action) : S list =
        match a, s.waiting, s.queue with
        | ExecCmd, None, (FetchVersionAndState :: _) ->
            Model.step s a
            |> List.map (fun s2 ->
                if s2.core.phase = AcceptGuard then { s2 with daemonVersionVintage = Some s.attemptVersion }
                else s2)
        | _ -> Model.step s a

    // M4 (PR#8 round-2, pre-rider-B): eager log clear before the accept guard.
    let m4EagerLogClear (s: S) (a: Action) : S list =
        match a, s.waiting, s.queue with
        | ExecCmd, None, (FetchVersionAndState :: _) ->
            Model.step s a
            |> List.map (fun s2 ->
                if s2.core.phase = AcceptGuard then { s2 with logVintage = Some s.attemptVersion }
                else s2)
        | _ -> Model.step s a

    // M8 (I6's red-first witness): the backoff publish lies about the lifecycle —
    // it writes Connected instead of Retrying. The phase/status projection must
    // catch it. (The Retrying publish is never batch-terminal — WaitBackoff always
    // follows — so no trailing-EffectOk feed is needed here.)
    let m8WrongBackoffPublish (s: S) (a: Action) : S list =
        match a, s.waiting, s.queue with
        | ExecCmd, None, (PublishStatus(HostConnectionState.Retrying, _, _, _, _) :: rest) ->
            [ { s with queue = rest
                       statusState = HostConnectionState.Connected
                       statusVersion = s.attemptVersion } ]
        | _ -> Model.step s a

    // M5 (PR#8 audit, pre-rider-C): dispose without the idempotency flag —
    // start-after-dispose can read a disposed token (EnvStart's fault branch).
    let m5NoDisposeFlag (s: S) (a: Action) : S list =
        match a with
        | EnvDispose when not s.disposeFlag ->
            [ { s with outerCanceled = true; wake = true
                       disposesLeft = s.disposesLeft - 1
                       cts = (if s.cts = CtsLive then CtsCanceled else s.cts)
                       outerDisposed = (s.loopTask = TaskInitial || not s.started
                                        || s.core.phase = Exited) } ]
        | _ -> Model.step s a

[<Fact>]
let ``mutant M1 split snapshot violates I3`` () =
    Assert.True(violates (fun r ->
        Explorer.checkInvariant r "I3" (fun s ->
            s.attemptVersion = s.curVersion
            || s.configChanged
            || s.cts = CtsCanceled
            || not s.started
            || (match s.core.phase with
                | Dispatch | SnapshotWait | TearingDown _ | BackoffWaiting
                | PostBackoffObserve | Parked | Exited -> true
                | _ -> false)))
        Mutants.m1SplitSnapshot)

[<Fact>]
let ``mutant M2 no attempt reset violates I4`` () =
    let r = Explorer.explore Mutants.m2NoAttemptReset (Model.initial bounds)
    let caught =
        r.edges |> Seq.exists (fun (KeyValue (s, outs)) ->
            match s.core.phase, s.queue with
            | TearingDown (OFailed(_, true)), [ DisposeClient ] ->
                outs |> List.exists (fun (a, s2) ->
                    a = ExecCmd && s2.core.phase = BackoffWaiting && s2.core.attempt <> 1)
            | _ -> false)
    Assert.True(caught, "mutant M2 was not detected by I4")

[<Fact>]
let ``mutant M3 eager daemonVersion violates I1-mutation`` () =
    Assert.True(violates (fun r ->
        Explorer.checkInvariant r "I1m" (fun s ->
            s.core.reachedConnected || s.daemonVersionVintage <> Some s.attemptVersion
            || s.attemptVersion < 0))
        Mutants.m3EagerDaemonVersion)

[<Fact>]
let ``mutant M4 eager log clear violates I1-mutation`` () =
    Assert.True(violates (fun r ->
        Explorer.checkInvariant r "I1m" (fun s ->
            s.core.reachedConnected || s.logVintage <> Some s.attemptVersion
            || s.attemptVersion < 0))
        Mutants.m4EagerLogClear)

[<Fact>]
let ``mutant M5 no dispose flag violates I5`` () =
    Assert.True(violates (fun r ->
        Explorer.checkInvariant r "I5" (fun s -> not s.faulted))
        Mutants.m5NoDisposeFlag)

[<Fact>]
let ``mutant M8 wrong backoff publish violates I6`` () =
    let check (r: Explorer.Reach) = Explorer.checkInvariant r "I6" i6Coherent
    Assert.True(violates check Mutants.m8WrongBackoffPublish)

// L1 no lost wakeup — a completed wake is eventually consumed, unless the loop is not
// running (Exited, or never started). The latch may survive a delay exit (WhenAny race);
// what must never happen is a wake that sits completed forever while the loop keeps running.
[<Fact>]
let ``L1 no lost wakeup`` () =
    Explorer.checkEventually reach.Value "L1"
        (fun s -> s.wake)
        (fun s -> not s.wake || s.core.phase = Exited || not s.started)

// L2 config convergence.
[<Fact>]
let ``L2 config change converges`` () =
    Explorer.checkEventually reach.Value "L2"
        (fun s -> s.configChanged)
        (fun s -> not s.configChanged || s.core.phase = Exited || not s.started)

[<Fact>]
let ``L3 disposal terminates the loop`` () =
    Explorer.checkEventually reach.Value "L3"
        (fun s -> s.outerCanceled)
        (fun s -> s.core.phase = Exited || not s.started)

// L3b: AuthFailed has no exit but config change or disposal (edge-shaped safety). From a
// genuinely-parked state (no pending config change, not disposed) every LOOP successor stays
// Parked — latch-eating WakeConsumed self-transitions allowed, no other phase reachable.
[<Fact>]
let ``L3b AuthFailed parks until config change or disposal`` () =
    let r = reach.Value
    for KeyValue(s, outs) in r.edges do
        if s.core.phase = Parked && s.waiting = Some WPark
           && not s.configChanged && not s.outerCanceled then
            for (a, s2) in outs do
                if isLoopAction a && s2.core.phase <> Parked then
                    failwithf "AuthFailed exited via %A without config change/disposal. %s"
                        a (Explorer.trace r s2)

module private LivenessMutants =
    open Model

    // M6: deaf waits — WaitAsync forgets every completed-latch check (entry AND post-
    // WhenAny): the wake is never consumed, so L1 must catch the closed cycle that keeps
    // running with the latch set forever.
    let m6DeafWaits (s: S) (a: Action) : S list =
        match a, s.waiting, s.queue with
        | WakeConsumed, _, _ -> []                            // never consume via a wait
        | ExecCmd, None, (WaitPollInterval :: rest) -> [ { s with queue = rest; waiting = Some WPoll } ]
        | ExecCmd, None, (WaitBackoff _ :: rest) -> [ { s with queue = rest; waiting = Some WBackoff } ]
        | ExecCmd, None, (ParkForConfigChange :: rest) ->
            // keep the configChanged release, drop the entry wake-consume (latch stays set)
            if s.configChanged then [ { s with queue = rest } |> feed WaitEnded ]
            else [ { s with queue = rest; waiting = Some WPark } ]
        | _ -> Model.step s a

    // M7: stuck park — WPark ignores configChanged everywhere (entry and while parked);
    // only disposal exits. L2 must catch the never-converging config change.
    let m7StuckPark (s: S) (a: Action) : S list =
        match a, s.waiting with
        | ExecCmd, Some WPark ->
            if s.outerCanceled then [ { s with waiting = None } |> feed (Faulted Disposal) ]
            else [ s ]                                        // ignore configChanged: never release
        | WakeConsumed, Some WPark -> [ { s with wake = false } ]
        | ExecCmd, None ->
            match s.queue with
            | ParkForConfigChange :: rest ->                  // entry always parks (ignores flag)
                if s.wake then [ { s with queue = rest; wake = false; waiting = Some WPark } ]
                else [ { s with queue = rest; waiting = Some WPark } ]
            | _ -> Model.step s a
        | _ -> Model.step s a

[<Fact>]
let ``mutant M6 deaf waits violates L1`` () =
    Assert.True(violates (fun r ->
        Explorer.checkEventually r "L1" (fun s -> s.wake)
            (fun s -> not s.wake || s.core.phase = Exited || not s.started))
        LivenessMutants.m6DeafWaits)

[<Fact>]
let ``mutant M7 stuck park violates L2`` () =
    Assert.True(violates (fun r ->
        Explorer.checkEventually r "L2" (fun s -> s.configChanged)
            (fun s -> not s.configChanged || s.core.phase = Exited || not s.started))
        LivenessMutants.m7StuckPark)
