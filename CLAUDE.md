# Lattice

A multi-host BOINC monitoring dashboard. Cross-platform desktop app built with Avalonia and Fluent Design.

**Positioning:** open-source alternative to BOINCTasks (closed-source, Windows-centric multi-host manager). NOT another single-machine BOINC Manager replacement — that niche is filled (official Manager, Fresco). Differentiators: multi-host aggregation, data visualization (credit history, task timelines, per-project throughput), modern Fluent UI.

Lattice is a GUI RPC *client*. It does not schedule, download, or compute anything. All real work is done by the official BOINC core client (`boinc` daemon) running on each host; Lattice connects to it over TCP and renders state.

## Solution structure

```
lattice/
├── Lattice.App/            # Avalonia UI (views, viewmodels, theming)
├── Lattice.Core/           # Domain: host registry, polling scheduler, state cache + diff. NO UI deps, NO direct socket code.
├── Lattice.Boinc.GuiRpc/   # Protocol layer: connection, auth, RPC ops, strongly-typed models. Single-host semantics only.
└── Lattice.Tests/
```

Boundary discipline:
- `Lattice.Boinc.GuiRpc` knows nothing about multiple hosts, polling policy, or the app. It is publishable as a standalone NuGet package (that is the M1 deliverable). Keep it that way.
- `Lattice.Core` owns multi-host aggregation, polling cadence, reconnect/backoff, and state diffing. It depends on GuiRpc, never the reverse.
- `Lattice.App` contains no protocol logic. ViewModels consume `Lattice.Core` observables/events.

## Tech stack

- .NET 10, C# (latest LTS at project start; verify)
- Avalonia 12.x
- FluentAvaloniaUI 3.x (NuGet `FluentAvaloniaUI`, requires Avalonia >= 12) — Fluent 2 theming + WinUI-ported controls (NavigationView, TabView, InfoBar, InfoBadge, NumberBox, TaskDialog)
- MVVM via CommunityToolkit.Mvvm (source-generated `[ObservableProperty]`, `[RelayCommand]`)
- Charting: evaluate LiveCharts2 vs ScottPlot vs OxyPlot when M4 starts; do not commit early
- Testing: xUnit. Protocol layer must be testable against canned XML fixtures without a live daemon.

## BOINC GUI RPC protocol — established facts

Reference implementations (authoritative over any wiki):
- BOINC repo: `lib/gui_rpc_client.cpp`, `client/gui_rpc_server_ops.cpp`
- .NET binding: `chausner/BoincRpc` on GitHub — **evaluate this before writing from scratch.** If maintained/adequate: use or fork. If stale: treat as same-language reference implementation.

Wire protocol:
- TCP, default port **31416**, localhost by default
- XML request/reply. Request wrapped in `<boinc_gui_rpc_request>`, reply in `<boinc_gui_rpc_reply>`. Each message terminated by byte `\x03`. Framing = accumulate until 0x03.
- Strictly request-reply on a persistent connection. No pipelining. No server push — all UI state comes from polling.
- **Parser landmine:** self-closing tags must have NO space before the slash. Send `<authorized/>`, never `<authorized />`. The C++ parser on the other end is not a real XML parser.
- BOINC's XML output is not guaranteed strictly compliant (historically unescaped chars in message bodies). Parse leniently; never use a strict validating parser.

Auth:
- Challenge-response: send `<auth1/>` → receive nonce → send `<auth2><nonce_hash>MD5(nonce + password)</nonce_hash></auth2>` → `<authorized/>` or `<unauthorized/>`.
- Password lives in `gui_rpc_auth.cfg` in the BOINC data directory (auto-generated on first run). Some read-only ops are unauthenticated for localhost connections only.
- Any RPC may return `<unauthorized/>` at any time — handle it everywhere, trigger re-auth.
- **Never parse error message text.** Wording changes between versions. Branch only on structural tags (`<error>`, `<unauthorized/>`, `<success/>`).

Versioning:
- The protocol has no versioned API. Call `exchange_versions` after connect, store the daemon version, gate newer RPCs on it. Target BOINC 8.x; degrade gracefully on older.

State model (this drives Core's design):
- `get_state` returns the full CC_STATE (projects, apps, app_versions, workunits, results). Can be several MB on busy hosts. Call once per connection (and on reconnect), cache it.
- Steady-state polling uses cheap deltas: `get_cc_status` (run modes, network, suspend reasons), `get_results` (task list w/ progress), `get_messages` with a `seqno` param (returns only messages with seqno > given; monotonic).
- Official Manager polls at ~1s for the visible tab. Lattice polls per-host with configurable cadence; back off aggressively for unreachable hosts (exponential, capped).
- Project attach flow is the one async part: `lookup_account` → poll `lookup_account_poll` → `project_attach`. Model it as such.

Remote hosts:
- Daemon refuses remote connections unless `remote_hosts.cfg` lists the client IP or `cc_config.xml` sets `allow_remote_gui_rpc`.
- **No transport encryption.** Challenge-response only protects the password itself. Cross-network management realistically requires an SSH tunnel. A built-in tunnel manager (via SSH.NET) is a candidate differentiator feature — design Core's connection abstraction so a tunnel can wrap the TCP endpoint transparently.

## Avalonia coding rules

- **Avalonia is not WPF.** This is the #1 hallucination risk. Always consult the Avalonia Build MCP server (`avalonia-docs`) for API usage before writing XAML or framework-touching C#. When in doubt between a WPF-ism and an Avalonia-ism, look it up — do not guess.
- Known dialect traps: `StyledProperty<T>` / `DirectProperty` (not `DependencyProperty`); Avalonia selectors in `<Style Selector="...">` (not WPF triggers); `.axaml` files; `x:DataType` + `{CompiledBinding}` preferred everywhere — enable compiled bindings project-wide and treat binding errors as build failures.
- Use FluentAvalonia controls (`FluentAvalonia.UI.Controls`) over hand-rolled equivalents: NavigationView for the shell, TabView, InfoBar/InfoBadge for status surfaces.
- Window materials: request Mica via `TransparencyLevelHint` on Windows 11, with solid-color fallback on macOS/Linux/Win10. Never let a feature depend on the material being present.
- Async all the way down: socket I/O and RPC calls are async; ViewModels marshal to the UI thread via `Dispatcher.UIThread`. No sync-over-async, no blocking the UI thread on RPC.

## Design direction

- Fluent 2 idiom, information-dense utility aesthetic. Visual references: Windows 11 Task Manager, Dev Home. This is a monitoring tool — density and scanability over whitespace.
- Light + dark theme from day one (FluentAvaloniaTheme handles system sync and accent color).
- Data visualization is a first-class differentiator, not decoration: credit history curves, task timeline (gantt-ish per host), per-project throughput. But charts arrive in M4 — do not front-load them.
- No web-frontend idioms. This is a native XAML app; do not reach for HTML/CSS mental models.

## Tooling & workflow

- **Avalonia Build MCP** (remote HTTP, free): docs search, API lookup, expert rules. Rule: consult it for any Avalonia API question. Endpoint: `https://docs-mcp.avaloniaui.net/mcp`.
- **Rider MCP server** (built into Rider 2025.2+): use it for builds (structured compile errors) and rename refactorings (semantic, project-wide). If tool registration flakes out, fall back to `dotnet build` and parse output.
- Avalonia DevTools MCP (live visual tree, screenshots) is paid (Avalonia Plus) and deliberately deferred; do not assume its availability.
- Small commits, conventional messages. Protocol code changes must come with fixture-based tests in the same commit.

## Milestones

**M1 — Protocol layer** (`Lattice.Boinc.GuiRpc`)
Connect, frame, auth, `exchange_versions`, `get_state`, `get_cc_status`, `get_results`, `get_messages`. Strongly-typed models. Fixture-based unit tests. Acceptance: a console smoke test authenticates against a local BOINC daemon and dumps typed state. Publishable to NuGet (check ID availability first).

**M2 — Read-only dashboard**
Single + multi-host: task list with progress, project list, transfers, message log. Polling scheduler with per-host state machines (connected / retrying / unreachable) in Core. NavigationView shell, Fluent theming, Mica where available.

**M3 — Control operations**
Suspend/resume (task, project, global run modes), task abort, project update/attach/detach (incl. the async lookup_account flow), snooze. Confirmation UX for destructive ops.

**M4 — Differentiators**
Charts (credit history, task timeline, throughput). SSH tunnel manager for remote hosts. Host groups. Notification surface (task failures, unreachable hosts) via InfoBar/tray.

## Naming

- Root namespace `Lattice`. Projects as listed above.
- The protocol package ships with a searchable ID (`Lattice.Boinc.GuiRpc`); avoid colliding with the existing `BoincRpc` NuGet ID.
- App display name: **Lattice**. Repo tagline: "A multi-host BOINC dashboard."