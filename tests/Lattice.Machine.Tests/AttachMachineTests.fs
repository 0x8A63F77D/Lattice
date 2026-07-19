module Lattice.Core.AttachMachineTests

open Xunit
open FsCheck
open FsCheck.Xunit
open Lattice.Core
open Lattice.Core.AttachMachine

// Verification bar (design Part 5): exhaustive transition-table tests plus FsCheck
// SAFETY and ABSORPTION properties — deliberately NOT liveness over arbitrary input
// sequences (an arbitrary sequence may simply stop mid-flow). Reaching Done is the
// runner's obligation, exercised in AttachFlowRunnerTests with the scripted fake.

let private url = "https://example.org/"

let private emailReq =
    { ProjectUrl = url
      ProjectName = "Example"
      Credentials = EmailPassword ("user@example.org", "pw") }

let private keyReq =
    { ProjectUrl = url
      ProjectName = "Example"
      Credentials = AuthenticatorKey "key123" }

let private allInputs =
    [ Start keyReq
      EffectOk
      LookupReply (0, "", "AUTH")
      AttachReply (0, [ "m" ])
      Faulted "boom" ]

let private nonTerminalPhases =
    [ Idle; LookupRequested; LookupPolling 5; AttachRequested; AttachPolling 5 ]

// ---------------------------------------------------------------------------
// Transition table
// ---------------------------------------------------------------------------

[<Fact>]
let ``Idle + Start with email credentials requests the lookup`` () =
    let state, commands = step initial (Start emailReq)
    Assert.Equal({ Phase = LookupRequested; Request = Some emailReq }, state)
    Assert.Equal<Command list>(
        [ Report LookupStage; SendLookup (url, "user@example.org", "pw") ], commands)

[<Fact>]
let ``Idle + Start with an authenticator key skips straight to attach with empty email`` () =
    let state, commands = step initial (Start keyReq)
    Assert.Equal({ Phase = AttachRequested; Request = Some keyReq }, state)
    Assert.Equal<Command list>(
        [ Report AttachStage; SendAttach (url, "key123", "Example", "") ], commands)

[<Fact>]
let ``LookupRequested + EffectOk starts polling from zero`` () =
    let s0 = { Phase = LookupRequested; Request = Some emailReq }
    let state, commands = step s0 EffectOk
    Assert.Equal({ s0 with Phase = LookupPolling 0 }, state)
    Assert.Equal<Command list>([ PollLookup ], commands)

[<Theory>]
[<InlineData(-204)>]  // ErrInProgress
[<InlineData(-199)>]  // ErrRetry behaves identically
let ``LookupPolling + a keep-polling code increments the counter and repolls`` code =
    let s0 = { Phase = LookupPolling 3; Request = Some emailReq }
    let state, commands = step s0 (LookupReply (code, "", ""))
    Assert.Equal({ s0 with Phase = LookupPolling 4 }, state)
    Assert.Equal<Command list>([ PollLookup ], commands)

[<Fact>]
let ``LookupPolling at the cap boundary times out the lookup stage`` () =
    let s0 = { Phase = LookupPolling (PollLimit - 1); Request = Some emailReq }
    let state, commands = step s0 (LookupReply (ErrInProgress, "", ""))
    Assert.Equal({ s0 with Phase = Done (Error (TimedOut LookupStage)) }, state)
    Assert.Empty commands

[<Fact>]
let ``LookupPolling + success sends the attach with the replied authenticator and the credentials email`` () =
    let s0 = { Phase = LookupPolling 2; Request = Some emailReq }
    let state, commands = step s0 (LookupReply (0, "", "AUTH"))
    Assert.Equal({ s0 with Phase = AttachRequested }, state)
    Assert.Equal<Command list>(
        [ Report AttachStage; SendAttach (url, "AUTH", "Example", "user@example.org") ], commands)

[<Fact>]
let ``LookupPolling + success with no request in state settles in FlowFaulted`` () =
    let s0 = { Phase = LookupPolling 2; Request = None }
    let state, commands = step s0 (LookupReply (0, "", "AUTH"))
    Assert.Equal(
        { s0 with Phase = Done (Error (FlowFaulted "lookup completed with no request in state")) },
        state)
    Assert.Empty commands

[<Theory>]
[<InlineData(-1)>]    // upstream generic failure (bare <error> poll reply, design 1.2)
[<InlineData(-161)>]  // any other non-zero, non-keep-polling code
let ``LookupPolling + a failure code fails the flow with LookupFailed`` code =
    let s0 = { Phase = LookupPolling 2; Request = Some emailReq }
    let state, commands = step s0 (LookupReply (code, "no such account", ""))
    Assert.Equal(
        { s0 with Phase = Done (Error (LookupFailed (code, "no such account"))) }, state)
    Assert.Empty commands

[<Fact>]
let ``AttachRequested + EffectOk starts the settling poll from zero`` () =
    let s0 = { Phase = AttachRequested; Request = Some emailReq }
    let state, commands = step s0 EffectOk
    Assert.Equal({ s0 with Phase = AttachPolling 0 }, state)
    Assert.Equal<Command list>([ PollAttach ], commands)

[<Theory>]
[<InlineData(-204)>]
[<InlineData(-199)>]
let ``AttachPolling + a keep-polling code repolls (same predicate, for uniformity)`` code =
    let s0 = { Phase = AttachPolling 0; Request = Some emailReq }
    let state, commands = step s0 (AttachReply (code, []))
    Assert.Equal({ s0 with Phase = AttachPolling 1 }, state)
    Assert.Equal<Command list>([ PollAttach ], commands)

[<Fact>]
let ``AttachPolling at the cap boundary times out the attach stage`` () =
    let s0 = { Phase = AttachPolling (PollLimit - 1); Request = Some emailReq }
    let state, commands = step s0 (AttachReply (ErrRetry, []))
    Assert.Equal({ s0 with Phase = Done (Error (TimedOut AttachStage)) }, state)
    Assert.Empty commands

[<Fact>]
let ``AttachPolling + success completes with the daemon's messages preserved`` () =
    let s0 = { Phase = AttachPolling 0; Request = Some emailReq }
    let state, commands = step s0 (AttachReply (0, [ "welcome"; "note" ]))
    Assert.Equal({ s0 with Phase = Done (Ok [ "welcome"; "note" ]) }, state)
    Assert.Empty commands

[<Fact>]
let ``AttachPolling + a failure code fails the flow with AttachFailed carrying the messages`` () =
    let s0 = { Phase = AttachPolling 0; Request = Some emailReq }
    let state, commands = step s0 (AttachReply (-136, [ "Already attached to project" ]))
    Assert.Equal(
        { s0 with Phase = Done (Error (AttachFailed (-136, [ "Already attached to project" ]))) },
        state)
    Assert.Empty commands

[<Fact>]
let ``Done absorbs every input kind`` () =
    for terminal in [ Done (Ok [ "m" ]); Done (Error (TimedOut LookupStage)) ] do
        let s0 = { Phase = terminal; Request = Some emailReq }
        for input in allInputs do
            let state, commands = step s0 input
            Assert.Equal(s0, state)
            Assert.Empty commands

[<Fact>]
let ``Faulted fails the flow from every non-terminal phase`` () =
    for phase in nonTerminalPhases do
        let s0 = { Phase = phase; Request = Some emailReq }
        let state, commands = step s0 (Faulted "connection died")
        Assert.Equal({ s0 with Phase = Done (Error (FlowFaulted "connection died")) }, state)
        Assert.Empty commands

[<Fact>]
let ``unexpected (phase, input) pairs settle in FlowFaulted, never raise`` () =
    let unexpected =
        [ Idle, EffectOk
          Idle, LookupReply (0, "", "AUTH")
          Idle, AttachReply (0, [])
          LookupRequested, Start emailReq
          LookupRequested, LookupReply (0, "", "AUTH")
          LookupPolling 1, EffectOk
          LookupPolling 1, AttachReply (0, [])
          AttachRequested, AttachReply (0, [])
          AttachPolling 1, LookupReply (0, "", "AUTH")
          AttachPolling 1, Start emailReq ]
    for phase, input in unexpected do
        let s0 = { Phase = phase; Request = Some emailReq }
        let state, commands = step s0 input
        Assert.Equal(
            { s0 with Phase = Done (Error (FlowFaulted "unexpected (phase, input) pair")) }, state)
        Assert.Empty commands

// ---------------------------------------------------------------------------
// End-to-end folds (still pure — the runner's I/O obligations are elsewhere)
// ---------------------------------------------------------------------------

[<Fact>]
let ``happy-path email flow reaches Done Ok with messages preserved`` () =
    let inputs =
        [ Start emailReq
          EffectOk
          LookupReply (ErrInProgress, "", "")
          LookupReply (ErrInProgress, "", "")
          LookupReply (0, "", "AUTH")
          EffectOk
          AttachReply (0, [ "welcome"; "note" ]) ]
    let final = inputs |> List.fold (fun s i -> fst (step s i)) initial
    Assert.Equal(Done (Ok [ "welcome"; "note" ]), final.Phase)

[<Fact>]
let ``the lookup stage emits exactly PollLimit poll commands before timing out`` () =
    let s1 = fst (step initial (Start emailReq))
    let s2, entry = step s1 EffectOk                       // poll #1
    Assert.Equal<Command list>([ PollLookup ], entry)
    let sN =
        [ 1 .. PollLimit - 1 ]                             // polls #2..#60
        |> List.fold (fun s _ ->
            let s', commands = step s (LookupReply (ErrInProgress, "", ""))
            Assert.Equal<Command list>([ PollLookup ], commands)
            s') s2
    let final, commands = step sN (LookupReply (ErrInProgress, "", ""))
    Assert.Equal(Done (Error (TimedOut LookupStage)), final.Phase)
    Assert.Empty commands

// ---------------------------------------------------------------------------
// FsCheck properties (safety + absorption + provenance)
// ---------------------------------------------------------------------------

let private credentialsGen =
    Gen.oneof
        [ Gen.map2 (fun e p -> EmailPassword (e, p))
              (Gen.elements [ "a@x.org"; "b@y.org" ]) (Gen.elements [ "pw"; "" ])
          Gen.map AuthenticatorKey (Gen.elements [ "key1"; "key2" ]) ]

let private requestGen =
    gen {
        let! credentials = credentialsGen
        let! name = Gen.elements [ ""; "Proj" ]
        return { ProjectUrl = url; ProjectName = name; Credentials = credentials }
    }

// Keep-polling codes weighted heavily so long sequences actually exercise the cap.
let private errorNumGen =
    Gen.frequency
        [ 6, Gen.elements [ ErrInProgress; ErrRetry ]
          1, Gen.constant 0
          1, Gen.elements [ -1; -161; -136; 1 ] ]

let private inputGen =
    Gen.oneof
        [ Gen.map Start requestGen
          Gen.constant EffectOk
          gen {
              let! errorNum = errorNumGen
              let! authenticator = Gen.elements [ ""; "AUTH1"; "AUTH2" ]
              return LookupReply (errorNum, "msg", authenticator)
          }
          gen {
              let! errorNum = errorNumGen
              let! messages = Gen.listOf (Gen.elements [ "m1"; "m2" ])
              return AttachReply (errorNum, messages)
          }
          Gen.map Faulted (Gen.elements [ "boom"; "died" ]) ]

// Long enough to overrun PollLimit when the keep-polling bias cooperates.
let private inputsGen =
    gen {
        let! n = Gen.choose (0, 150)
        return! Gen.listOfLength n inputGen
    }

type MachineArbs =
    static member Inputs() = Arb.fromGen inputsGen

[<Property(Arbitrary = [| typeof<MachineArbs> |])>]
let ``P1 step is total and per-stage poll commands never exceed PollLimit`` (inputs: Input list) =
    let _, lookupPolls, attachPolls =
        inputs
        |> List.fold
            (fun (s, lookups, attaches) input ->
                let s', commands = step s input
                let count command = commands |> List.filter ((=) command) |> List.length
                s', lookups + count PollLookup, attaches + count PollAttach)
            (initial, 0, 0)
    lookupPolls <= PollLimit && attachPolls <= PollLimit

[<Property(Arbitrary = [| typeof<MachineArbs> |])>]
let ``P2 Done is absorbing under any continuation`` (inputs: Input list) =
    inputs
    |> List.fold
        (fun (s, ok) input ->
            let s', commands = step s input
            let holds =
                match s.Phase with
                | Done _ -> s' = s && List.isEmpty commands
                | Idle | LookupRequested | LookupPolling _ | AttachRequested | AttachPolling _ -> true
            s', ok && holds)
        (initial, true)
    |> snd

[<Property(Arbitrary = [| typeof<MachineArbs> |])>]
let ``P3 SendAttach's authenticator comes only from an AuthenticatorKey start or a zero-error lookup reply`` (inputs: Input list) =
    inputs
    |> List.fold
        (fun (s, ok) input ->
            let s', commands = step s input
            let sentAuthenticators =
                commands
                |> List.choose (fun command ->
                    match command with
                    | SendAttach (_, authenticator, _, _) -> Some authenticator
                    | SendLookup _ | PollLookup | PollAttach | Report _ -> None)
            let legitimate =
                match input with
                | Start request ->
                    match request.Credentials with
                    | AuthenticatorKey key -> sentAuthenticators |> List.forall ((=) key)
                    | EmailPassword _ -> List.isEmpty sentAuthenticators
                | LookupReply (0, _, authenticator) ->
                    sentAuthenticators |> List.forall ((=) authenticator)
                | LookupReply _ | EffectOk | AttachReply _ | Faulted _ ->
                    List.isEmpty sentAuthenticators
            s', ok && legitimate)
        (initial, true)
    |> snd
