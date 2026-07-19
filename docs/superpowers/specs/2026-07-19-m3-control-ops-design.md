# M3 Control Operations — Design

**Goal:** Lattice's first write path to BOINC daemons: suspend/resume (task, project, global run modes), task abort, project update/attach/detach (including the async `lookup_account` flow), snooze, and confirmation UX for destructive operations.

**Scope (fixed by CLAUDE.md M3; not negotiable here):**
- Suspend/resume a task; abort a task.
- Suspend/resume a project; update a project; attach to a project (lookup_account → poll → project_attach); detach from a project.
- Global run modes (CPU / GPU / network lanes: always / auto / never / restore) and snooze (temporary "never" with a duration).
- Confirmation UX for destructive ops.

**Non-goals (adjacent RPCs deliberately excluded):** `project_reset`, `project_nomorework` / `allowmorework`, `project_detach_when_done`, file-transfer ops (`retry_file_transfer` / `abort_file_transfer`), account-manager flows (`acct_mgr_rpc`), `create_account`, `run_benchmarks`, preference writes, multi-select task operations, and any op queueing for offline hosts (see decision item DI-3). The enum designs below leave room for these; none ship in M3.

---

## Part 1 — Protocol facts (verified 2026-07-19; every claim carries its source)

Sources: BOINC/boinc @ master commit `6bd49e0` (`lib/gui_rpc_client_ops.cpp`, `lib/gui_rpc_client.cpp`, `client/gui_rpc_server_ops.cpp`, `client/gui_rpc_server.cpp`, `client/client_types.cpp`, `client/acct_setup.cpp`, `lib/error_numbers.h`, `lib/common_defs.h`) and chausner/BoincRpc @ `29fb45d` (`RpcClient.cs`, `Enums.cs`, `Structs.cs`) as the same-language cross-check. Extraction was verbatim-from-source (no memory fill).

### 1.1 Request wire formats

| # | Op | Request body (exact shape) | Source |
|---|----|---------------------------|--------|
| W1 | Task suspend/resume/abort | `<{suspend\|resume\|abort}_result>` wrapping `<project_url>URL</project_url>` + `<name>RESULT_NAME</name>` | `RPC_CLIENT::result_op`, gui_rpc_client_ops.cpp:2075 |
| W2 | Project suspend/resume/update/detach | `<project_{suspend\|resume\|update\|detach}>` wrapping `<project_url>URL</project_url>` | `RPC_CLIENT::project_op`, gui_rpc_client_ops.cpp:1664 |
| W3 | Run modes | `<set_{run\|gpu\|network}_mode>` wrapping one mode tag (`<always/>`, `<auto/>`, `<never/>`, `<restore/>` — self-closing, **no space before the slash**) + `<duration>SECONDS</duration>` (double; `0` = permanent) | `RPC_CLIENT::set_run_mode`/`mode_name`, gui_rpc_client_ops.cpp:1766 |
| W4 | Account lookup (start) | `<lookup_account>` wrapping `<url>`, `<email_addr>` (lowercased), `<passwd_hash>` = `MD5(password + email_lowercase)`, `<ldap_auth>0</ldap_auth>` | `RPC_CLIENT::lookup_account`, gui_rpc_client_ops.cpp:2279 |
| W5 | Account lookup (poll) | `<lookup_account_poll/>` | gui_rpc_client_ops.cpp:2306 |
| W6 | Project attach | `<project_attach>` wrapping `<project_url>`, `<authenticator>`, `<project_name>`, `<email_addr>` | `RPC_CLIENT::project_attach`, gui_rpc_client_ops.cpp:1726 |
| W7 | Project attach (poll) | `<project_attach_poll/>` | gui_rpc_client_ops.cpp:1748 |
| W8 | Project status (read) | `<get_project_status/>` | `RPC_CLIENT::get_project_status` (request confirmed by dispatch-table entry; see 1.6) |

### 1.2 Reply forms

- Every in-scope **control** op replies `<success/>` or `<error>text</error>` — verified per-handler in gui_rpc_server_ops.cpp (each handler prints one of the two literally). The `<status>N</status>` reply form exists in the generic client parser (`RPC::parse_reply`, gui_rpc_client.cpp) but **no in-scope handler emits it**; Lattice's `RpcReplyParser` therefore keeps ignoring it.
- `<unauthorized/>` (not `<error>`) is the auth-rejection tag, emitted by `auth_failure()` (gui_rpc_server_ops.cpp) — matches `RpcReplyParser`'s existing structural branch.
- `lookup_account_poll` replies `<account_out>` containing `<error_num>N</error_num>` and, on success, `<authenticator>`; while the daemon's HTTP call to the project is outstanding, `error_num == ERR_IN_PROGRESS = -204` (`LOOKUP_ACCOUNT_OP::do_rpc`, acct_setup.cpp; error_numbers.h). Official GUI poll loops also treat `ERR_RETRY = -199` as keep-polling, with a 60 s cap (clientgui/ProjectPropertiesPage.cpp). The poll handler can also pass through a raw `<error>` element from the project server (handle_lookup_account_poll, gui_rpc_server_ops.cpp:800) — so an `<error>` reply to the poll means *lookup failed*, not protocol failure.
- `project_attach_poll` replies `<project_attach_reply>` containing zero or more `<message>` elements and `<error_num>N</error_num>`.
- **`project_attach` is not genuinely async**: `handle_project_attach` runs `gstate.add_project(...)` synchronously and stashes its result; the poll merely echoes `gstate.project_attach` (gui_rpc_server_ops.cpp:839–937). One poll after `<success/>` yields the final verdict — there is no `-204` phase. The *lookup* leg is the only truly-polling leg of the attach flow. Upstream also rejects attach to an already-attached URL with `<error>Already attached to project</error>` after canonicalizing the URL.
- Branching on the **numeric** `error_num` field is structural (a typed field, not message text) and is the sanctioned mechanism for the poll loop. Lattice still never branches on `<error>` body text — upstream's own `parse_reply` does string-match error text for attach errors, and we deliberately do not replicate that (display-only, per CLAUDE.md).

### 1.3 Auth requirements

From the dispatch table (gui_rpc_server_ops.cpp:1772–1858, columns `auth_required / enable_network / read_only`):
- **Every in-scope control op requires auth** (task ops, project ops, run modes, lookup/attach + polls).
- `project_update`, `lookup_account(_poll)`, `project_attach(_poll)` additionally set `enable_network` (daemon temporarily permits network even if network mode is Never).
- `get_project_status` is **unauthenticated read-only** — same class as `get_state`/`get_results`, so adding it to the steady poll tick has no auth implications.
- Auth model: with a non-empty `gui_rpc_auth.cfg` password, *remote* unauthenticated connections are rejected outright; *localhost* unauthenticated connections may call the read-only block but get `<unauthorized/>` on any control op. Successful `<auth2>` clears `auth_needed` for the connection's lifetime.

### 1.4 Run-mode / snooze semantics (`RUN_MODE::set`, client/client_types.cpp)

- `duration == 0` → permanent mode change (`perm_mode` set + state file dirtied).
- `duration > 0` → temporary override: `temp_mode`/`temp_timeout` set, `perm_mode` untouched; when the deadline passes, `get_current()` silently reports `perm_mode` again (poll-time fallback, no event). **Snooze = `set_run_mode(<never/>, duration)`.**
- `<restore/>` cancels a temporary override immediately, and if the override had been made permanent-equal, restores the previous permanent mode. **Un-snooze = `set_run_mode(<restore/>, 0)`.**
- Numeric enum values `RUN_MODE_ALWAYS=1, RUN_MODE_AUTO=2, RUN_MODE_NEVER=3, RUN_MODE_RESTORE=4` (lib/common_defs.h) — matches Lattice's existing `RunMode` enum (`Models/Enums.cs`) and chausner's `Mode`. Per the common_defs.h comment, `RESTORE` is a **request-only** value: it never appears in `task_mode`/`task_mode_perm` replies.
- The `get_cc_status` reply carries, per lane, `{task|gpu|network}_mode` (= current, temp-aware), `{...}_mode_perm` (permanent mode), and `{...}_mode_delay` (seconds remaining on a temporary override; `0` when none) — `handle_get_cc_status`, gui_rpc_server_ops.cpp:671. This is everything a "Snoozed until hh:mm" display needs. Also note: `task_suspend_reason` is masked to `0` when the only reason is CPU throttle (deliberate upstream behavior).

### 1.5 Identity keys

- Tasks are addressed by **(project_url, result name)** — the server does `lookup_project(url)` then `lookup_result(project, name)` (handle_result_op, gui_rpc_server_ops.cpp:542). Both fields are already on Lattice's `Result` model (`ProjectUrl`, `Name`).
- Projects are addressed by **master_url** only (`get_project(grc, url)`); already `Project.MasterUrl`.

### 1.6 get_project_status reply content

`handle_get_project_status` (gui_rpc_server_ops.cpp:142) wraps `<projects>` around `PROJECT::write_state(out, gui_rpc=true)` per project (client/project.cpp) — the **same writer** that produces `get_state`'s `<project>` elements. The reply therefore carries every field Lattice's existing `Project.Parse` reads: `master_url`, `project_name`, `user_total_credit`, `user_expavg_credit`, `host_total_credit`, `host_expavg_credit`, `resource_share`, plus `suspended_via_gui` / `dont_request_more_work` as presence-only self-closing flags (absent = false — exactly how `get_state` already encodes them, so `ParseHelpers.GetBool` handles them unchanged). Consequence for DI-5: **`Project.Parse` is reused verbatim and no join with the cached `get_state` projects is needed** — the status reply is a complete replacement source for `ProjectSnapshot`.

### 1.7 Version-gate audit

No numeric version gating exists for any in-scope op in either dispatch or handlers (source-grounded negative for gui_rpc_server_ops.cpp / gui_rpc_server.cpp; the sole preflight found anywhere nearby is `create_account` + `consented_to_terms` requiring a client name from `exchange_versions` — out of scope). All in-scope RPCs predate BOINC 8.x by many major versions (chausner's README pins its identical op set to ≤ 7.18.1). **Conclusion: M3 ships no version-gate policy module.** The audit table above is the artifact; if a future op (M4+) needs gating, the gate belongs in `Lattice.Core` next to where `DaemonVersion` is already stored per connection (CLAUDE.md's versioning rule), and this section is the template for auditing it.

---

## Part 2 — Architecture

### 2.1 Connection acquisition: the control lane (no HostMonitor changes)

The per-host `HostMonitor` actor loop *exclusively* owns its `IGuiRpcClient`; injecting control RPCs into that loop would mean new `HostMachine` inputs/commands and a full verification-sync update (F# executable spec + Promela + probe inventory). Instead, control ops use the **existing side-connection precedent** (`HostMonitorManager.TestConnectionAsync`, HostMonitorManager.cs:100 — one-shot connect → auth → RPC → dispose):

- **`HostControlService` (new, `Lattice.Core`)** owns one **serialized control lane per host**: a per-host async mutex; each submitted op runs connect → auth (when a password is configured) → op RPC → dispose, on a fresh short-lived connection. Ops on different hosts run concurrently; ops on the same host queue FIFO on the lane (they are user-initiated and rare — no throughput concern).
- On op success the service calls the host monitor's existing `RequestRefresh()` so the read loop converges the UI within ~1 poll tick. **No optimistic snapshot mutation** — `HostSnapshot` remains single-writer (the monitor). The UI consequence (a ~1 s convergence delay after a successful op) is stated in DI-1.
- The attach flow holds the host's lane for its whole duration (lookup can take seconds of polling) over **one** connection — the daemon's lookup/attach state is per-connection (`grc.lookup_account_op`), so the poll **must** reuse the connection that issued the request. Other control ops on that host queue behind it; read polling is unaffected (separate connection).
- Daemon-side concurrent-connection limits: the daemon serves multiple GUI RPC sockets (Manager + boinccmd coexist routinely), and the lane discipline means Lattice holds at most 2 connections per host (monitor + one control op) transiently. The monitor's own comment ("BOINC daemons allow very few concurrent GUI RPC connections") is respected by the serialization.
- **`HostMonitor.cs` and `HostMachine.fs` are not touched by any M3 PR except the DI-5 tick extension, which is its own costed decision item.**

Failure taxonomy (`ControlOpResult`, Core): `Succeeded` | `Unreachable` (connect/socket failure — carries display text) | `AuthFailed` (`<unauthorized/>` or password rejected) | `DaemonError` (an `<error>` reply — text is display-only, never branched on) | `Canceled`. Exception→category mapping happens once in `HostControlService` (same type-only classification style as `HostMonitor.Classify`).

Re-auth path: because every op authenticates on its own fresh connection, "any RPC may return `<unauthorized/>` at any time" reduces to a per-op failure — there is no session to re-authenticate. A mid-op `<unauthorized/>` (password changed between auth and op) maps to `AuthFailed`; the UI surfaces it exactly like the monitor's AuthFailed state (fix the password in host settings). No retry loops.

Rejected alternative — routing control ops through the monitor's connection: saves a TCP+auth round-trip per op (~tens of ms on LAN) at the price of a full verification-contract update and interleaving control latency with poll ticks (a multi-MB `get_state` refetch would block a suspend click behind it). Rejected for M3; if M4's tunnel manager makes connection setup expensive (SSH handshake per op), revisit with a persistent-second-connection variant of the lane — the `HostControlService` API is deliberately connection-agnostic so only its internals would change.

### 2.2 Data freshness after control ops

Post-op visibility, per data source:
- **Task ops** (`suspended_via_gui` on results) — converge on the next `get_results` tick: already fresh.
- **Run modes** — converge on the next `get_cc_status` tick: already fresh. The "Snoozed until hh:mm" display uses the per-lane `*_mode_delay` / `*_mode_perm` fields (1.4) — an additive extension to `CcStatus.Parse` (six new fields), carried through `HostSnapshot` for the DI-4 surface.
- **Project ops** (`suspended_via_gui`, detach removing the project) — **do not converge today**: projects render from the `get_state` snapshot, which the steady tick never refetches (only an unknown-workunit appearance triggers `RefetchState`). A suspended project would stay visually active until an unrelated state refetch. This is the one place M3 genuinely touches read-path behavior → **DI-5** proposes adding `get_project_status` (unauthenticated, read-only, small reply) as a fifth RPC inside the existing `RunTickRpcs` command execution, with `SnapshotBuilder` taking its project list as the fresh source (joined with cached `get_state` for any static fields the status reply lacks). Cost assessment in DI-5.

### 2.3 The attach flow machine (`AttachMachine`, pure F#)

The lookup→poll→attach flow is a state machine and is modeled as one from birth (`Lattice.Core.Machine/AttachMachine.fs` — same project as `HostMachine`, same pure Mealy-machine idiom: `State → Input → State * Command list`, total transition function, no I/O). It is **not** under the HostMonitor verification-sync contract (that contract is scoped to HostMonitor.cs semantics); its verification bar is set in Part 5.

Shape (full F# in the plan doc):
- Credentials input DU: `EmailPassword of email * password` (drives the lookup leg) or `Authenticator of key` (skips straight to attach — the daemon accepts a raw account key, W6, and weak/no-email accounts need this path).
- Phases: `Idle → LookupRequested → LookupPolling (poll counter in state) → AttachRequested → AttachPolling → Done of Result<unit, AttachError>`.
- Poll discipline: keep polling while `error_num ∈ {-204, -199}`; count-based timeout (60 polls at the interpreter's 1 s cadence — mirrors the official Manager's 60 s cap); the attach leg settles on its first poll (1.2) but uses the same predicate for uniformity.
- Errors: `LookupFailed of errorNum * message` | `AttachFailed of errorNum * messages` | `FlowFaulted of message` (transport/auth exceptions, classified by the interpreter by exception type) | `TimedOut of stage`.
- Interpreter: `AttachFlowRunner` (Lattice.Core) executes commands against one `IGuiRpcClient` on the host's control lane; a `BoincRpcException` during a poll is fed back as a failed-poll input (an `<error>` poll reply is a lookup failure, 1.2), not thrown.
- Cancellation: caller's token aborts the flow between commands; the daemon-side lookup continues harmlessly and dies with the connection.

### 2.4 Protocol layer additions (`Lattice.Boinc.GuiRpc`)

New `IGuiRpcClient` members, matching the existing idiom (string request bodies via `PerformRpcAsync`, structural reply handling, models with `internal static Parse(XElement)`):

```csharp
Task PerformTaskOpAsync(TaskOp op, string projectUrl, string taskName, CancellationToken ct = default);
Task PerformProjectOpAsync(ProjectOp op, string projectUrl, CancellationToken ct = default);
Task SetModeAsync(ModeLane lane, RunMode mode, TimeSpan duration, CancellationToken ct = default);
Task<IReadOnlyList<Project>> GetProjectStatusAsync(CancellationToken ct = default);
Task RequestAccountLookupAsync(string projectUrl, string email, string password, CancellationToken ct = default);
Task<AccountLookupReply> PollAccountLookupAsync(CancellationToken ct = default);
Task RequestProjectAttachAsync(string projectUrl, string authenticator, string projectName, string emailAddr, CancellationToken ct = default);
Task<ProjectAttachReply> PollProjectAttachAsync(CancellationToken ct = default);
```

- `TaskOp { Suspend, Resume, Abort }`, `ProjectOp { Suspend, Resume, Update, Detach }`, `ModeLane { Cpu, Gpu, Network }` — C# enums, exhaustive-switch discipline (no default arm; CS8524 rule applies). Enum-dispatched ops mirror upstream's shared `project_op`/`result_op` and keep the interface small; the enums deliberately contain only M3-scope members.
- `passwd_hash` computation (`MD5(password + email.ToLowerInvariant())`) lives inside `RequestAccountLookupAsync` — same pattern as `AuthorizeAsync`'s nonce hash; the password never appears on the wire or in the call log.
- `AccountLookupReply(int ErrorNum, string ErrorMessage, string Authenticator)`, `ProjectAttachReply(int ErrorNum, IReadOnlyList<string> Messages)` — thin typed models; a `BoincErrorCodes` static class carries the two named constants (`InProgress = -204`, `Retry = -199`). The GuiRpc layer does **not** wrap the poll loop (chausner does; we deliberately diverge): loop cadence/timeout is policy and belongs to Core's `AttachMachine`. The README's protocol notes gain the lookup/attach flow description.
- Boundary check: everything above is single-host protocol semantics — publishable in the standalone NuGet package, no Core/App leakage.

### 2.5 App layer (concept level; details are PR-ladder work)

- **Commands** live on the existing view models: Tasks view (suspend/resume/abort on the selected row — single selection, as today), Projects view (suspend/resume/update/detach on rows; "Add project…" opens the attach dialog), host rail / command bar (run modes + snooze; placement is DI-4).
- **One generic `ConfirmationDialog`** (`FAContentDialog` subclass; `StyleKeyOverride => typeof(FAContentDialog)` — known template trap), driven entirely by the pure confirmation policy's output (Part 3). Destructive severity styles the primary button with the danger accent. Visual fidelity: one version, owner eyeball (verification-lane split).
- **Attach dialog**: fields for project URL + (email & password | account key toggle), host picker prefilled from scope; progress states rendered from `AttachMachine` phase reports; failure shows the daemon/project error text verbatim (display-only).
- **Failure surface**: an InfoBar per data view showing the last `ControlOpResult` failure (dismissible, replaced by newer failures); op failures also appear naturally in the daemon's own event log (the daemon logs each control op, 1.1 handlers call `msg_printf`).
- In-flight affordance: commands disable while their `AsyncRelayCommand.IsRunning`; no row-level spinners in M3.

---

## Part 3 — Pure policy modules (named at design time, per project rule)

| Module | Home | Responsibility |
|---|---|---|
| `ControlIntent` DU + `ConfirmationPolicy` | `Lattice.App.Aggregation/ControlConfirmation.fs` (new) | Total mapping op-intent → confirmation class. Input carries the blast radius (`hostCount` for project ops). Output DU: `Instant` \| `Confirm of ConfirmSeverity` (`Caution` = reversible but multi-host; `Destructive` = data loss). The dialog *content* is derived from the intent by the view layer (localized strings stay in Strings.resx); the policy decides only the class. Exhaustive match, no wildcard on domain DUs. |
| `RunModePolicy` | same file | Total mapping `ModeIntent` (`SetPermanent (lane, mode)` \| `Snooze duration` \| `CancelTemporary lane`) → wire args `(lane, mode, duration)`, pinning the snooze semantics of 1.4 in one tested place. Also derives the display state ("Snoozed until…") from cc_status's `*_mode_delay`. |
| `AttachMachine` | `Lattice.Core.Machine/AttachMachine.fs` (new) | The attach flow state machine (2.3). |
| Version gates | — | **Not shipped** (audit 1.7: empty table). This row exists to record the decision, not a module. |

`Lattice.App.Aggregation` keeps its no-GuiRpc-types rule: the F# side defines its own `Mode` DU; the C# adapter maps to `Lattice.Boinc.GuiRpc.RunMode` with an exhaustive switch.

Proposed `ConfirmationPolicy.classify` (normative; full snippet in the plan):

| Intent | Class |
|---|---|
| Task suspend / resume | Instant |
| Task abort | Confirm Destructive |
| Project suspend / resume / update, 1 host | Instant |
| Project suspend / resume / update, N > 1 hosts | Confirm Caution |
| Project detach (any host count) | Confirm Destructive |
| Run mode / snooze (single host by construction) | Instant |

---

## Part 4 — Decision items for the owner

Each item: (a) product-language consequences, (b) steelmanned alternatives, (c) what would prove the proposal wrong. DI-1..DI-4 are the concept-level confirmation-UX taxonomy the milestone calls for; DI-5 is the one read-path change.

### DI-1. Which operations ask "are you sure?" — proposal: only the two that destroy work

**Proposal:** Task abort and project detach open a confirmation dialog (danger-styled). Everything else — suspend/resume of tasks and projects, project update, run modes, snooze — executes on click, because each is instantly reversible by its inverse action (BOINC has no undo primitive, so reversibility is the only safety that exists).

- **(a) Consequences:** You can pause and resume things freely without dialog fatigue; the two clicks that permanently lose computed work (abort throws away a task's progress; detach discards ALL of that project's in-progress tasks on that host and the host must re-download everything on re-attach) always get a stop-and-look moment naming exactly what will be lost. After any successful action the grid catches up within about a second (the app re-polls rather than faking the new state instantly); the click affordance disables while the action is in flight.
- **(b) Steelmen:** *Confirm everything* — maximally safe, but trains reflexive click-through, which defeats the two dialogs that matter (the well-documented alert-fatigue failure). *Confirm nothing + undo toast* — nicer UX in theory, but BOINC offers no undo for abort/detach, so the toast would be a lie; rejected as impossible, not just undesirable. *Type-the-name confirmation for detach* (GitHub-style) — proportionate for repo deletion, heavyweight for a per-host project detach that re-attach can mostly repair (except lost in-progress tasks); rejected as friction disproportionate to the loss.
- **(c) Would prove it wrong:** If the owner walkthrough (or real use) produces accidental suspends from misclicks on the dense grid, promote suspend to Caution-class on the evidence; if the abort dialog is being clicked through without reading, the taxonomy (not the wording) is wrong — revisit with friction proportional to loss.

### DI-2. Project actions from an aggregated row: act on all hosts, or make the user go host-by-host? — proposal: act on all, always confirm when N > 1

**Proposal:** In All-hosts scope the Projects view shows one parent row per project spanning N hosts. Suspend/resume/update on a parent row applies to every host attached to that project, and **any** multi-host action (even reversible ones) confirms first with the host list spelled out ("Suspend Einstein@Home on 3 hosts: mini, tower, laptop"). Child rows (one host) keep the DI-1 classes. Detach from a parent row is Destructive and enumerates hosts the same way.

- **(a) Consequences:** The multi-host manager actually manages multiple hosts with one click — the product's core differentiator — but a fat-finger on an aggregate row can't silently hit N daemons: the dialog is the blast-radius receipt. Single-host actions stay friction-free.
- **(b) Steelmen:** *Child-rows-only ops* — unambiguous, zero blast radius, but reduces M3 to a per-host tool exactly where BOINCTasks-class multi-host management is the selling point; rejected as product self-defeat. *No confirm on reversible multi-host ops* (symmetric with DI-1) — consistent, but "reversible" understates N-host suspends: resuming 8 hosts one misclick later is real toil, and the confirm doubles as the only place the user sees *which* hosts will be touched; rejected because the information value alone justifies the dialog.
- **(c) Would prove it wrong:** If walkthrough use on a 2-host setup makes the Caution dialog feel naggy (the most common fleet size), add a "don't ask again for reversible multi-host actions" setting or an N-threshold — the policy module makes that a one-line class change.

### DI-3. Acting on a host that's offline: queue the action or refuse it? — proposal: refuse, with the controls disabled

**Proposal:** Control commands are enabled only for hosts whose `HostConnectionState` is `Connected`. In every other lifecycle state (Disconnected / Connecting / Authorizing / FetchingState / Retrying — incl. its "Unreachable" rendering at attempt ≥ 4 — / AuthFailed) the buttons/menu items are disabled (tooltip says why). Nothing is ever queued for later delivery. If a host drops in the instant between click and execution, the op fails fast with "host unreachable" in the InfoBar.

- **(a) Consequences:** What you see is what happens *now* — an action either takes effect within a second or visibly fails. Nobody discovers at 3 a.m. that an abort queued eight hours ago fired when the laptop woke up. The cost: to act on an offline host you must wait until it's back (the rail shows when it reconnects).
- **(b) Steelmen:** *Queue-and-deliver on reconnect* (BOINCTasks-style pending ops) — genuinely useful for flappy laptops-as-hosts, but it imports delayed-blast semantics (destructive ops firing without a human present), a persistence question (does the queue survive Lattice restarts?), and a reconciliation question (is the queued op still meaningful against the host's new state?). Each is solvable; together they are a feature, not a default — and M4's notification surface is the natural home if evidence demands it. *Enable the buttons and let the op fail* — simpler wiring, but turns a knowable precondition into an error message; rejected because disabled-with-reason is the same information delivered earlier.
- **(c) Would prove it wrong:** Recurring real-world pattern of "the host flaps and I keep missing the window to act" — that's the signal to design the pending-ops queue as its own feature with its own confirmation semantics, not to bolt it on here. This is also where HostMonitor-adjacency lurks (a queue would need reconnect hooks into the monitor's state machine); refusing keeps M3 entirely clear of the verification-sync contract.

### DI-4. Where do run modes and snooze live, and which snooze durations? — proposal: host context menu + scoped command bar; 15 min / 1 h / 4 h

**Proposal:** Run-mode controls attach to a *host*, not a data row: right-click a host in the rail (or a "Computing" dropdown in the command bar when a single host is scoped) → Run modes submenu (CPU: Always/Auto/Never; GPU: same; Network: same) + Snooze (15 min / 1 h / 4 h) + Resume (= restore, shown while a temporary override is active). In All-hosts scope the dropdown is absent (no fleet-wide run-mode changes in M3 — that's M4 host-groups territory).

- **(a) Consequences:** "Pause this machine for lunch" is two clicks on the machine itself, and the row grids stay purely informational. No fleet-wide snooze yet — deliberate, because DI-2's blast-radius receipt pattern would need host-group semantics to be meaningful here.
- **(b) Steelmen:** *Official-Manager-style app menu* (Activity menu) — familiar to BOINC veterans, but menus detached from the host list are wrong for a multi-host app (which host does "Snooze" mean?); rejected on ambiguity. *Fleet-wide snooze in All-hosts scope* — tempting symmetry, deferred to M4 with host groups where "snooze these 3 of my 8 hosts" is expressible; a fleet-wide toggle that can't be scoped is a blunt instrument. *Single "1 hour" snooze* (official Manager) — simpler, but the three-duration menu costs nothing in the same submenu.
- **(c) Would prove it wrong:** If the walkthrough shows the rail context menu is undiscoverable (nobody right-clicks), promote the command-bar dropdown to always-visible-when-scoped; if 15 min/4 h go unused, collapse to the Manager's single hour.

### DI-5. Making project changes visible: add one cheap read RPC to the steady poll — proposal: yes (costed)

**Proposal:** Add `get_project_status` (unauthenticated, read-only, small reply — 1.3) as a fifth RPC inside the tick's existing `RunTickRpcs` execution, and have `SnapshotBuilder` take its project list as the authoritative dynamic-project source. Without it, a project suspend/detach issued from Lattice (or from any other client, or the daemon's own scheduling) stays invisible until an unrelated full-state refetch — an M3 suspend button whose effect the UI can't show.

- **(a) Consequences:** Project rows (suspended state, no-new-tasks flag, credit figures) become live like tasks already are, at the cost of one small extra RPC per host per tick (~1 s cadence). Every project op's effect is visible on the next tick.
- **(b) Steelmen:** *Force a full `get_state` refetch after each project op* — no tick change, but multi-MB on busy hosts, and it only covers *Lattice-initiated* changes (external suspends stay invisible); worse, wiring a "refetch now" trigger into the monitor means a new `HostMachine` input — the **full** verification-sync update DI-5 exists to avoid. *Do nothing in M3* — ships a suspend button that doesn't visibly work; rejected.
- **(c) Would prove it wrong:** The source audit (1.6) confirms the reply is a complete replacement source (same writer as `get_state`'s project elements), so the remaining falsifier is empirical: if live-daemon testing surfaces a reply-size or cadence cost that matters on busy hosts (dozens of projects — unlikely; the reply is a few KB), the cost balance shifts toward refetch-on-op — that evidence arrives in the PR that implements this, before any HostMonitor-adjacent code lands.
- **Cost (verification-sync contract):** the change is confined to the `RunTickRpcs` *command execution* in the C# shell (one more fetch into an attempt-local, merged in `SnapshotBuilder`) — no new `HostMachine` inputs/commands/guards, no new shared state, no new interleave points. Per the contract, the commit states why no model change is needed (attempt-local data, no routing change); the F# spec/Promela/probe lists are untouched. This is the LOW-cost corner of HostMonitor adjacency; anything needing new routing (the steelman's refetch trigger) is the HIGH-cost corner and is not proposed.

---

## Part 5 — Verification plan (lane split per artifact)

| Artifact | Lane | Bar |
|---|---|---|
| GuiRpc control ops | machine | Fixture-based: assert exact request bytes (incl. self-closing tags with no space — W3) via the existing `ScriptedStream` harness; canned replies for `<success/>`, `<error>`, `<unauthorized/>`; parser tests for `AccountLookupReply`/`ProjectAttachReply` incl. `-204` and raw-`<error>` poll bodies. |
| `HostControlService` | machine | `FakeGuiRpcClient`-driven: per-host lane serialization (two ops on one host never overlap; ops on two hosts do), exception→`ControlOpResult` taxonomy, RequestRefresh nudge on success, cancellation. Concurrency invariant stated in the plan before code (async discipline rule). |
| `AttachMachine` | machine | Exhaustive transition-table tests + FsCheck properties (terminal states absorb; poll count never exceeds the cap; every input sequence reaches `Done`; credentials-variant reachability). No Promela: single sequential flow, no concurrent interleavings to model — stated here per the verification-cost pricing rule. |
| `ConfirmationPolicy` / `RunModePolicy` | machine | Exhaustive transition tables (every DU case × boundary host counts). |
| ViewModel command wiring | machine | Headless: command enablement vs connection state (DI-3), dialog-request routing per policy class, InfoBar surfacing, attach-dialog phase progression against a scripted runner. |
| ConfirmationDialog / attach dialog visuals | owner eye | One version, owner eyeball at the PR gate (no autonomous visual iteration). |
| End-to-end against a real daemon | owner walkthrough | M3 acceptance checklist (mirrors the M2 #32 pattern): suspend/resume/abort a task, suspend/resume/update a project, snooze/restore, attach+detach a test project on the dev machine's live BOINC 8.2.11. Every checklist assertion written against shipped code, not design intent. |

Mutation gate: Tier-1 Stryker scope stays pinned to `SnapshotBuilder.cs` (its config); whether to extend the scope to `HostControlService` is a controller/owner call at implementation time, out of this design's scope.

---

## Part 6 — PR ladder (summary; the plan doc is normative)

Protocol first (fixture-tested, no live daemon in CI), then Core, then App:

- **PR A** — GuiRpc: task/project/run-mode control ops + `FakeGuiRpcClient` extension + fixtures.
- **PR B** — GuiRpc: lookup/attach primitives + reply models + fixtures.
- **PR C** — GuiRpc + Core: `get_project_status` + tick integration + SnapshotBuilder merge (**gated on DI-5 approval**; carries the verification-sync justification).
- **PR D** — Core: `HostControlService` (control lane) + failure taxonomy + tests.
- **PR E** — Core.Machine: `AttachMachine` (F#) + FsCheck/transition tests; Core `AttachFlowRunner`.
- **PR F** — App: `ControlConfirmation.fs` policies + generic `ConfirmationDialog` + the shared InfoBar failure surface + Tasks-view ops. Owner eyeball on the dialog.
- **PR G** — App: Projects-view ops (incl. DI-2 parent-row semantics), reusing PR F's dialog + failure surface.
- **PR H** — App: run modes + snooze surface (per DI-4).
- **PR I** — App: attach dialog + flow wiring.
- **PR J** — docs: M3 owner walkthrough / acceptance checklist.

Dependencies: A→{D, F, G, H}; B→E→I; C independent (any time after DI-5 approval); F before G/H/I (shared dialog + InfoBar land in F/G). Every PR independently green; merged on the standard Codex+CI loop; **the App-facing PRs (F–I) additionally hold for owner visual eyeball before merge**.
