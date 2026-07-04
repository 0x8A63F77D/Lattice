# Lattice.Boinc.GuiRpc

Async .NET client for the BOINC GUI RPC protocol (the protocol BOINC Manager
uses to talk to the `boinc` core client on TCP port 31416).

Part of [Lattice](https://github.com/0x8A63F77D/Lattice), a multi-host BOINC
dashboard. This package is single-host: one client, one connection, typed
models. Polling, reconnect, and multi-host aggregation live upstream.

API is 0.x and unstable.
