# M3 Control Operations — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement M3 — suspend/resume (task, project, global run modes), task abort, project update/attach/detach (incl. the async lookup_account flow), snooze, and confirmation UX for destructive ops — as a ladder of small, independently green PRs: protocol first, then Core, then App.

**Architecture:** Per the design doc (`docs/superpowers/specs/2026-07-19-m3-control-ops-design.md`, authoritative over this plan where they conflict). Control ops run on a per-host serialized **control lane** of short-lived side connections in a new `Lattice.Core.HostControlService`; `HostMonitor`/`HostMachine` are untouched except the DI-5 tick extension (its own gated PR). Decision logic is pure from birth: `ConfirmationPolicy` + `RunModePolicy` (F#, App.Aggregation) and `AttachMachine` (F#, Core.Machine) are fully specified here; C#/XAML tasks are contract-level.

**Tech Stack:** .NET 10, F# (policy/machine cores), xUnit + FsCheck, Avalonia 12.1 + FluentAvalonia 3.x headless tests, existing `ScriptedStream`/`FakeGuiRpcClient` harnesses.

**Granularity contract (hybrid B):** F# pure cores below are **normative, complete code** — transcribe them verbatim (fix compile errors only; report semantic doubts to the controller, do not silently redesign). Wire-format strings are normative data. C#/XAML tasks are contract-level: follow the named precedent files' idioms.
**Judgment routing (per task):** `transcription` = the code is fully specified here (haiku-tier); `tight-spec` = one-shot against a precise contract (sonnet-tier); `iterative` = shaping/debugging expected (opus). First failed fix on any task ⇒ escalate a tier.

**Gating:** PR C is **gated on DI-5 approval**; PRs F–I hold for owner visual eyeball after Codex+CI. Everything else merges on the standard loop. Every PR: red-first tests + mutation-falsification discipline; reviewer independently re-runs the falsification.

**PR dependency graph:** A→{D, F, G, H}; B→E→I; C independent (post-DI-5); F before G/H/I (shared dialog + failure InfoBar land in F).

---

## PR A — GuiRpc: control-op RPCs

Single-host protocol semantics only; publishable-package quality (XML docs on all public members, README protocol notes updated).

**Files:**
- Modify: `src/Lattice.Boinc.GuiRpc/IGuiRpcClient.cs`, `src/Lattice.Boinc.GuiRpc/BoincGuiRpcClient.cs`, `src/Lattice.Boinc.GuiRpc/Models/Enums.cs`, `src/Lattice.Boinc.GuiRpc/Models/CcStatus.cs`, `src/Lattice.Boinc.GuiRpc/README.md`
- Modify: `tests/Lattice.TestSupport/FakeGuiRpcClient.cs`
- Test: `tests/Lattice.Tests/BoincGuiRpcClientControlOpsTests.cs` (new), `tests/Lattice.Tests/CcStatusTests.cs`

### Task A1: enums + interface + implementation (tight-spec)

- [ ] **A1.1** Add to `Models/Enums.cs` (XML-doc'd, no `Flags`):
  - `public enum TaskOp { Suspend, Resume, Abort }`
  - `public enum ProjectOp { Suspend, Resume, Update, Detach }`
  - `public enum ModeLane { Cpu, Gpu, Network }`
- [ ] **A1.2** Add to `IGuiRpcClient` + implement in `BoincGuiRpcClient` (idiom: string body → `PerformRpcAsync(body, throwOnUnauthorized: true, ct)`; reply handling is entirely `RpcReplyParser`'s existing structural branches — `<success/>` falls through, `<error>` throws `BoincRpcException`, `<unauthorized/>` throws `BoincUnauthorizedException`):

  ```csharp
  Task PerformTaskOpAsync(TaskOp op, string projectUrl, string taskName, CancellationToken ct = default);
  Task PerformProjectOpAsync(ProjectOp op, string projectUrl, CancellationToken ct = default);
  Task SetModeAsync(ModeLane lane, RunMode mode, TimeSpan duration, CancellationToken ct = default);
  ```

  Normative wire bodies (design 1.1; tag chosen by exhaustive switch — **no default arm**, CS8524 discipline):
  - Task ops: `<suspend_result>` / `<resume_result>` / `<abort_result>` wrapping `<project_url>{url}</project_url>` + `<name>{taskName}</name>`.
  - Project ops: `<project_suspend>` / `<project_resume>` / `<project_update>` / `<project_detach>` wrapping `<project_url>{url}</project_url>`.
  - Modes: `<set_run_mode>` / `<set_gpu_mode>` / `<set_network_mode>` wrapping the mode tag then `<duration>{seconds}</duration>`. Mode tags are **exactly** `<always/>`, `<auto/>`, `<never/>`, `<restore/>` — self-closing, **no space before the slash** (parser landmine). Duration formatted with `CultureInfo.InvariantCulture` (daemon parses a C double). All four `RunMode` values are legal on the wire (`Restore` is request-only, design 1.4); no validation beyond the enum itself.
  - XML-escape `projectUrl`/`taskName` via the same mechanism existing request builders use (judgment: check how `GetMessagesAsync` interpolates; URLs and result names are ASCII in practice but escaping is still correct — `SecurityElement.Escape` or `XElement`-free manual escape, match file style).
- [ ] **A1.3** Extend `CcStatus` (additive): `RunMode TaskModePerm, double TaskModeDelaySeconds, RunMode GpuModePerm, double GpuModeDelaySeconds, RunMode NetworkModePerm, double NetworkModeDelaySeconds` parsed from `task_mode_perm` / `task_mode_delay` etc. (design 1.4). Update `FakeGuiRpcClient.DefaultStatus` accordingly.
- [ ] **A1.4** Extend `FakeGuiRpcClient` mirroring the existing hook pattern: `Func<TaskOp, string, string, Task> OnTaskOp`, `Func<ProjectOp, string, Task> OnProjectOp`, `Func<ModeLane, RunMode, TimeSpan, Task> OnSetMode`, each recording a call string (e.g. `"task_op:abort:{url}:{name}"`, `"set_mode:gpu:never:3600"`), defaulting to `Task.CompletedTask`.

### Task A2: fixture tests (tight-spec; red-first)

- [ ] **A2.1** Red first: new `BoincGuiRpcClientControlOpsTests` against `ScriptedStream` (precedent: `BoincGuiRpcClientRpcTests.cs`). Assert per op:
  - the exact request body written (byte-level: mode tags self-closing without space; both children of task ops; invariant-culture duration),
  - `<success/>` reply → completes,
  - `<error>some text</error>` reply → `BoincRpcException` with the text preserved (display-only),
  - `<unauthorized/>` reply → `BoincUnauthorizedException`.
  Run: `dotnet test tests/Lattice.Tests --filter BoincGuiRpcClientControlOps` — expect FAIL (methods missing), then implement A1, then PASS.
- [ ] **A2.2** `CcStatusTests`: parse fixture with perm/delay fields; fixture without them (old daemon) → defaults (`RunMode` 0-coercion judgment: absent fields parse as existing `ParseHelpers` defaults — verify consistency with current absent-field behavior).
- [ ] **A2.3** Mutation falsification (reviewer repeats): flip one mode tag to `<always />` (with space) in the implementation → the byte-level fixture must fail; revert.
- [ ] **A2.4** Commit `feat(guirpc): control ops — task/project ops + run modes (M3 PR A)`; README protocol-notes paragraph in the same commit.

---

## PR B — GuiRpc: account lookup / attach primitives

**Files:**
- Create: `src/Lattice.Boinc.GuiRpc/Models/AccountLookupReply.cs`, `src/Lattice.Boinc.GuiRpc/Models/ProjectAttachReply.cs`, `src/Lattice.Boinc.GuiRpc/BoincErrorCodes.cs`
- Modify: `IGuiRpcClient.cs`, `BoincGuiRpcClient.cs`, `tests/Lattice.TestSupport/FakeGuiRpcClient.cs`, README
- Test: `tests/Lattice.Tests/BoincGuiRpcClientAttachFlowTests.cs` (new)

### Task B1: models + methods (tight-spec)

- [ ] **B1.1** `BoincErrorCodes`: `public static class BoincErrorCodes { public const int InProgress = -204; public const int Retry = -199; }` (XML docs cite lib/error_numbers.h; these are the only codes Lattice branches on — never branch on `<error>` text).
- [ ] **B1.2** Models with `internal static Parse(XElement)` idiom:
  - `AccountLookupReply(int ErrorNum, string ErrorMessage, string Authenticator)` — from `<account_out>`: `error_num`, `error_msg` (may be absent → empty), `authenticator` (absent → empty).
  - `ProjectAttachReply(int ErrorNum, IReadOnlyList<string> Messages)` — from `<project_attach_reply>`: `error_num` + zero-or-more `<message>` elements.
- [ ] **B1.3** Interface + implementation:

  ```csharp
  Task RequestAccountLookupAsync(string projectUrl, string email, string password, CancellationToken ct = default);
  Task<AccountLookupReply> PollAccountLookupAsync(CancellationToken ct = default);
  Task RequestProjectAttachAsync(string projectUrl, string authenticator, string projectName, string emailAddr, CancellationToken ct = default);
  Task<ProjectAttachReply> PollProjectAttachAsync(CancellationToken ct = default);
  ```

  Normative bodies (design 1.1 W4–W7): `RequestAccountLookupAsync` lowercases the email (invariant), computes `passwd_hash = MD5hex(password + emailLower)` (precedent: `AuthorizeAsync`'s nonce hash — same `Convert.ToHexStringLower(MD5.HashData(...))`, UTF-8 bytes), sends `<lookup_account>` with `<url>`, `<email_addr>`, `<passwd_hash>`, `<ldap_auth>0</ldap_auth>`; reply is `<success/>`/`<error>`. Polls send `<lookup_account_poll/>` / `<project_attach_poll/>`. **XML-escaping: same requirement as A1.2** for every interpolated field here (`projectUrl`, `email`, `authenticator`, `projectName`, `emailAddr` — all user/config supplied; an `&` in an email local-part or URL query must not emit malformed request XML), using the same mechanism PR A settled on.
  **Poll reply parsing subtlety (design 1.2):** `PollAccountLookupAsync` must parse the reply container **without** the generic error-throw: `handle_lookup_account_poll` can pass through a raw `<error>` element from the project server, and that means *lookup failed*, not RPC failure. Route (normative): perform the RPC, then branch structurally — `<account_out>` → `AccountLookupReply.Parse`; a bare `<error>` → `AccountLookupReply(ErrorNum: -1, ErrorMessage: text, Authenticator: "")`, where `-1` is upstream's own generic-failure return in `RPC::parse_reply` (design 1.2 source; cite it in the XML doc — do not invent a new code); `<unauthorized/>` still throws `BoincUnauthorizedException` (structural, pre-existing meaning).
- [ ] **B1.4** `FakeGuiRpcClient`: hooks `OnRequestAccountLookup` (records URL+email, **never the password** — the fake's existing rule), `OnPollAccountLookup` returning scripted `AccountLookupReply` sequences, `OnRequestProjectAttach`, `OnPollProjectAttach`.

### Task B2: fixture tests (tight-spec; red-first)

- [ ] **B2.1** Red-first fixtures: exact `lookup_account` body incl. lowercased email + known-vector MD5 (precompute one: email `User@Example.COM`, password `pw` → assert the literal hex); byte-level escaping fixture (email `a&b@example.com`, URL with a `?a=1&b=2` query → request contains `&amp;`, never a raw `&`; note the hash is computed over the RAW lowercased email, escaping applies only to the XML serialization); `-204` poll body → `InProgress`; success body with authenticator; bare `<error>project says no</error>` poll body → `ErrorNum == -1` + message, **no throw**; `project_attach_reply` with two messages; `<unauthorized/>` on any of the four → `BoincUnauthorizedException`.
- [ ] **B2.2** Falsification: remove the email-lowercasing → the MD5 vector test must fail; revert.
- [ ] **B2.3** Commit `feat(guirpc): account lookup + project attach primitives (M3 PR B)`.

---

## PR C — get_project_status + tick integration *(GATED on DI-5 approval)*

**Files:**
- Modify: `IGuiRpcClient.cs`, `BoincGuiRpcClient.cs` (`GetProjectStatusAsync` — body `<get_project_status/>`, reply `<projects>` of `<project>` elements, **reuse `Project.Parse` verbatim**, design 1.6), `tests/Lattice.TestSupport/FakeGuiRpcClient.cs`
- Modify: `src/Lattice.Core/HostMonitor.cs` (the `RunTickRpcs` case only: fifth fetch into a new attempt-local `IReadOnlyList<Project> projectStatuses`, reset in `SnapshotConfig`, passed to `SnapshotBuilder.Build`), `src/Lattice.Core/SnapshotBuilder.cs` (projects source parameter: tick-fresh list replaces `state.Projects` as the `ProjectSnapshot` source; task project-name lookups keep working off the merged dictionary)
- Test: `tests/Lattice.Tests/BoincGuiRpcClientRpcTests.cs` (fixture), `tests/Lattice.Tests/SnapshotBuilderTests.cs`, `tests/Lattice.Tests/HostMonitorPollingTests.cs` (tick now issues five RPCs, ordering asserted via the fake's call log)

### Task C1 (tight-spec, but read `verification/README.md` correspondence rules first)

- [ ] **C1.1** Red-first: polling test asserting the call log gains `get_project_status` inside each tick; SnapshotBuilder test: a project suspended in the status list but stale in `ccState` renders suspended.
- [ ] **C1.2** Implement. **Verification-sync contract:** the commit message MUST carry the justification (normative text): *"HostMonitor semantics: RunTickRpcs command execution gains a fifth fetch into a new attempt-local; no new HostMachine inputs/commands/guards, no new shared state, no new interleave points — decision core, F# spec, Promela model, and probe inventory unchanged by construction (see design DI-5)."* Reviewer checks this claim against the diff (no `_gate` touches, no new fields outside attempt locals).
- [ ] **C1.3** Stryker tier-1 note: `SnapshotBuilder.cs` is in the pinned mutation scope — expect the incremental run to exercise the new merge; adjudicate any survivor per the standing rules (no assertion-stuffing).
- [ ] **C1.4** Commit `feat(core): live project status in the poll tick (M3 PR C, DI-5)`.

---

## PR D — Core: HostControlService

**Files:**
- Create: `src/Lattice.Core/HostControlService.cs`, `src/Lattice.Core/ControlOpResult.cs`
- Test: `tests/Lattice.Tests/HostControlServiceTests.cs`

### Task D1: contracts (tight-spec for the surface; iterative allowed for the lane internals)

- [ ] **D1.1** `ControlOpResult` (Core, C#): `sealed record ControlOpResult(ControlOpOutcome Outcome, string? Error)` with `enum ControlOpOutcome { Succeeded, Unreachable, AuthFailed, DaemonError, Canceled }`. Factory helpers `Success`, `FromException(Exception)` — classification by **exception type only** (precedent: `HostMonitor.Classify`): `OperationCanceledException` → Canceled; `BoincUnauthorizedException` → AuthFailed; `BoincConnectionException` → Unreachable; `BoincRpcException` → DaemonError (text = display-only); anything else → Unreachable with message.
- [ ] **D1.2** `HostControlService` (ctor: `HostRegistry`, `HostMonitorManager`, `Func<IGuiRpcClient>`): public surface

  ```csharp
  Task<ControlOpResult> PerformTaskOpAsync(Guid hostId, TaskOp op, string projectUrl, string taskName, CancellationToken ct = default);
  Task<ControlOpResult> PerformProjectOpAsync(Guid hostId, ProjectOp op, string projectUrl, CancellationToken ct = default);
  Task<ControlOpResult> SetModeAsync(Guid hostId, ModeLane lane, RunMode mode, TimeSpan duration, CancellationToken ct = default);
  ```

  **Stated concurrency invariants (write these as a comment block BEFORE implementing — async discipline rule):** (I-CL1) at most one control connection per host exists at any instant; (I-CL2) an op reads its `HostConfig` fresh from the registry inside the lane hold (config updates between click and execution win); (I-CL3) ops never throw — every path returns `ControlOpResult` (cancellation included); (I-CL4) the side client is disposed on every path (`await using`); (I-CL5) **same-host ops execute in submission order (FIFO)** — a `SemaphoreSlim` is NOT acceptable here (blocked waiters have no documented FIFO order, so Suspend-then-Resume could execute as Resume-then-Suspend and land the wrong final state). Mechanism (normative): a per-host **tail-task chain** — `ConcurrentDictionary<Guid, Task>` where submitting an op atomically swaps the stored tail for a continuation that awaits the previous tail (ignoring its outcome) then runs this op; FIFO holds by construction, there is no consumer loop or disposal lifecycle, and a canceled/failed op never breaks the chain. Entries are never removed for the service lifetime (host removal leaves a bounded, completed orphan task — document). Lane body = `TestConnectionAsync`'s shape (connect → auth-if-password → op → dispose) + `RequestRefresh` on the host's monitor on success. Unknown `hostId` → `Unreachable` with a fixed message (host was removed mid-flight).
- [ ] **D1.3** Red-first tests with `FakeGuiRpcClient`: two ops on one host never overlap (scripted gate in `OnConnect`, assert strict call ordering); **three ops submitted to one busy host execute in exact submission order (I-CL5), including when the first op faults or is canceled mid-chain**; ops on two hosts interleave; each `ControlOpOutcome` reached via its scripted exception; password path calls authorize, empty-password path doesn't; success nudges exactly the target monitor (observable via a follow-up poll on the fake — settle on the fake's observed calls, never wall-clock).
- [ ] **D1.4** Commit `feat(core): HostControlService — serialized per-host control lane (M3 PR D)`.

---

## PR E — AttachMachine (F#) + AttachFlowRunner

**Files:**
- Create: `src/Lattice.Core.Machine/AttachMachine.fs` (add to `.fsproj` compile order after `HostMachine.fs`)
- Create: `src/Lattice.Core/AttachFlowRunner.cs`
- Test: F# transition/FsCheck tests — home judgment: `tests/Lattice.Verification` if its fsproj scope allows a sibling module cleanly, else new `tests/Lattice.Machine.Tests` fsproj wired into CI like `Lattice.Aggregation.Tests`; runner tests in `tests/Lattice.Tests/AttachFlowRunnerTests.cs`

### Task E1: the pure core (transcription — this code is normative)

- [ ] **E1.1** Create `AttachMachine.fs`:

```fsharp
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
```

- [ ] **E1.2** Red-first F# tests before wiring the runner:
  - Transition table (xUnit `[<Theory>]`): every row above — happy path email flow (Start → EffectOk → in-progress ×2 → success reply → EffectOk → attach reply 0 with messages → `Done (Ok messages)`, messages preserved); authenticator skip; each failure exit; retry code −199 behaves like −204; poll #60 times out; `Done` absorbs all five input kinds; `Faulted` from every non-terminal phase.
  - FsCheck properties: (P1) for any input sequence, `step` never raises and poll commands per stage never exceed `PollLimit`; (P2) any state's `Phase = Done` is absorbing; (P3) `SendAttach` is always preceded (in the emitted command history) by either `AuthenticatorKey` start or a zero-`errorNum` `LookupReply` — i.e. an authenticator is never fabricated.
- [ ] **E1.3** Commit `feat(machine): AttachMachine pure core (M3 PR E1)`.

### Task E2: AttachFlowRunner (iterative)

- [ ] **E2.1** `AttachFlowRunner` (Lattice.Core): `Task<Result-like> RunAsync(Guid hostId, AttachRequest, IProgress<Stage>?, CancellationToken)` — acquires the host's control lane via `HostControlService` (expose an internal lane-scoped hook rather than duplicating lane logic — judgment: an internal `RunOnLaneAsync(Guid, Func<IGuiRpcClient, Task<T>>, ct)` on the service that both op methods and the runner share), holds ONE client for the whole flow, drives `AttachMachine.step` exactly like `HostMonitor.RunAsync` drives `HostMachine` (execute commands; poll commands = `Task.Delay(1s, timeProvider, ct)` then the poll RPC; exceptions → `Faulted` via type-only classification; `BoincRpcException` **on a poll RPC** → that stage's failure input per design 1.2). On `Done(Ok)`: `RequestRefresh` the host monitor. Returns the terminal result mapped to `ControlOpResult`-style categories for the App.
- [ ] **E2.2** Red-first runner tests (`FakeGuiRpcClient` + `FakeTimeProvider`): scripted −204/−204/success sequence completes; cancellation mid-poll → Canceled, client disposed; a scripted failure `AccountLookupReply` (PR B parses bare-`<error>` poll bodies into the reply — script the reply, don't throw) → LookupFailed surfaced; connection death mid-flow (scripted `BoincConnectionException`) → FlowFaulted; lane exclusivity: a task op submitted during a running attach waits (call-order assertion).
- [ ] **E2.3** Commit `feat(core): AttachFlowRunner (M3 PR E2)`.

---

## PR F — App: confirmation policies + dialog + Tasks-view ops

**Files:**
- Create: `src/Lattice.App.Aggregation/ControlConfirmation.fs` (compile order: after existing modules; no GuiRpc types)
- Create: `src/Lattice.App/Views/ConfirmationDialog.axaml(+.cs)`, `src/Lattice.App/Infrastructure/ControlFailureSurface.cs` (the shared InfoBar-backing state, one per data view)
- Modify: `src/Lattice.App/ViewModels/TasksViewModel.cs`, `src/Lattice.App/Views/TasksView.axaml(+.cs)`, `src/Lattice.App/Localization/Strings.resx`, App composition (`App.axaml.cs` — construct `HostControlService`, pass through)
- Test: `tests/Lattice.Aggregation.Tests/ControlConfirmationTests.fs`, `tests/Lattice.App.Tests/TasksViewModelControlTests.cs` (+ headless view test)

### Task F1: the F# policy file (transcription — normative)

- [ ] **F1.1** Create `ControlConfirmation.fs`:

```fsharp
namespace Lattice.App.Aggregation

open System

/// M3 control operations on one task (always exactly one host + result).
type TaskOp =
    | TaskSuspend
    | TaskResume
    | TaskAbort

/// M3 control operations on a project attachment.
type ProjectOp =
    | ProjectSuspend
    | ProjectResume
    | ProjectUpdate
    | ProjectDetach

/// Run-mode lanes. App.Aggregation stays free of GuiRpc types (module rule);
/// the C# adapter maps these to Lattice.Boinc.GuiRpc enums with an exhaustive
/// switch (CS8524 discipline).
type ModeLane =
    | CpuLane
    | GpuLane
    | NetworkLane

/// Permanent modes a user can select directly (restore is CancelTemporary).
type PermMode =
    | ModeAlways
    | ModeAuto
    | ModeNever

/// What the user asked for on one host's run-mode surface (DI-4).
type ModeIntent =
    | SetPermanent of ModeLane * PermMode
    | Snooze of duration: TimeSpan          // CPU lane, temporary Never (design 1.4)
    | CancelTemporary of ModeLane           // wire: restore

/// One user-initiated control intent carrying its blast radius.
type ControlIntent =
    | OfTask of TaskOp
    | OfProject of ProjectOp * hostCount: int   // parent row spans hostCount hosts
    | OfMode of ModeIntent                      // single host by construction (DI-4)

type ConfirmSeverity =
    | Caution        // reversible but multi-host: the dialog is the blast-radius receipt
    | Destructive    // permanently loses computed work

type ConfirmationClass =
    | Instant
    | Confirm of ConfirmSeverity

/// Total mapping intent -> confirmation class (design Part 3 / DI-1 / DI-2).
/// The dialog CONTENT (strings, host enumeration) is the view layer's job;
/// this module decides only the class.
module ConfirmationPolicy =

    let classify (intent: ControlIntent) : ConfirmationClass =
        match intent with
        | OfTask TaskAbort -> Confirm Destructive
        | OfTask (TaskSuspend | TaskResume) -> Instant
        | OfProject (ProjectDetach, _) -> Confirm Destructive
        | OfProject ((ProjectSuspend | ProjectResume | ProjectUpdate), hostCount) ->
            if hostCount > 1 then Confirm Caution else Instant
        | OfMode (SetPermanent _ | Snooze _ | CancelTemporary _) -> Instant

/// Pins the snooze/restore wire semantics (design 1.4) in one tested place.
module RunModePolicy =

    /// Wire-level mode values (RUN_MODE_* in BOINC's lib/common_defs.h).
    type WireMode =
        | WireAlways
        | WireAuto
        | WireNever
        | WireRestore

    /// Total mapping intent -> (lane, mode, duration) RPC arguments.
    /// duration Zero = permanent; positive = temporary override (snooze).
    let toWireArgs (intent: ModeIntent) : ModeLane * WireMode * TimeSpan =
        match intent with
        | SetPermanent (lane, ModeAlways) -> lane, WireAlways, TimeSpan.Zero
        | SetPermanent (lane, ModeAuto) -> lane, WireAuto, TimeSpan.Zero
        | SetPermanent (lane, ModeNever) -> lane, WireNever, TimeSpan.Zero
        | Snooze duration -> CpuLane, WireNever, duration
        | CancelTemporary lane -> lane, WireRestore, TimeSpan.Zero

    /// "Snoozed until" derivation from cc_status's *_mode_delay (design 1.4):
    /// Some deadline while a temporary override is active, else None.
    let temporaryUntil (now: DateTimeOffset) (modeDelaySeconds: float) : DateTimeOffset option =
        if modeDelaySeconds > 0.0 then Some (now.AddSeconds modeDelaySeconds) else None
```

- [ ] **F1.2** Red-first F# tests: `classify` full transition table (every DU case; hostCount 1 vs 2 boundary); `toWireArgs` full table; `temporaryUntil` at 0 / negative / positive. FsCheck: classify never returns `Instant` for `TaskAbort`/`ProjectDetach` regardless of construction path (guards the DI-1 invariant under future refactors).
- [ ] **F1.3** Commit `feat(app): control confirmation + run-mode policies (M3 PR F1)`.

### Task F2: ConfirmationDialog + failure surface (tight-spec shell, owner-eyeball visuals)

- [ ] **F2.1** Define `sealed record ConfirmationRequest(string Title, string Body, string PrimaryButtonText, ConfirmSeverity Severity)` (App layer, next to the dialog) — the sole payload between VMs and the dialog seam. `ConfirmationDialog : FAContentDialog` — **`protected override Type StyleKeyOverride => typeof(FAContentDialog);`** (template trap, precedent AddHostDialog.axaml.cs:22) — renders a `ConfirmationRequest`; body is pre-formatted by the caller (incl. DI-2's host enumeration); `Destructive` severity styles the primary button with the danger accent (consult avalonia-docs MCP for the FA danger-button resource; one version, owner eyeball). Returns bool.
- [ ] **F2.2** `ControlFailureSurface`: tiny observable holder (last `ControlOpResult` failure + dismiss) bound to an InfoBar in each data view; op successes clear it. Judgment: shape after existing InfoBar usage in AddHost flow.
- [ ] **F2.3** Commit.

### Task F3: Tasks-view wiring (tight-spec)

- [ ] **F3.1** `TasksViewModel`: `AsyncRelayCommand`s Suspend/Resume/Abort over the selected row (`TaskRowViewModel` carries `HostId`, `ProjectUrl`?, `Name` — judgment: confirm `TaskRowViewModel` exposes `ProjectUrl`; if not, add it from `Result.ProjectUrl` in its `From`). Flow per command: build `ControlIntent` → `ConfirmationPolicy.classify` → `Instant` ⇒ execute; `Confirm` ⇒ dialog via an injected `Func<ConfirmationRequest, Task<bool>>` seam (headless tests fake it — never construct the FA dialog in VM code). Execute = `HostControlService.PerformTaskOpAsync`; failure → `ControlFailureSurface`. Enablement (DI-3): command `CanExecute` requires the row's host Connected (`HostEntry.Status.State`) — re-evaluated on store `Changed`.
- [ ] **F3.2** `TasksView.axaml`: command-bar buttons (Suspend/Resume/Abort) + row context menu, icons from `Icons.axaml` set. Abort button visually separated from suspend/resume (misclick distance, DI-1(c)).
- [ ] **F3.3** Red-first headless tests: enablement flips with connection state; Instant path calls the service without the dialog seam; Destructive path consults the seam, executes only on true; failure lands in the surface; settle on expected text/fake calls (no wall-clock waits).
- [ ] **F3.4** Commit `feat(app): Tasks control ops (M3 PR F3)`. **PR F holds for owner eyeball on the dialog before merge.**

---

## PR G — App: Projects-view ops + detach

**Files:** Modify `src/Lattice.App/ViewModels/ProjectsViewModel.cs` (+ row VMs), `src/Lattice.App/Views/ProjectsView.axaml(+.cs)`, Strings.resx; tests in `tests/Lattice.App.Tests/ProjectsViewModelControlTests.cs`.

- [ ] **G1** Commands on both row tiers: child (per-host) rows → `OfProject (op, 1)` targeting that host; parent rows → `OfProject (op, group.Attachments.Length)` fanning out `PerformProjectOpAsync` per attachment host (sequential fan-out, aggregate failures into one surface message naming failed hosts). Confirm-dialog body for multi-host enumerates host display names (DI-2 receipt). Detach → Destructive dialog whose body names the host(s) and states in-progress-task loss (Strings.resx). "Add project…" button lands here but stays disabled until PR I wires the dialog (or lands in I entirely — judgment by PR size).
- [ ] **G2** Enablement per DI-3: parent-row commands require **all** covered hosts Connected (steelman'd alternative — partial fan-out to reachable subset — rejected in design DI-2/DI-3: the receipt must match reality); child rows their own host.
- [ ] **G3** Red-first headless tests: fan-out call set matches the parent row's attachment hosts exactly; partial failure aggregates; classify routing (1 vs N) hits dialog appropriately; detach always dialogs.
- [ ] **G4** Commit `feat(app): Projects control ops (M3 PR G)`. Owner eyeball before merge.

---

## PR H — App: run modes + snooze surface

**Files:** Modify rail item VM/view (`HostRailItemViewModel.cs`, rail templates in `ShellWindow.axaml`), possibly a command-bar dropdown in the shell; `src/Lattice.Core/HostSnapshot.cs` + `SnapshotBuilder.cs` (surface `TaskModePerm`/`TaskModeDelaySeconds` etc. from the extended `CcStatus` — additive record fields); Strings.resx; tests `tests/Lattice.App.Tests/RunModeControlTests.cs`.

- [ ] **H1** Host context menu (rail right-click) + scoped command-bar dropdown per DI-4: Run modes submenu (three lanes × Always/Auto/Never), Snooze (15 min / 1 h / 4 h), Resume-from-snooze (visible while `temporaryUntil` yields Some). All route: `ModeIntent` → `RunModePolicy.toWireArgs` → exhaustive `WireMode`→`RunMode` C# switch → `HostControlService.SetModeAsync`. Absent in All-hosts scope (DI-4: no fleet-wide modes in M3).
- [ ] **H2** "Snoozed until hh:mm" chip on the host rail item from `temporaryUntil` (UI clock tick refresh, precedent: `TimeText`).
- [ ] **H3** Red-first tests: intent→wire-args→service call chain for every menu item; snooze chip appears/disappears on scripted `CcStatus` delay values; enablement per DI-3.
- [ ] **H4** Commit `feat(app): run modes + snooze (M3 PR H)`. Owner eyeball (menu/chip) before merge.

---

## PR I — App: attach dialog

**Files:** Create `src/Lattice.App/Views/AttachProjectDialog.axaml(+.cs)`, `src/Lattice.App/ViewModels/AttachProjectViewModel.cs`; modify ProjectsView ("Add project…" enable); Strings.resx; tests `tests/Lattice.App.Tests/AttachProjectViewModelTests.cs`.

- [ ] **I1** Dialog (FAContentDialog subclass + StyleKeyOverride; deferral pattern precedent: AddHostDialog): host picker (prefilled+locked when a host is scoped; dropdown of Connected hosts in All-hosts scope), project URL, credential toggle (email+password | account key), progress area driven by `Stage` reports ("Contacting project…" / "Attaching…"), failure area showing the flow error verbatim (display-only). Success wording is **"attached"**, never "verified" (design 2.3: the daemon accepts the attach without checking the authenticator — a bad account key on the direct-key path surfaces later in the event log via the first scheduler RPC), and any `Done (Ok messages)` daemon messages render in the success state. Passwords: in-memory only, never logged/persisted (the VM holds them only for the flow's duration).
- [ ] **I2** VM drives `AttachFlowRunner.RunAsync`; success closes + the tick (PR C) or a `RequestRefresh` surfaces the new project; cancellation via dialog close token.
- [ ] **I3** Red-first headless tests against a scripted runner seam: phase progression text, failure rendering, host-picker gating, busy-state (no double submit — deferral re-entrancy guard precedent AddHostDialog.axaml.cs:50).
- [ ] **I4** Commit `feat(app): attach project dialog (M3 PR I)`. Owner eyeball before merge.

---

## PR J — docs: M3 owner walkthrough

- [ ] **J1** `docs/design/m3/walkthrough.md` mirroring the M2 #32 checklist pattern: every assertion verified against **shipped runtime code** (standing lesson: not design intent) — suspend/resume/abort a task; suspend/resume/update a project single- and multi-host; detach + re-attach a test project (Einstein@Home on the dev machine's live BOINC 8.2.11); attach with a deliberately bad account key via the direct-key path (expect: dialog reports attached, project appears, authenticator failure surfaces in the event log afterwards — design 2.3 semantics); snooze 15 min + resume; unreachable-host disablement; auth-failure surface (temporarily wrong password). Include the DI-1/DI-2 dialog-appearance checks as explicit steps.
- [ ] **J2** Commit `docs(m3): owner walkthrough checklist (M3 PR J)`.

---

## Plan self-review record

- Scope coverage: task suspend/resume/abort (A, F), project suspend/resume/update/detach (A, G), attach + lookup_account flow (B, E, I), global run modes + snooze (A, F1, H), confirmation UX (F1, F2, G), visibility of effects (C), unreachable semantics (D, DI-3 wiring in F/G/H). No M3 scope item lacks a PR; no PR exceeds M3 scope.
- The two F# modules are complete and self-consistent with the design doc's Part 3 tables (classify table ↔ DI-1/DI-2; wire args ↔ design 1.4).
- Type-name consistency spot-checks: `TaskOp`/`ProjectOp`/`ModeLane` exist in BOTH GuiRpc (C# enums, PR A) and App.Aggregation (F# DUs, PR F1) by design — the boundary rule (Aggregation is GuiRpc-free) forces the duplication; the C# adapter switch in PR H is the single mapping point. Implementers: do not "deduplicate" across this boundary; in C# files that see both namespaces, disambiguate with using-aliases (e.g. `using GuiTaskOp = Lattice.Boinc.GuiRpc.TaskOp;`).
