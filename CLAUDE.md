# Lattice

A multi-host BOINC monitoring dashboard. Cross-platform desktop app built with Avalonia and Fluent Design.

**Positioning:** a modern, native, cross-platform multi-host BOINC manager. The established tool in this niche is BOINCTasks (efmer — GPL, actively maintained): a mature Windows-native classic plus a cross-platform Electron rewrite (boinctasks-js) not yet at the classic's feature parity. Lattice's differentiators are a modern native Fluent UI, first-class data visualization (credit history, task timelines, per-project throughput), and multi-host aggregation in a native desktop app rather than an Electron port. NOT another single-machine BOINC Manager replacement — that niche is filled (official Manager, Fresco; each can point at a single remote host over GUI RPC but holds only one connection at a time, so neither aggregates across hosts).

Lattice is a GUI RPC *client*. It does not schedule, download, or compute anything. All real work is done by the official BOINC core client (`boinc` daemon) running on each host; Lattice connects to it over TCP and renders state.

## Solution structure

```
lattice/
├── Lattice.App/            # Avalonia UI (views, viewmodels, theming)
├── Lattice.Core/           # Domain: host registry, polling scheduler, state cache + diff. NO UI deps, NO direct socket code.
├── Lattice.Core.Machine/    # Pure F# decision core for HostMonitor (HostMachine.step). No I/O, no deps.
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
- Charting: **LiveCharts2** (`LiveChartsCore.SkiaSharpView.Avalonia`, `2.1.0-dev` line) — adopted for M4 (ruling on #148). Two-layer verification gate: chart content pixel-gated via `InMemorySkiaSharpChart` PNG snapshots (machine gate); the Avalonia-hosted layer masked in visual tests + owner eyeball. Flip-back trigger to ScottPlot recorded on #148.
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

- **Avalonia is not WPF.** This is the #1 hallucination risk. Consult the Avalonia Build MCP server (`avalonia-docs`) for API usage before writing XAML or framework-touching C#.
- Known dialect traps: `StyledProperty<T>` / `DirectProperty` (not `DependencyProperty`); Avalonia selectors in `<Style Selector="...">` (not WPF triggers); `.axaml` files; `x:DataType` + `{CompiledBinding}` preferred everywhere — enable compiled bindings project-wide and treat binding errors as build failures.
- Use FluentAvalonia controls (`FluentAvalonia.UI.Controls`) over hand-rolled equivalents: NavigationView for the shell, TabView, InfoBar/InfoBadge for status surfaces.
- Window materials: request Mica via `TransparencyLevelHint` on Windows 11, with solid-color fallback on macOS/Linux/Win10. Never let a feature depend on the material being present.
- Async all the way down: socket I/O and RPC calls are async; ViewModels marshal to the UI thread via `Dispatcher.UIThread`. No sync-over-async, no blocking the UI thread on RPC.

## F# style canon (adopted 2026-07-11, issue #38)

Practical, readable, .NET-idiomatic functional style — not purity maximalism. Purity is judged at the function boundary (referential transparency; mutation never escaping), not by a token blacklist. Idiom review against this canon is a blocking review step for F# changes.

- Pipelines of small named functions. Data-last parameter order for F#-internal helpers; C#-consumed signatures may stay consumer-shaped.
- Prefer semantically specific combinators (`map` / `filter` / `choose` / `collect` / `groupBy` / `mapFold`) over `fold`; `fold` is the last resort. An accumulator that wants to be mutable is a signal to restructure the data (ops-as-values, state threaded through the fold) — not to reach for `ResizeArray`.
- No wildcard `| _ ->` on domain DUs outside predicate lambdas. DU totality is why F# was chosen; a wildcard silently defeats the compiler check when a case is added. (`function X -> true | _ -> false` predicates are the acceptable form.)
- `Option` for absence, `Result` with typed errors for expected failure; convert exception-style .NET APIs at the boundary.
- Seq re-enumeration is a correctness trap, not a style point: materialize expensive sources at an explicit point before multi-pass use.
- Sanctioned imperative kernels (pt 24): a perf-relevant algorithm kernel may use imperative collections when the mutation is confined inside the function, the external interface is pure, and a short boundary comment states the exception (reference: `tests/Lattice.Verification/Explorer.fs`).
- Review gate: `mutable` / `<-` / `ResizeArray` / imperative loops in F# domain logic are a review-blocking finding unless covered by the sanctioned-kernel rule. Mutation-as-simulation in test oracles is acceptable when it aids clarity.
- Plan-time rules (the primary fix — with transcription-tier implementers the plan snippet IS the product): state the input→output relation declaratively first; pick the recursion scheme (fold / unfold / map / structural recursion) up front and write the signature in immutable terms; plan-doc F# is born pure — there is no "imperative sketch now, clean up later" tier.

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
- **Verification sync rule (hard workflow contract):** any semantic change to `src/Lattice.Core/HostMonitor.cs` must update, in the SAME commit: the F# executable spec (`tests/Lattice.Verification/`), the Promela model (`verification/HostMonitor.pml`), and the shared-state inventory + probe-point list. All code here is AI-written, so model-code sync is the author's obligation at write time — never deferred to review. A commit touching HostMonitor semantics without touching the verification artifacts must state in its message why no model change is needed. The F# executable spec executes the production `HostMachine.step` directly (functional-core restructure 2026-07-08); the spec-sync leg is by-construction for decision logic, still manual for shell changes.

### Cross-PR workflow lessons (durable; migrated from the retired SDD ledger, 2026-07-11)

- Red-first + mutation falsification is mandatory on every fix and every reviewer pass; a reviewer independently repeats the falsification. False greens have been caught at every stage this discipline was applied.
- Repeated same-class findings ⇒ restructure the invariant, don't re-patch (escalation ladder in user memory; TasksOverlayPolicy's taxonomy restructure on repair round 3 is the reference case).
- Pure decision logic is extracted into policy modules from birth (PartialBarPolicy / TasksOverlayPolicy / ColumnVisibilityPolicy pattern), with exhaustive transition-table tests.
- Deterministic UI tests: never wait on transient states or booleans that can go true early — settle on the EXPECTED TEXT or the fake's observed calls. Wall-clock settles are banned (HeadlessSync.WaitUntilAsync).
- Test dispatchers must reproduce production ordering semantics: lock-only serialization is not `Dispatcher.UIThread.Post` — use the deferred-queue dispatcher (QueueUiDispatcher + Drain) for multi-monitor fixtures.
- Visual bugs: geometry/pixel probing (headless Skia) is the verification bar; a fix without end-state visual verification has shipped broken once.
- Codex review loop: verdicts count only on the FINAL commit; a review object with boilerplate body is not clean — re-poll review threads ≥60 s after it posts (inline findings share its timestamp). Quota errors mean the round will never come: get the reset time from the user. Reviews route through the github MCP tools; pr-monitor is status-only (`.claude/agents/pr-monitor.md`).
- Design doc (`docs/design/m2/README.md`) is authoritative over plan wording when they conflict — cite the design line when deviating from a plan detail (window-width breakpoints case, PR #28).
- Scheduled tasks (client-side cron): pre-approve their tools via "Run now" at creation, or unattended runs die on permission prompts.

### Mutation gates (Stryker.NET pilot, issue #77)

- Tier 0: the plain `dotnet test` per-PR gate — unchanged, still the default quality bar.
- Tier 1 (test admission): PR job path-filtered to the mutation scope + its test project, runs Stryker incrementally (`--since:origin/main`). **ENFORCING as of #111 (calibration complete):** `break = 80`, set from the observed **88.89% calibration floor** (not guessed up front) — a PR whose mutation score drops below 80 fails. The 3 SnapshotBuilder survivors surfaced during calibration were **adjudicated genuine coverage gaps and killed** (`stryker-config.json` thresholds `high 90 / low 80 / break 80`). The companion screenshot gate `visual-tests.yml` is also **enforcing** post-#82 calibration (`MeanErrorThreshold 1.0` / `PixelErrorCountGuard 400`; macOS-only, skipped on the cross-platform `ci.yml`).
- Tier 2 (regression audit): nightly full run over the scope; posts/updates a score comment on #77 (`scripts/post-mutation-report.sh`). Never blocks PRs.
- Scope is pinned in `tests/Lattice.Tests/stryker-config.json` (currently `src/Lattice.Core/SnapshotBuilder.cs`) — never repo-wide. Two tooling limits shape it: Stryker.NET has no F# support (`Lattice.Core.Machine`, `Lattice.App.Aggregation` excluded), and its Roslyn-only recompile cannot build Avalonia projects (XamlIl-injected `InitializeComponent` doesn't exist ⇒ CS0103), so the `Lattice.App` ViewModels policy modules stay out until extracted to a non-UI assembly.
- Survivor adjudication is a controller judgment call: equivalent mutants exist and cannot be killed; adding assertions solely to raise the score is banned — that is false-green in a new costume.

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