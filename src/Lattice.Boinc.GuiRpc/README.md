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
