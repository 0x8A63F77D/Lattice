# M1 Design — `Lattice.Boinc.GuiRpc` Protocol Layer

Date: 2026-07-04
Status: Approved (brainstorming session)

## Goal

A standalone, MIT-licensed .NET library implementing the BOINC GUI RPC protocol for a single host: connect, frame, authenticate, and execute the read-only RPCs needed by Lattice's dashboard. Publishable to NuGet as `Lattice.Boinc.GuiRpc`. No multi-host logic, no polling policy, no reconnect strategy — those belong to `Lattice.Core` (M2).

## Decisions made

| Decision | Choice | Rationale |
|---|---|---|
| Build vs reuse | Write from scratch | `chausner/BoincRpc` is LGPL-3.0 (incompatible with MIT packaging), stale (BOINC 7.18, .NET Standard 2.0). Used as a design reference only — no code copied. BOINC C++ sources (`lib/gui_rpc_client.cpp`, `client/gui_rpc_server_ops.cpp`) are the authoritative protocol reference. |
| RPC scope | Minimal set per CLAUDE.md M1 | Connect/frame/auth + `exchange_versions`, `get_state`, `get_cc_status`, `get_results`, `get_messages`. Control RPCs arrive in M3 when the UI can exercise them. Package ships as 0.x (API unstable). |
| Target framework | net10.0 single-target | .NET 10 is both current and LTS. Multi-targeting can be added later if demand appears. |
| API shape | Single client class | One `BoincGuiRpcClient` with one async method per RPC, returning typed models. Matches .NET ecosystem convention; honest about the strictly request-reply protocol. |
| XML strategy | Sanitize-then-XElement with hand-written per-model `Parse` | BOINC's own parser is a hand-rolled tag scanner (never validates); BoincRpc uses strict `XElement.Parse` plus ad-hoc string patches. We systematize the latter: one central sanitizer + lenient hand-written field extraction. |

## Project structure

```
Lattice.sln
├── src/Lattice.Boinc.GuiRpc/     # the library (NuGet package)
├── tests/Lattice.Tests/          # xUnit; fixtures/ holds captured reply XML
└── tools/Lattice.SmokeTest/      # console acceptance tool; never packaged
```

`Lattice.App` / `Lattice.Core` are not created in M1; they join the solution when M2 starts.

## Public API

```csharp
public sealed class BoincGuiRpcClient : IAsyncDisposable
{
    Task ConnectAsync(string host, int port = 31416, CancellationToken ct = default);
    Task<bool> AuthorizeAsync(string password, CancellationToken ct = default);
    Task<VersionInfo> ExchangeVersionsAsync(CancellationToken ct = default);

    Task<CcState> GetStateAsync(CancellationToken ct = default);
    Task<CcStatus> GetCcStatusAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Result>> GetResultsAsync(bool activeOnly = false, CancellationToken ct = default);
    Task<IReadOnlyList<Message>> GetMessagesAsync(int seqno = 0, CancellationToken ct = default);

    ConnectionState State { get; }       // Disconnected / Connected / Authorized
    VersionInfo? DaemonVersion { get; }  // set after ExchangeVersionsAsync; callers gate newer RPCs on it
}
```

Public surface beyond this: model records, `ConnectionState`, the exception types below. Everything else (`RpcConnection`, XML sanitizer, parse helpers) is `internal` with `InternalsVisibleTo("Lattice.Tests")`.

Concurrency: all RPCs on a connection are serialized through a `SemaphoreSlim`. The protocol is strictly request-reply; concurrent callers queue rather than fault.

## Connection & framing (`internal RpcConnection`)

- `TcpClient` + `NetworkStream`. Read loop accumulates bytes until `0x03`, strips the terminator, yields the reply. Requests are sent with a trailing `0x03`.
- Request XML is assembled by hand (string building), never via XElement serialization, guaranteeing self-closing tags have no space before the slash (`<auth1/>`, never `<auth1 />`) — the daemon's scanner chokes on the latter.
- Encoding: ASCII out; UTF-8 in with replacement fallback (invalid bytes never throw).

## Authentication

1. Send `<auth1/>` → receive nonce.
2. Send `<auth2><nonce_hash>hex(MD5(nonce + password))</nonce_hash></auth2>` (lowercase hex).
3. `<authorized/>` → return `true`, `State = Authorized`. `<unauthorized/>` → return `false` (wrong password is a normal outcome, not an exception).

Any RPC may return `<unauthorized/>` at any time; every reply path checks for it.

## Error model

| Situation | Behavior | Caller's move |
|---|---|---|
| Network failure / timeout | `BoincConnectionException` (wraps IO exceptions) | Connection is dead; reconnect (Core's job in M2) |
| Reply unparseable after sanitizing | `BoincProtocolException`, carries raw payload snippet | Diagnose; likely a library bug or novel daemon quirk |
| `<unauthorized/>` on any RPC | `BoincUnauthorizedException` | Re-run `AuthorizeAsync` |
| `<error>` tag in reply | `BoincRpcException` carrying the raw error text | Display text only — never branch on error wording (it changes between versions); branch only on structural tags |

Explicitly out of scope for the library: auto-reconnect, retry, backoff, polling. Single-host, single-connection semantics; policy lives in `Lattice.Core`.

## XML sanitizer (`internal static SanitizeXml`)

One pure function applied to every reply before `XElement.Parse`:

1. Strip the illegal `<?xml version="1.0" encoding="ISO-8859-1" ?>` declaration some RPCs emit (BOINC bug #1509).
2. Strip control characters other than legal XML whitespace.
3. Escape bare `&` without corrupting already-valid entities.

Directly unit-tested against malformed fixtures.

## Models & parsing conventions

Models (all `public sealed record`, immutable): `VersionInfo`, `CcState` (aggregating `Projects`, `Apps`, `AppVersions`, `Workunits`, `Results`, `HostInfo`), `Project`, `App`, `AppVersion`, `Workunit`, `Result`, `HostInfo`, `CcStatus`, `Message`. Enums (`RunMode`, `SuspendReason`, `ResultState`, `MessagePriority`, …) mirror the integer constants in BOINC sources.

Each model has `internal static Parse(XElement)` following these conventions:

1. **Missing fields never throw.** Fields come and go across BOINC versions; use type defaults (0, empty string, null timestamp). Partial data beats a failed `get_state`.
2. **Empty tag = boolean true** (`<active/>`). Shared `GetBool` helper.
3. **Timestamps** are Unix-epoch-seconds doubles → `DateTimeOffset`.
4. **Numeric parsing** always uses `CultureInfo.InvariantCulture`.
5. **Unknown child elements are ignored** — newer daemons must not break older library versions.
6. **Unknown enum integers are preserved** via direct cast (C# enums admit undefined values); never thrown on, never collapsed to a synthetic Unknown.

## Testing

**Fixture tests (bulk of coverage; no network, no daemon, CI-safe):**
- `tests/Lattice.Tests/fixtures/` holds captured reply XML (`get_state.xml`, `get_cc_status.xml`, auth exchanges, deliberately-malformed samples). Initial fixtures are constructed from BOINC source/docs and BoincRpc's observed behavior; once a local daemon is installed, the smoke test doubles as a capture tool to replace/extend them with real payloads.
- Every model's `Parse` asserted field-by-field against fixtures; `SanitizeXml` asserted against malformed samples; malformed input must never throw past the sanitizer.
- Framing tested over in-memory streams: fragmented arrival, partial frames, stream termination mid-reply.
- Auth MD5 asserted against known vectors derived from BOINC sources.

**Smoke test (acceptance):** connect to `localhost:31416` → password from `gui_rpc_auth.cfg` or CLI arg → authorize → `exchange_versions` → run all five RPCs → print typed results. Non-zero exit with diagnostics on any failure.

**Prerequisite:** the dev machine does not yet have BOINC installed. Installing the BOINC client and attaching at least one project (e.g. World Community Grid or Einstein@Home for steady task supply) is a required setup step before smoke-test acceptance. Fixture tests do not depend on it.

## Definition of done

1. All fixture tests pass (CI-runnable, zero external dependencies).
2. Smoke test passes end-to-end against a real local daemon.
3. `dotnet pack` produces a well-formed package: ID `Lattice.Boinc.GuiRpc` (availability on nuget.org verified), MIT license, README, SourceLink. Actually publishing to nuget.org is optional and not an acceptance gate.
