# Lattice.Boinc.GuiRpc

Async .NET client for the BOINC GUI RPC protocol (the protocol BOINC Manager
uses to talk to the `boinc` core client on TCP port 31416).

Part of [Lattice](https://github.com/0x8A63F77D/Lattice), a multi-host BOINC
dashboard. This package is single-host: one client, one connection, typed
models. Polling, reconnect, and multi-host aggregation live upstream.

API is 0.x and unstable.

## Protocol notes: control operations

Beyond the read RPCs, the client exposes the daemon's control operations:
task suspend/resume/abort (`PerformTaskOpAsync` — a task is addressed by
project URL + result name), project suspend/resume/update/detach
(`PerformProjectOpAsync` — addressed by master URL), and run-mode changes
(`SetModeAsync` — CPU/GPU/network lanes). All control operations require
authorization (`AuthorizeAsync`).

Run-mode semantics: a zero duration makes the mode permanent; a positive
duration is a temporary override the daemon reverts on its own (snooze =
`Never` + duration). `RunMode.Restore` cancels a temporary override and is
request-only — it never appears in `get_cc_status` replies. The reply's
per-lane `*_mode_perm` / `*_mode_delay` fields (surfaced on `CcStatus`)
carry the permanent mode and the seconds remaining on a temporary override.

Wire trivia worth knowing: the daemon's XML parser is hand-rolled — mode
tags must be self-closing with no space before the slash (`<always/>`,
never `<always />`). Control ops reply `<success/>`, `<error>text</error>`
(surfaced as `BoincRpcException`; the text is display-only and never worth
branching on — wording changes between versions), or `<unauthorized/>`
(`BoincUnauthorizedException`).

## Protocol notes: account lookup / attach flow

Attaching to a project is the protocol's one asynchronous flow. All four RPCs
must run on the **same connection** — the daemon tracks the pending lookup per
connection.

1. `RequestAccountLookupAsync(url, email, password)` sends `lookup_account`.
   The password itself never goes on the wire: the request carries
   `MD5(password + lowercased email)`.
2. Poll `PollAccountLookupAsync()`. `ErrorNum` −204 (`BoincErrorCodes.InProgress`)
   and −199 (`BoincErrorCodes.Retry`) mean poll again; `0` delivers the account
   authenticator; any other value is the failure code. A bare `<error>` reply to
   this poll is the project server's own failure passed through by the daemon —
   it is returned as `ErrorNum = -1` with the text, not thrown, because it means
   "lookup failed", not "RPC failed".
3. `RequestProjectAttachAsync(url, authenticator, projectName, emailAddr)`
   sends `project_attach`; users with an account key can skip straight here.
4. One `PollProjectAttachAsync()` yields the verdict — the daemon attaches
   synchronously, so there is no in-progress phase. `ErrorNum 0` means the
   daemon **accepted** the attach; it does not verify the authenticator, which
   is only checked on the daemon's first scheduler RPC.

Poll cadence and timeout are deliberately not wrapped here: loop policy belongs
to the caller (in Lattice, the Core layer's attach state machine).
