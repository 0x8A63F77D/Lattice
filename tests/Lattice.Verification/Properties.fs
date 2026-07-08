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
