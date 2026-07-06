# M2a — GuiRpc Protocol Extensions Design

Date: 2026-07-04
Status: approved

## Context

M2 (read-only dashboard) is split into three sub-projects, each with its own spec → plan → implementation cycle:

- **M2a (this spec)** — extend `Lattice.Boinc.GuiRpc` with the fields and the one RPC the dashboard needs
- **M2b** — `Lattice.Core`: host registry, per-host connection state machine, polling scheduler, snapshots
- **M2c** — `Lattice.App`: Avalonia shell + five views per `docs/design/m2/`

The data requirements come from the M2 design handoff (`docs/design/m2/README.md`, "New M2 data requirements") and the Codex fixture audit on PR #5. Every field below was verified against the repository fixtures and the BOINC reference parser (`lib/gui_rpc_client_ops.cpp`, `FILE_TRANSFER::parse`).

## Goal

After M2a, `Lattice.Boinc.GuiRpc` supplies every value the M2 dashboard renders; Core and App layers never touch raw XML.

## Decision record

| Question | Decision | Rationale |
|---|---|---|
| Version gating | None | All extended fields and `get_file_transfers` predate BOINC 7; nothing to gate |
| Interface extraction | Extract `IGuiRpcClient` from `BoincGuiRpcClient` now | M2b's state machine is tested against a fake client; extracting while the surface is small is cheap |
| Transfer parsing shape | Flat, tags accepted at any depth inside `<file_transfer>` | Mirrors the reference parser, which treats `persistent_file_xfer`/`file_xfer` as presence markers and parses children in one flat loop |
| Package version | Minor bump (additive API only) | New fields on existing records are breaking for positional construction, but the package has no external consumers yet; minor is honest enough pre-1.0 |

## Section 1: `Result` model additions

Four new fields, all direct children of `<result>` (NOT inside `<active_task>` — confirmed against `tests/Lattice.Tests/fixtures/get_results.xml`):

| Property | XML tag | Type | Notes |
|---|---|---|---|
| `EstimatedCpuTimeRemaining` | `estimated_cpu_time_remaining` | `double` | Seconds; feeds the Remaining column and the deadline-at-risk rule (computed in M2b) |
| `FinalElapsedTime` | `final_elapsed_time` | `double` | Seconds; Elapsed column for rows without `<active_task>` (finished/uploading/ready-to-report) |
| `VersionNum` | `version_num` | `int` | e.g. `218` = 2.18; versioned application labels |
| `PlanClass` | `plan_class` | `string` | Optional in daemon output; default `""` |

`Platform` (`platform`) is also present in `get_results` output but is not rendered by any M2 view; it is NOT added (YAGNI — revisit if a view needs it).

Elapsed-column rule for downstream layers: use `ActiveTask.ElapsedTime` when `ActiveTask` is present, else `FinalElapsedTime`.

Compatibility fallbacks (mirroring the reference `RESULT::parse`): when `final_elapsed_time` is absent or zero while `final_cpu_time` is nonzero, `FinalElapsedTime` takes the CPU time; likewise `ActiveTask.ElapsedTime` falls back to `current_cpu_time`. Old daemons report only CPU time.

## Section 2: `Project` model addition

| Property | XML tag | Type | Notes |
|---|---|---|---|
| `ResourceShare` | `resource_share` | `double` | From `get_state` project blocks; Projects-view share column/bars |

## Section 3: `FileTransfer` model + `get_file_transfers` RPC

New RPC: `Task<IReadOnlyList<FileTransfer>> GetFileTransfersAsync(CancellationToken ct = default)` — request `<get_file_transfers/>`, reply `<file_transfers>` containing `<file_transfer>` elements.

`FileTransfer` record, fields per the reference parser:

| Property | XML tag | Type | Notes |
|---|---|---|---|
| `Name` | `name` | `string` | File name |
| `ProjectUrl` | `project_url` | `string` | Join key to `Project` |
| `ProjectName` | `project_name` | `string` | Display fallback |
| `Nbytes` | `nbytes` | `double` | Total size; Progress column denominator |
| `Status` | `status` | `int` | Raw daemon status code |
| `IsUpload` | `is_upload` (fallback: `generated_locally`) | `bool` | Direction column; older daemons emit only `generated_locally` |
| `NumRetries` | `num_retries` | `int` | Retrying state ("attempt N") |
| `FirstRequestTime` | `first_request_time` | `DateTimeOffset?` | Epoch seconds |
| `NextRequestTime` | `next_request_time` | `DateTimeOffset?` | Epoch seconds; retry countdown source |
| `TimeSoFar` | `time_so_far` | `double` | Seconds of active transfer time |
| `BytesXferred` | `bytes_xferred` (fallback: `last_bytes_xferred`) | `double` | Progress numerator; the daemon emits `bytes_xferred` inside `<file_xfer>` while active and `last_bytes_xferred` inside `<persistent_file_xfer>` between attempts — take `bytes_xferred` when present, else `last_bytes_xferred` |
| `FileOffset` | `file_offset` | `double` | Resume offset |
| `XferSpeed` | `xfer_speed` | `double` | Bytes/sec; Speed column |
| `ProjectBackoff` | `project_backoff` | `double` | Seconds |
| `PersXferActive` | presence of `<persistent_file_xfer>` | `bool` | Transfer attempt in progress |
| `XferActive` | presence of `<file_xfer>` | `bool` | Socket currently open; live speed is meaningful |

Parsing rule (lenient, mirrors reference): accept every tag above at ANY depth inside `<file_transfer>` — the daemon nests `is_upload`, `num_retries`, `next_request_time`, `time_so_far` inside `<persistent_file_xfer>`, and `bytes_xferred`-family tags inside `<file_xfer>`, but the reference client parses them flatly and so do we (`XElement.Descendants` lookup instead of `Element`).

Derived view states (documented here, implemented in M2b/M2c): Active = `XferActive`; Retrying = not active and `NextRequestTime` is in the future; Queued = otherwise. (`PersXferActive` merely means the transfer is pending — it is true in all three states on a live daemon.)

## Section 4: `IGuiRpcClient` interface

Extract an interface covering the existing public RPC surface plus the new call:

```csharp
public interface IGuiRpcClient : IAsyncDisposable
{
    Task ConnectAsync(string host, int port = 31416, CancellationToken ct = default);
    Task<bool> AuthorizeAsync(string password, CancellationToken ct = default);
    Task<VersionInfo> ExchangeVersionsAsync(CancellationToken ct = default);
    Task<CcState> GetStateAsync(CancellationToken ct = default);
    Task<CcStatus> GetCcStatusAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Result>> GetResultsAsync(bool activeOnly = false, CancellationToken ct = default);
    Task<IReadOnlyList<Message>> GetMessagesAsync(int seqno = 0, CancellationToken ct = default);
    Task<IReadOnlyList<FileTransfer>> GetFileTransfersAsync(CancellationToken ct = default);
}
```

`BoincGuiRpcClient` implements it; no behavioral change. M2b consumes only the interface.

## Test strategy

- Extend the `get_results.xml` fixture assertions to cover the four new `Result` fields (values already present in the fixture).
- Add a `plan_class`-absent case asserting the `""` default.
- `get_state.xml`: assert `ResourceShare` on both project entries (add the tag to the fixture if missing).
- New fixture `fixtures/get_file_transfers.xml`, hand-built against the reference parser with three entries: an active upload (`file_xfer` present, live speed), a retrying download (`persistent_file_xfer` with `next_request_time` in the future, no `file_xfer`), and a queued entry (neither block). Asserts flat-parse leniency (nested tags found) and the `generated_locally` fallback.
- Smoke test (`Lattice.SmokeTest`): add a transfers section; verify against the local BOINC 8.2.11 daemon (output may legitimately be empty — print the count).

## Acceptance

- All fixture tests green (`dotnet test -c Release`)
- Smoke test dumps transfers (or "0 transfers") against the live local daemon without error
- `Lattice.Boinc.GuiRpc` version bumped (0.1.x → 0.2.0), still packs cleanly

## Non-goals

- No connection-state machine, polling, or multi-host logic (M2b)
- No UI (M2c)
- No `Platform` on `Result`, no transfer-control RPCs (`retry_file_transfer`, `abort_file_transfer` — M3)
