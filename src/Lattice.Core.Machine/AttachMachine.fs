namespace Lattice.Core

/// Pure decision core for the project-attach flow (design 2.3):
/// lookup_account -> poll while error_num is in-progress/retry -> project_attach
/// -> settling poll. Total Mealy machine, no I/O. AttachFlowRunner (Lattice.Core)
/// interprets Commands over ONE GuiRpc connection held on the host's control
/// lane (the daemon's lookup state is per-connection) and owns the 1 s poll
/// cadence; the machine owns everything decidable without a clock.
///
/// Interpreter contract (mirrors HostMachine's): step is total — unexpected
/// (phase, input) pairs settle in Done (FlowFaulted), never throw; a command
/// batch's trailing request command produces the next Input; RPC exceptions are
/// classified by the runner by type only (BoincRpcException on a poll = that
/// stage's failure reply, design 1.2; BoincUnauthorizedException / connection
/// failures -> Faulted).
module AttachMachine =

    /// ERR_IN_PROGRESS (lib/error_numbers.h): the daemon's HTTP op is outstanding.
    [<Literal>]
    let ErrInProgress = -204
    /// ERR_RETRY: daemon busy with another GUI HTTP op; official GUIs keep polling.
    [<Literal>]
    let ErrRetry = -199
    /// Poll cap per stage — official-Manager parity (60 polls at 1 s cadence).
    [<Literal>]
    let PollLimit = 60

    type Credentials =
        | EmailPassword of email: string * password: string
        | AuthenticatorKey of key: string

    type AttachRequest =
        { ProjectUrl: string
          ProjectName: string     // display-only; the daemon accepts empty
          Credentials: Credentials }

    type Stage =
        | LookupStage
        | AttachStage

    type AttachError =
        | LookupFailed of errorNum: int * message: string
        | AttachFailed of errorNum: int * messages: string list
        | FlowFaulted of message: string
        | TimedOut of Stage

    type Phase =
        | Idle
        | LookupRequested                  // SendLookup in flight, awaiting EffectOk
        | LookupPolling of polls: int
        | AttachRequested                  // SendAttach in flight, awaiting EffectOk
        | AttachPolling of polls: int
        // Success carries the daemon's attach messages for display. NOTE the
        // semantics (design 2.3): errorNum 0 means the daemon ACCEPTED the attach
        // and created the project entry (gstate.add_project returned 0) — it does
        // NOT verify the authenticator, which the daemon only checks on its first
        // scheduler RPC (failures surface in the event log afterwards). The dialog
        // wording must say "attached", not "verified".
        | Done of Result<string list, AttachError>

    type State =
        { Phase: Phase
          Request: AttachRequest option }

    type Input =
        | Start of AttachRequest
        | EffectOk
        | LookupReply of errorNum: int * errorMessage: string * authenticator: string
        | AttachReply of errorNum: int * messages: string list
        | Faulted of message: string

    type Command =
        | SendLookup of url: string * email: string * password: string
        | PollLookup                        // runner: delay 1 s, then poll RPC
        | SendAttach of url: string * authenticator: string * projectName: string * email: string
        | PollAttach                        // runner: delay 1 s, then poll RPC
        | Report of Stage                   // progress surface for the attach dialog

    let initial = { Phase = Idle; Request = None }

    let private keepPolling errorNum =
        errorNum = ErrInProgress || errorNum = ErrRetry

    let private emailOf credentials =
        match credentials with
        | EmailPassword (email, _) -> email
        | AuthenticatorKey _ -> ""

    /// Total transition function. Unlike HostMachine.step (whose trailing `| _, _`
    /// fallthrough is compensated by the exhaustive interleaving explorer),
    /// AttachMachine has no model-checking harness — so compiler exhaustiveness IS
    /// the guard here: the safe-settle arm enumerates phases and inputs explicitly
    /// (grouped or-patterns, no wildcard), and adding a Phase/Input case produces
    /// an incomplete-match warning that forces an explicit transition decision.
    /// An unexpected pair is an interpreter bug surfaced as a terminal FlowFaulted,
    /// never an exception.
    let step (state: State) (input: Input) : State * Command list =
        let fail error = { state with Phase = Done (Error error) }, []
        match state.Phase, input with
        | Idle, Start request ->
            match request.Credentials with
            | EmailPassword (email, password) ->
                { Phase = LookupRequested; Request = Some request },
                [ Report LookupStage
                  SendLookup (request.ProjectUrl, email, password) ]
            | AuthenticatorKey key ->
                { Phase = AttachRequested; Request = Some request },
                [ Report AttachStage
                  SendAttach (request.ProjectUrl, key, request.ProjectName, "") ]

        | LookupRequested, EffectOk ->
            { state with Phase = LookupPolling 0 }, [ PollLookup ]

        | LookupPolling polls, LookupReply (errorNum, errorMessage, authenticator) ->
            if keepPolling errorNum then
                if polls + 1 >= PollLimit then fail (TimedOut LookupStage)
                else { state with Phase = LookupPolling (polls + 1) }, [ PollLookup ]
            elif errorNum = 0 then
                match state.Request with
                | Some request ->
                    { state with Phase = AttachRequested },
                    [ Report AttachStage
                      SendAttach (request.ProjectUrl, authenticator,
                                  request.ProjectName, emailOf request.Credentials) ]
                | None -> fail (FlowFaulted "lookup completed with no request in state")
            else fail (LookupFailed (errorNum, errorMessage))

        | AttachRequested, EffectOk ->
            { state with Phase = AttachPolling 0 }, [ PollAttach ]

        | AttachPolling polls, AttachReply (errorNum, messages) ->
            if keepPolling errorNum then
                if polls + 1 >= PollLimit then fail (TimedOut AttachStage)
                else { state with Phase = AttachPolling (polls + 1) }, [ PollAttach ]
            elif errorNum = 0 then { state with Phase = Done (Ok messages) }, []
            else fail (AttachFailed (errorNum, messages))

        // Terminal absorbs every input (enumerated: the exhaustiveness tripwire
        // must fire here too when an Input case is added).
        | Done _, (Start _ | EffectOk | LookupReply _ | AttachReply _ | Faulted _) ->
            state, []
        | (Idle | LookupRequested | LookupPolling _ | AttachRequested | AttachPolling _),
          Faulted message -> fail (FlowFaulted message)
        // Safe settle for every remaining pair. The earlier rules already matched
        // the meaningful pairs, so the overlap here is dead by construction; the
        // point of the explicit enumeration is that a NEW Phase or Input case
        // falls outside these or-patterns and triggers the compiler's
        // incomplete-match warning instead of silently settling.
        | (Idle | LookupRequested | LookupPolling _ | AttachRequested | AttachPolling _),
          (Start _ | EffectOk | LookupReply _ | AttachReply _) ->
            fail (FlowFaulted "unexpected (phase, input) pair")
