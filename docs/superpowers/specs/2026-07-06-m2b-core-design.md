# M2b — Lattice.Core Design

Date: 2026-07-06
Status: draft (awaiting user review)

## Context

Second of the three M2 sub-projects (M2a protocol extensions — merged, PR #6; M2c Avalonia app — next). This spec creates the `Lattice.Core` project: the layer between the per-host GUI RPC client (`Lattice.Boinc.GuiRpc`, consumed only via `IGuiRpcClient`) and the UI. It owns everything multi-host: the host registry, per-host connection lifecycle, polling cadence, reconnect/backoff, snapshot building, and the derived values the M2 views render (`docs/design/m2/README.md`).

Boundary discipline (CLAUDE.md): Core has NO UI dependencies and NO direct socket code; App contains no protocol logic and consumes Core events/snapshots only.

## Goal

After M2b, a headless consumer (test or console) can: load a host registry from disk, start monitoring, and receive — per host — connection-status changes, immutable state snapshots with all view-ready derived fields, and appended log messages. All timing-dependent behavior is testable with a fake clock and fake `IGuiRpcClient`; no live daemon needed.

## Decision record

| Question | Decision | Rationale |
|---|---|---|
| Concurrency model | One async actor loop per host (single `Task` owning the connection); no locks on the hot path | A GUI RPC connection is strictly request-reply; one loop per host is the natural shape and cannot race itself |
| Eventing | Plain .NET events raising immutable records | No Rx dependency; App marshals to `Dispatcher.UIThread` itself |
| Snapshot shape | Immutable `HostSnapshot` record, replaced wholesale each poll tick | Diff-free consumption; UI binds to the latest snapshot, no partial-mutation races |
| Password storage | Plaintext JSON in the user config directory | Matches BOINC's own `gui_rpc_auth.cfg` (plaintext); user-approved. File written with user-only ACL where the OS supports it, but no encryption theater |
| Clock | `System.TimeProvider` injected everywhere time is read or waited on | Backoff, countdowns, and deadline-at-risk are unit-testable with `FakeTimeProvider` |
| Unreachable vs Retrying | Single internal `Retrying` state; `Unreachable` is the same state at attempt ≥ 5 | The daemon doesn't distinguish them; the design's two UI states are a severity threshold, not different behavior |
| get_state refresh | On (re)connect, plus targeted refresh when a result references an unknown workunit | Full CC_STATE can be MB-sized; refreshing only when the app/workunit join misses keeps steady-state polling cheap while keeping the Application column correct for newly downloaded tasks |
| Derived view models | Core computes them (`TaskSnapshot`, `TransferSnapshot`) | Deadline-at-risk, Application join, transfer tri-state, and elapsed-column rule are business rules, not presentation; M2c binds them directly |

## Project structure

```
src/Lattice.Core/
├── Lattice.Core.csproj          # net10.0, refs Lattice.Boinc.GuiRpc + Microsoft.Bcl.TimeProvider (if needed; TimeProvider is in-box on net10.0)
├── HostConfig.cs                # host identity + credentials record
├── LatticeConfig.cs             # root config: hosts + polling interval; JSON load/save
├── HostRegistry.cs              # mutable collection over LatticeConfig, persistence, events
├── ConnectionStatus.cs          # state enum + status record
├── HostMonitor.cs               # the per-host actor: state machine + polling loop
├── HostMonitorManager.cs        # composition root: registry → set of monitors
├── HostSnapshot.cs              # immutable snapshot + TaskSnapshot/TransferSnapshot/ProjectSnapshot
├── SnapshotBuilder.cs           # raw RPC models → HostSnapshot (pure, static)
└── MessageLog.cs                # capped per-host message buffer
tests/Lattice.Tests/             # same test project; new files per unit
```

## Section 1: Configuration & registry

`HostConfig` — immutable record, one per monitored host:

```csharp
public sealed record HostConfig(
    Guid Id,               // stable identity; survives renames/re-addressing
    string Name,           // display name; defaults to Address when user left it blank
    string Address,        // hostname or IP
    int Port,              // default 31416
    string Password);      // gui_rpc_auth.cfg content; "" = attempt unauthenticated
```

`LatticeConfig` — the persisted root:

```csharp
public sealed record LatticeConfig(int PollingIntervalSeconds, IReadOnlyList<HostConfig> Hosts)
{
    public static LatticeConfig Default { get; }   // 5s, no hosts
}
```

Persistence: single JSON file at `Environment.SpecialFolder.ApplicationData`/`Lattice`/`config.json` (`%APPDATA%\Lattice\config.json` on Windows, `~/.config/Lattice/config.json` on Linux, `~/Library/Application Support/Lattice/config.json` on macOS — all via the same SpecialFolder call). `System.Text.Json`, indented, camelCase. Load tolerates a missing file (returns `Default`); a corrupt file throws — App decides how to surface it (M2c). Save is atomic: write to `config.json.tmp`, then `File.Move(overwrite: true)`.

`HostRegistry` wraps the config:

- `IReadOnlyList<HostConfig> Hosts`, `int PollingIntervalSeconds`
- `AddHost(HostConfig)`, `UpdateHost(HostConfig)` (matched by `Id`), `RemoveHost(Guid)`, `SetPollingInterval(int)` — each mutates, saves, and raises `Changed` (`EventHandler<RegistryChangedEventArgs>` carrying the kind: HostAdded/HostUpdated/HostRemoved/IntervalChanged, and the affected `HostConfig`).
- Allowed polling intervals: 2, 5, 10, 30, 60 (design Settings spec); `SetPollingInterval` rejects other values with `ArgumentOutOfRangeException`.

## Section 2: Connection state machine (`HostMonitor`)

One `HostMonitor` per host. Public surface:

```csharp
public sealed class HostMonitor : IAsyncDisposable
{
    public HostMonitor(HostConfig config, Func<IGuiRpcClient> clientFactory,
                       TimeProvider timeProvider, int pollingIntervalSeconds);

    public Guid HostId { get; }
    public ConnectionStatus Status { get; }          // latest
    public HostSnapshot? Snapshot { get; }           // latest, null before first successful poll
    public IReadOnlyList<Message> Messages { get; }  // capped buffer contents

    public event EventHandler<ConnectionStatus>? StatusChanged;
    public event EventHandler<HostSnapshot>? SnapshotUpdated;
    public event EventHandler<MessagesAddedEventArgs>? MessagesAdded;  // record MessagesAddedEventArgs(Guid HostId, IReadOnlyList<Message> Messages) — only the new messages

    public void Start();                             // idempotent
    public void RequestRefresh();                    // F5 / "Retry now": wake the loop immediately
    public void UpdateConfig(HostConfig config);     // new address/password: tear down, reconnect from scratch
    public void SetPollingInterval(int seconds);
    public ValueTask DisposeAsync();                 // cancels the loop, disposes the client
}
```

States and transitions:

```
enum HostConnectionState { Disconnected, Connecting, Authorizing, FetchingState, Connected, Retrying, AuthFailed }

Disconnected --Start()--> Connecting
Connecting   --TCP ok-->            Authorizing
Authorizing  --auth ok-->           FetchingState        (exchange_versions + get_state)
FetchingState --state cached-->     Connected            (polling loop runs)
Connected    --poll tick ok-->      Connected            (snapshot event each tick)
any of Connecting/Authorizing/FetchingState/Connected
             --I/O or protocol error--> Retrying         (schedule next attempt)
Retrying     --backoff elapsed-->   Connecting           (attempt++)
Authorizing  --<unauthorized/>-->   AuthFailed           (terminal)
Connected    --<unauthorized/> mid-poll--> Authorizing   (re-auth once; if that fails → AuthFailed)
AuthFailed   --UpdateConfig()-->    Connecting           (attempt reset)
any          --DisposeAsync()-->    Disconnected
```

`ConnectionStatus` — immutable record raised once per state transition. Core does NOT tick the retry countdown; the UI derives "in 12s" itself, once per second, from `NextAttemptAt`:

```csharp
public sealed record ConnectionStatus(
    Guid HostId,
    HostConnectionState State,
    int Attempt,                    // 0 when not retrying
    DateTimeOffset? NextAttemptAt,  // set in Retrying; UI renders "Retrying in 12s (attempt 3)"
    string? LastError,              // exception message of the last failure; tooltip on Unreachable
    VersionInfo? DaemonVersion);    // set from Connected onward
```

Backoff: delays 1, 2, 4, 8, 16, 32, 60, 60, … seconds (double, cap 60). Attempt counter resets on reaching Connected. Retries continue forever — a host that comes back is picked up without user action. UI mapping (documented for M2c): `Retrying && Attempt >= 5` renders as **Unreachable**; below that, **Retrying** with countdown. `RequestRefresh()` during Retrying skips the remaining backoff and retries immediately (design's "Retry now" link).

Auth rules: if `Password == ""`, skip the auth1/auth2 handshake entirely and rely on the daemon's localhost-unauthenticated reads (Authorizing passes through instantly); any later `<unauthorized/>` reply surfaces as AuthFailed. Mid-session `<unauthorized/>` (e.g. daemon restarted with a new password): one silent re-auth attempt with the stored password; if refused → AuthFailed.

## Section 3: Polling loop

On entering FetchingState (every (re)connect):
1. `ExchangeVersionsAsync` → store in status
2. `GetStateAsync` → cache `CcState` (projects, apps, workunits — the join tables)
3. Build and publish first snapshot → Connected

Each tick while Connected (interval = registry's `PollingIntervalSeconds`, awaited via `timeProvider`):
1. `GetCcStatusAsync`
2. `GetResultsAsync()`
3. `GetFileTransfersAsync()`
4. `GetMessagesAsync(seqno: lastSeqno)` → append to `MessageLog`, raise `MessagesAdded` if non-empty, advance `lastSeqno`
5. If any result's `WorkunitName` is missing from the cached state's workunits → `GetStateAsync()` once, re-cache (new task downloaded since connect)
6. `SnapshotBuilder.Build(...)` → replace `Snapshot`, raise `SnapshotUpdated`

Any exception in a tick → Retrying (the connection is assumed dead; GUI RPC has no error recovery mid-stream). `RequestRefresh()` while Connected wakes the loop early for an immediate tick.

`MessageLog`: ring buffer capped at 5,000 messages (design: retain last 5,000/host); `Messages` returns the current contents as a snapshot list.

## Section 4: Snapshots & derived values

All derivation lives in `SnapshotBuilder` (pure static — trivially testable):

```csharp
public sealed record HostSnapshot(
    Guid HostId,
    string HostName,
    DateTimeOffset Timestamp,            // for "Updated 3s ago"
    CcStatus CcStatus,
    IReadOnlyList<TaskSnapshot> Tasks,
    IReadOnlyList<TransferSnapshot> Transfers,
    IReadOnlyList<ProjectSnapshot> Projects);

public sealed record TaskSnapshot(
    Result Result,                       // full underlying model
    string ProjectName,                  // join ProjectUrl → CcState.Projects (fallback: url)
    string ApplicationName,              // join WorkunitName → Workunit.AppName → App.UserFriendlyName (fallbacks: AppName, then "")
    double ElapsedSeconds,               // ActiveTask?.ElapsedTime ?? Result.FinalElapsedTime  (spec'd elapsed-column rule)
    bool IsDeadlineAtRisk);              // deadline set && not ready-to-report && now + EstimatedCpuTimeRemaining > ReportDeadline

public enum TransferUiState { Active, Retrying, Queued }

public sealed record TransferSnapshot(
    FileTransfer Transfer,
    string ProjectName,                  // ProjectName if non-empty, else join via ProjectUrl
    TransferUiState UiState);            // Active = XferActive; Retrying = !XferActive && NextRequestTime > now; else Queued

public sealed record ProjectSnapshot(
    Project Project,
    int TaskCount);                      // count of this host's results for the project (nav/child-row "N tasks")
```

Cross-host aggregation (All-hosts rows, credit summing, "Varies · 50–100" share, partial-results banner) is **M2c viewmodel work** — it composes per-host snapshots and belongs with the UI scope selector, not in Core. Core's contract ends at correct per-host snapshots.

## Section 5: Composition (`HostMonitorManager`)

The single object App constructs:

- ctor: `(HostRegistry registry, Func<IGuiRpcClient> clientFactory, TimeProvider timeProvider)`
- Creates a `HostMonitor` per registry host; subscribes to `registry.Changed`: HostAdded → create+start monitor; HostRemoved → dispose monitor; HostUpdated → `monitor.UpdateConfig`; IntervalChanged → fan out `SetPollingInterval`.
- Re-raises the three monitor events unified: `StatusChanged`, `SnapshotUpdated`, `MessagesAdded` (each args already carry `HostId`).
- `IReadOnlyList<HostMonitor> Monitors` for direct reads.
- `static Task<(bool ok, string? error, VersionInfo? version)> TestConnectionAsync(HostConfig, Func<IGuiRpcClient>, CancellationToken)` — one-shot connect+auth+exchange_versions for the Settings "Test connection" button and the Add-host dialog; never touches the monitors.
- `DisposeAsync` disposes all monitors.

Threading: monitor events fire on the actor loop's thread-pool context. Documented contract: **subscribers marshal to their own context** (App uses `Dispatcher.UIThread.Post`). Events are raised sequentially per host, so per-host ordering is guaranteed.

## Test strategy

All tests in `Lattice.Tests`, no live daemon, using:
- `FakeGuiRpcClient : IGuiRpcClient` — scripted responses/exceptions per call, call log for assertions
- `Microsoft.Extensions.TimeProvider.Testing` `FakeTimeProvider` — deterministic backoff/interval control

Coverage:
1. **Config round-trip**: save→load equality; missing file → `Default`; atomic tmp-file rename; interval validation (2/5/10/30/60 only).
2. **Registry events**: each mutation raises the right kind and persists.
3. **State machine**: happy path Connecting→…→Connected with events in order; TCP failure → Retrying with 1s then 2s then 4s… cap 60; attempt reset after success; `<unauthorized/>` at auth → AuthFailed (no retry); mid-poll unauthorized → one re-auth → AuthFailed on second refusal; `UpdateConfig` from AuthFailed → reconnects; `RequestRefresh` cancels remaining backoff.
4. **Polling loop**: tick sequence issues the four RPCs; seqno advances (second tick asks `GetMessagesAsync(lastSeqno)`); unknown workunit triggers exactly one `GetStateAsync` re-fetch; tick failure → Retrying; message buffer caps at 5,000.
5. **SnapshotBuilder** (pure): application-name join incl. both fallbacks; project-name join; elapsed rule (active vs finished); deadline-at-risk true/false/edge (ready-to-report excluded, no deadline excluded); transfer tri-state (active / retrying-with-future-next-request / queued).
6. **Manager**: add/remove/update host lifecycle; TestConnectionAsync success and failure shapes.

## Acceptance

- `dotnet build -c Release -warnaserror` clean; all tests green in CI
- A smoke check wiring `HostMonitorManager` with the real client factory against the local BOINC 8.2.11 daemon receives Connected status + a populated snapshot (extend `Lattice.SmokeTest` with a `--core` mode)
- No reference from `Lattice.Boinc.GuiRpc` to `Lattice.Core` (dependency direction enforced by project refs)

## Non-goals

- UI, viewmodels, cross-host aggregation, scope selection (M2c)
- Control RPCs, attach/detach flows (M3)
- SSH tunnel transport (M4) — but `Func<IGuiRpcClient>` factory keeps the seam open
- Password encryption / OS keychain integration (revisit if users ask; BOINC itself is plaintext)
