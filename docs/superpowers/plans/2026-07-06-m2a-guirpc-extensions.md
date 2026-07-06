# M2a — GuiRpc Protocol Extensions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extend `Lattice.Boinc.GuiRpc` with the four `Result` fields, `Project.ResourceShare`, a `FileTransfer` model + `get_file_transfers` RPC, and an `IGuiRpcClient` interface, so the M2 dashboard layers never touch raw XML.

**Architecture:** Additive changes to the existing protocol library. New fields extend existing records in place; `FileTransfer` follows the established record + `internal static Parse(XElement)` pattern but parses tags at any depth (mirroring BOINC's flat reference parser); the interface is extracted verbatim from the current public surface.

**Tech Stack:** .NET 10, xUnit, canned XML fixtures (no live daemon in unit tests), `System.Xml.Linq`.

**Spec:** `docs/superpowers/specs/2026-07-04-m2a-guirpc-extensions-design.md`

## Global Constraints

- Every new shell first runs: `export PATH="/c/Program Files/dotnet:/c/Program Files/GitHub CLI:$PATH" HTTPS_PROXY="http://192.168.1.192:10090" HTTP_PROXY="http://192.168.1.192:10090"` (Git Bash)
- Working directory `D:/0x8A63F77D/Documents/GitHub/Lattice`, branch `m2a-guirpc-extensions`
- Local dotnet output is localized Chinese; the test success line is `已通过!` — judge by the result line, not exit codes after pipes
- Everything committed (code, comments, commit messages) is in English
- Repo builds with `-warnaserror` in CI: no unused fields, no missing XML-doc on public members if the project enforces it (it currently does not; match existing style — `///` docs on public records and methods)
- Fixture files are covered by the glob `<Content Include="fixtures/**">` in `Lattice.Tests.csproj` — adding a file needs no csproj edit
- All new public API must match the spec exactly (names, types, defaults); M2b consumes these signatures

---

### Task 1: `Result` model additions

**Files:**
- Modify: `src/Lattice.Boinc.GuiRpc/Models/Result.cs`
- Test: `tests/Lattice.Tests/ResultTests.cs`

**Interfaces:**
- Consumes: existing `Result` record, `ParseHelpers.GetDouble/GetInt/GetString`
- Produces: `Result.EstimatedCpuTimeRemaining` (double), `Result.FinalElapsedTime` (double), `Result.VersionNum` (int), `Result.PlanClass` (string, `""` default) — Task 6's smoke test and M2b rely on these exact names

- [ ] **Step 1: Add failing assertions to both existing tests**

In `tests/Lattice.Tests/ResultTests.cs`, extend the two test bodies (fixture values already exist in `fixtures/get_results.xml`; the second entry has none of the new tags, so it asserts the defaults):

```csharp
    [Fact]
    public void Parses_running_task_with_active_task()
    {
        Result r = Load()[0];
        Assert.Equal("h1_0437.60_O3aC01Cl1In0__O3MDFV2g_G34731_437.70Hz_19_1", r.Name);
        Assert.Equal("h1_0437.60_O3aC01Cl1In0__O3MDFV2g_G34731_437.70Hz_19", r.WorkunitName);
        Assert.Equal("https://einsteinathome.org/", r.ProjectUrl);
        Assert.Equal(ResultState.FilesDownloaded, r.State);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1752800000), r.ReportDeadline);
        Assert.False(r.ReadyToReport);
        Assert.False(r.SuspendedViaGui);
        Assert.Equal(19000.0, r.EstimatedCpuTimeRemaining);
        Assert.Equal(0.0, r.FinalElapsedTime);
        Assert.Equal(218, r.VersionNum);
        Assert.Equal("", r.PlanClass);
        Assert.NotNull(r.ActiveTask);
        Assert.Equal(0.421, r.ActiveTask!.FractionDone, precision: 6);
        Assert.Equal(3800.0, r.ActiveTask.ElapsedTime);
    }

    [Fact]
    public void Parses_finished_task_without_active_task()
    {
        Result r = Load()[1];
        Assert.Equal(ResultState.FilesUploaded, r.State);
        Assert.True(r.ReadyToReport);
        Assert.True(r.SuspendedViaGui);
        Assert.Null(r.ActiveTask);
        Assert.Equal(12000.5, r.FinalCpuTime);
        Assert.Equal(0.0, r.EstimatedCpuTimeRemaining);
        Assert.Equal(0.0, r.FinalElapsedTime);
        Assert.Equal(0, r.VersionNum);
        Assert.Equal("", r.PlanClass);
    }
```

- [ ] **Step 2: Run tests to verify they fail to compile**

```bash
cd "D:/0x8A63F77D/Documents/GitHub/Lattice"
dotnet test tests/Lattice.Tests -c Release --filter "FullyQualifiedName~ResultTests" 2>&1 | tail -5
```

Expected: compile error CS1061 — `Result` has no `EstimatedCpuTimeRemaining`.

- [ ] **Step 3: Extend the `Result` record**

Replace the `Result` record in `src/Lattice.Boinc.GuiRpc/Models/Result.cs` (leave `ActiveTask` untouched):

```csharp
/// <summary>A task instance ("result" in BOINC vocabulary), from get_results or get_state.</summary>
public sealed record Result(
    string Name,
    string WorkunitName,
    string ProjectUrl,
    ResultState State,
    DateTimeOffset? ReportDeadline,
    bool ReadyToReport,
    bool SuspendedViaGui,
    double FinalCpuTime,
    double FinalElapsedTime,
    double EstimatedCpuTimeRemaining,
    int VersionNum,
    string PlanClass,
    int ExitStatus,
    ActiveTask? ActiveTask)
{
    internal static Result Parse(XElement e) => new(
        ParseHelpers.GetString(e, "name"),
        ParseHelpers.GetString(e, "wu_name"),
        ParseHelpers.GetString(e, "project_url"),
        (ResultState)ParseHelpers.GetInt(e, "state"),
        ParseHelpers.GetTimestamp(e, "report_deadline"),
        ParseHelpers.GetBool(e, "ready_to_report"),
        ParseHelpers.GetBool(e, "suspended_via_gui"),
        ParseHelpers.GetDouble(e, "final_cpu_time"),
        ParseHelpers.GetDouble(e, "final_elapsed_time"),
        ParseHelpers.GetDouble(e, "estimated_cpu_time_remaining"),
        ParseHelpers.GetInt(e, "version_num"),
        ParseHelpers.GetString(e, "plan_class"),
        ParseHelpers.GetInt(e, "exit_status"),
        e.Element("active_task") is { } at ? ActiveTask.Parse(at) : null);
}
```

Note the new fields are inserted before `ExitStatus`/`ActiveTask` grouped by meaning (times together, version info together). Positional order changes are fine — the record has no external consumers yet.

- [ ] **Step 4: Run the full test suite**

```bash
dotnet test tests/Lattice.Tests -c Release 2>&1 | tail -3
```

Expected: `已通过!` with 0 failures (57 + no regressions).

- [ ] **Step 5: Commit**

```bash
git add src/Lattice.Boinc.GuiRpc/Models/Result.cs tests/Lattice.Tests/ResultTests.cs
git commit -m "feat: add remaining/elapsed/version fields to Result"
```

### Task 2: `Project.ResourceShare`

**Files:**
- Modify: `src/Lattice.Boinc.GuiRpc/Models/Project.cs`
- Modify: `tests/Lattice.Tests/fixtures/get_state.xml`
- Test: `tests/Lattice.Tests/CcStateTests.cs`

**Interfaces:**
- Consumes: existing `Project` record
- Produces: `Project.ResourceShare` (double) — M2b Projects aggregation relies on this name

- [ ] **Step 1: Add `resource_share` to the fixture**

In `tests/Lattice.Tests/fixtures/get_state.xml`, add inside the first `<project>` block (after `<project_name>Einstein@Home</project_name>`):

```xml
    <resource_share>100.000000</resource_share>
```

and inside the second `<project>` block (after `<project_name>World Community Grid</project_name>`):

```xml
    <resource_share>50.000000</resource_share>
```

- [ ] **Step 2: Add a failing test**

In `tests/Lattice.Tests/CcStateTests.cs`, add (the class already has a `private static CcState Load()` helper that parses `fixtures/get_state.xml`):

```csharp
    [Fact]
    public void Parses_project_resource_share()
    {
        CcState state = Load();
        Assert.Equal(100.0, state.Projects[0].ResourceShare);
        Assert.Equal(50.0, state.Projects[1].ResourceShare);
    }
```

- [ ] **Step 3: Verify compile failure**

```bash
dotnet test tests/Lattice.Tests -c Release --filter "FullyQualifiedName~CcStateTests" 2>&1 | tail -5
```

Expected: CS1061 — `Project` has no `ResourceShare`.

- [ ] **Step 4: Extend the `Project` record**

In `src/Lattice.Boinc.GuiRpc/Models/Project.cs`, add `double ResourceShare` after `HostExpavgCredit` and the matching parse line:

```csharp
/// <summary>An attached project, from get_state. MasterUrl is the stable identity key.</summary>
public sealed record Project(
    string MasterUrl,
    string ProjectName,
    double UserTotalCredit,
    double UserExpavgCredit,
    double HostTotalCredit,
    double HostExpavgCredit,
    double ResourceShare,
    bool SuspendedViaGui,
    bool DontRequestMoreWork)
{
    internal static Project Parse(XElement e) => new(
        ParseHelpers.GetString(e, "master_url"),
        ParseHelpers.GetString(e, "project_name"),
        ParseHelpers.GetDouble(e, "user_total_credit"),
        ParseHelpers.GetDouble(e, "user_expavg_credit"),
        ParseHelpers.GetDouble(e, "host_total_credit"),
        ParseHelpers.GetDouble(e, "host_expavg_credit"),
        ParseHelpers.GetDouble(e, "resource_share"),
        ParseHelpers.GetBool(e, "suspended_via_gui"),
        ParseHelpers.GetBool(e, "dont_request_more_work"));
}
```

- [ ] **Step 5: Run the full suite, expect green, commit**

```bash
dotnet test tests/Lattice.Tests -c Release 2>&1 | tail -3
git add src/Lattice.Boinc.GuiRpc/Models/Project.cs tests/Lattice.Tests/fixtures/get_state.xml tests/Lattice.Tests/CcStateTests.cs
git commit -m "feat: add ResourceShare to Project"
```

### Task 3: `FileTransfer` model + fixture

**Files:**
- Create: `src/Lattice.Boinc.GuiRpc/Models/FileTransfer.cs`
- Create: `tests/Lattice.Tests/fixtures/get_file_transfers.xml`
- Create: `tests/Lattice.Tests/FileTransferTests.cs`

**Interfaces:**
- Consumes: `ParseHelpers` conventions (but uses its own descendant-based lookups — see Step 3)
- Produces: `FileTransfer` record with exactly these members (Task 4's RPC and Task 6's smoke test use them): `Name`, `ProjectUrl`, `ProjectName` (string); `Nbytes`, `TimeSoFar`, `BytesXferred`, `FileOffset`, `XferSpeed`, `ProjectBackoff` (double); `Status`, `NumRetries` (int); `IsUpload`, `PersXferActive`, `XferActive` (bool); `FirstRequestTime`, `NextRequestTime` (DateTimeOffset?); `internal static FileTransfer Parse(XElement e)`

- [ ] **Step 1: Write the fixture**

Create `tests/Lattice.Tests/fixtures/get_file_transfers.xml`. Three entries: an active upload (both `persistent_file_xfer` and `file_xfer` blocks), a retrying download (`persistent_file_xfer` only, `next_request_time` far in the future), and a minimal queued entry exercising the old-style `generated_locally` direction tag:

```xml
<boinc_gui_rpc_reply>
<file_transfers>
<file_transfer>
    <project_url>https://einsteinathome.org/</project_url>
    <project_name>Einstein@Home</project_name>
    <name>h1_0437.60_result_upload_0</name>
    <nbytes>54198000.000000</nbytes>
    <status>1</status>
    <persistent_file_xfer>
        <num_retries>0</num_retries>
        <first_request_time>1751600000.000000</first_request_time>
        <next_request_time>0.000000</next_request_time>
        <time_so_far>12.500000</time_so_far>
        <last_bytes_xferred>10000000.000000</last_bytes_xferred>
        <is_upload>1</is_upload>
    </persistent_file_xfer>
    <file_xfer>
        <bytes_xferred>35862528.000000</bytes_xferred>
        <file_offset>1048576.000000</file_offset>
        <xfer_speed>2867200.000000</xfer_speed>
        <url>https://einsteinathome.org/upload_handler</url>
    </file_xfer>
</file_transfer>
<file_transfer>
    <project_url>https://www.worldcommunitygrid.org/</project_url>
    <project_name>World Community Grid</project_name>
    <name>wcg_input_42.dat</name>
    <nbytes>2097152.000000</nbytes>
    <status>0</status>
    <persistent_file_xfer>
        <num_retries>3</num_retries>
        <first_request_time>1751500000.000000</first_request_time>
        <next_request_time>1999999999.000000</next_request_time>
        <time_so_far>85.000000</time_so_far>
        <last_bytes_xferred>524288.000000</last_bytes_xferred>
    </persistent_file_xfer>
    <project_backoff>161.000000</project_backoff>
</file_transfer>
<file_transfer>
    <project_url>https://einsteinathome.org/</project_url>
    <project_name>Einstein@Home</project_name>
    <name>queued_upload.out</name>
    <nbytes>1024.000000</nbytes>
    <status>0</status>
    <generated_locally/>
</file_transfer>
</file_transfers>
</boinc_gui_rpc_reply>
```

Field placement mirrors real daemon output: retry fields nested in `<persistent_file_xfer>`, live-transfer fields in `<file_xfer>` (this is exactly what the flat parsing must tolerate). The second entry has no `is_upload` at all → `IsUpload` false (downloads emit nothing). The third has only the legacy `<generated_locally/>` presence tag → `IsUpload` true via fallback.

- [ ] **Step 2: Write the failing tests**

Create `tests/Lattice.Tests/FileTransferTests.cs`:

```csharp
using System.Xml.Linq;
using Xunit;
using Lattice.Boinc.GuiRpc;

namespace Lattice.Tests;

public class FileTransferTests
{
    private static List<FileTransfer> Load()
    {
        var reply = XElement.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "get_file_transfers.xml")));
        return reply.Element("file_transfers")!.Elements("file_transfer").Select(FileTransfer.Parse).ToList();
    }

    [Fact]
    public void Parses_active_upload_with_nested_blocks()
    {
        FileTransfer t = Load()[0];
        Assert.Equal("h1_0437.60_result_upload_0", t.Name);
        Assert.Equal("https://einsteinathome.org/", t.ProjectUrl);
        Assert.Equal("Einstein@Home", t.ProjectName);
        Assert.Equal(54198000.0, t.Nbytes);
        Assert.Equal(1, t.Status);
        Assert.True(t.IsUpload);
        Assert.Equal(0, t.NumRetries);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1751600000), t.FirstRequestTime);
        Assert.Null(t.NextRequestTime); // 0.0 => null, matches ParseHelpers.GetTimestamp semantics
        Assert.Equal(12.5, t.TimeSoFar);
        Assert.Equal(35862528.0, t.BytesXferred); // live bytes_xferred wins over last_bytes_xferred
        Assert.Equal(1048576.0, t.FileOffset);
        Assert.Equal(2867200.0, t.XferSpeed);
        Assert.True(t.PersXferActive);
        Assert.True(t.XferActive);
    }

    [Fact]
    public void Parses_retrying_download()
    {
        FileTransfer t = Load()[1];
        Assert.False(t.IsUpload);
        Assert.Equal(3, t.NumRetries);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1999999999), t.NextRequestTime);
        Assert.Equal(524288.0, t.BytesXferred); // falls back to last_bytes_xferred
        Assert.Equal(161.0, t.ProjectBackoff);
        Assert.True(t.PersXferActive);
        Assert.False(t.XferActive);
    }

    [Fact]
    public void Parses_queued_entry_with_legacy_direction_tag()
    {
        FileTransfer t = Load()[2];
        Assert.True(t.IsUpload); // <generated_locally/> presence fallback
        Assert.Equal(0, t.NumRetries);
        Assert.Equal(0.0, t.BytesXferred);
        Assert.False(t.PersXferActive);
        Assert.False(t.XferActive);
    }
}
```

- [ ] **Step 3: Verify compile failure, then implement the model**

```bash
dotnet test tests/Lattice.Tests -c Release --filter "FullyQualifiedName~FileTransferTests" 2>&1 | tail -5
```

Expected: CS0246 — `FileTransfer` not found. Then create `src/Lattice.Boinc.GuiRpc/Models/FileTransfer.cs`:

```csharp
using System.Xml.Linq;

namespace Lattice.Boinc.GuiRpc;

/// <summary>
/// An in-progress file upload or download, from get_file_transfers.
/// The daemon nests retry fields inside &lt;persistent_file_xfer&gt; and live-transfer
/// fields inside &lt;file_xfer&gt;; the BOINC reference parser reads all tags flatly
/// (lib/gui_rpc_client_ops.cpp, FILE_TRANSFER::parse), so parsing here accepts every
/// tag at any depth within &lt;file_transfer&gt;.
/// </summary>
public sealed record FileTransfer(
    string Name,
    string ProjectUrl,
    string ProjectName,
    double Nbytes,
    int Status,
    bool IsUpload,
    int NumRetries,
    DateTimeOffset? FirstRequestTime,
    DateTimeOffset? NextRequestTime,
    double TimeSoFar,
    double BytesXferred,
    double FileOffset,
    double XferSpeed,
    double ProjectBackoff,
    bool PersXferActive,
    bool XferActive)
{
    internal static FileTransfer Parse(XElement e)
    {
        // Direction: modern daemons emit is_upload; very old ones only generated_locally.
        XElement? direction = Find(e, "is_upload") ?? Find(e, "generated_locally");
        // Progress: bytes_xferred is the live value (inside file_xfer); last_bytes_xferred
        // is the persisted value between attempts.
        XElement? bytes = Find(e, "bytes_xferred") ?? Find(e, "last_bytes_xferred");
        return new(
            Str(e, "name"),
            Str(e, "project_url"),
            Str(e, "project_name"),
            Dbl(e, "nbytes"),
            (int)Dbl(e, "status"),
            direction is not null && BoolValue(direction),
            (int)Dbl(e, "num_retries"),
            Timestamp(e, "first_request_time"),
            Timestamp(e, "next_request_time"),
            Dbl(e, "time_so_far"),
            bytes is not null ? DblValue(bytes) : 0.0,
            Dbl(e, "file_offset"),
            Dbl(e, "xfer_speed"),
            Dbl(e, "project_backoff"),
            e.Element("persistent_file_xfer") is not null,
            e.Element("file_xfer") is not null);
    }

    private static XElement? Find(XElement e, string name) => e.Descendants(name).FirstOrDefault();

    private static string Str(XElement e, string name) => (string?)Find(e, name) ?? string.Empty;

    private static double Dbl(XElement e, string name) =>
        Find(e, name) is { } el ? DblValue(el) : 0.0;

    private static double DblValue(XElement el) =>
        double.TryParse((string)el, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : 0.0;

    private static bool BoolValue(XElement el)
    {
        string v = ((string)el).Trim();
        return v.Length == 0 || v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private static DateTimeOffset? Timestamp(XElement e, string name)
    {
        double seconds = Dbl(e, name);
        return seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds((long)seconds) : null;
    }
}
```

Note `status` is parsed via `Dbl` then cast: the daemon writes it as an integer but leniency costs nothing. `PersXferActive`/`XferActive` use `e.Element(...)` (direct child) deliberately — the block markers are always direct children.

- [ ] **Step 4: Run the full suite, expect green, commit**

```bash
dotnet test tests/Lattice.Tests -c Release 2>&1 | tail -3
git add src/Lattice.Boinc.GuiRpc/Models/FileTransfer.cs tests/Lattice.Tests/fixtures/get_file_transfers.xml tests/Lattice.Tests/FileTransferTests.cs
git commit -m "feat: add FileTransfer model with flat lenient parsing"
```

### Task 4: `GetFileTransfersAsync` RPC

**Files:**
- Modify: `src/Lattice.Boinc.GuiRpc/BoincGuiRpcClient.cs` (add method after `GetMessagesAsync`, before `PerformRpcAsync`)
- Test: `tests/Lattice.Tests/BoincGuiRpcClientRpcTests.cs`

**Interfaces:**
- Consumes: `FileTransfer.Parse` (Task 3), existing `PerformRpcAsync` private helper, `ScriptedStream` test double
- Produces: `Task<IReadOnlyList<FileTransfer>> GetFileTransfersAsync(CancellationToken ct = default)` — Task 5's interface and Task 6's smoke test use this exact signature

- [ ] **Step 1: Add the failing RPC test**

In `tests/Lattice.Tests/BoincGuiRpcClientRpcTests.cs`, add after `GetMessages_sends_seqno`:

```csharp
    [Fact]
    public async Task GetFileTransfers_returns_typed_transfers()
    {
        var stream = ScriptedStream.FromReplies(Fixture("get_file_transfers.xml"));
        await using var client = ClientWith(stream);

        IReadOnlyList<FileTransfer> transfers = await client.GetFileTransfersAsync();

        Assert.Equal(3, transfers.Count);
        Assert.True(transfers[0].XferActive);
        string sent = Encoding.ASCII.GetString(stream.Written.ToArray());
        Assert.Contains("<get_file_transfers/>", sent);
    }
```

- [ ] **Step 2: Verify compile failure**

```bash
dotnet test tests/Lattice.Tests -c Release --filter "FullyQualifiedName~GetFileTransfers" 2>&1 | tail -5
```

Expected: CS1061 — no `GetFileTransfersAsync`.

- [ ] **Step 3: Implement the RPC**

In `src/Lattice.Boinc.GuiRpc/BoincGuiRpcClient.cs`, after `GetMessagesAsync`:

```csharp
    /// <summary>Returns in-progress file uploads and downloads.</summary>
    public async Task<IReadOnlyList<FileTransfer>> GetFileTransfersAsync(CancellationToken ct = default)
    {
        XElement reply = await PerformRpcAsync("<get_file_transfers/>", throwOnUnauthorized: true, ct).ConfigureAwait(false);
        XElement container = reply.Element("file_transfers") ?? reply;
        return [.. container.Elements("file_transfer").Select(FileTransfer.Parse)];
    }
```

Reminder from CLAUDE.md: the self-closing request tag must have NO space before the slash (`<get_file_transfers/>`).

- [ ] **Step 4: Run the full suite, expect green, commit**

```bash
dotnet test tests/Lattice.Tests -c Release 2>&1 | tail -3
git add src/Lattice.Boinc.GuiRpc/BoincGuiRpcClient.cs tests/Lattice.Tests/BoincGuiRpcClientRpcTests.cs
git commit -m "feat: add get_file_transfers RPC"
```

### Task 5: Extract `IGuiRpcClient`

**Files:**
- Create: `src/Lattice.Boinc.GuiRpc/IGuiRpcClient.cs`
- Modify: `src/Lattice.Boinc.GuiRpc/BoincGuiRpcClient.cs:16` (declaration line only)
- Test: `tests/Lattice.Tests/BoincGuiRpcClientRpcTests.cs`

**Interfaces:**
- Consumes: every public RPC method signature from Tasks 1–4 (unchanged)
- Produces: `IGuiRpcClient` — the only type M2b's `HostConnection` will depend on

- [ ] **Step 1: Add the failing test**

In `tests/Lattice.Tests/BoincGuiRpcClientRpcTests.cs`:

```csharp
    [Fact]
    public async Task Client_implements_IGuiRpcClient()
    {
        var stream = ScriptedStream.FromReplies(Fixture("get_cc_status.xml"));
        await using BoincGuiRpcClient client = ClientWith(stream);

        IGuiRpcClient viaInterface = client;
        CcStatus status = await viaInterface.GetCcStatusAsync();

        Assert.Equal(RunMode.Auto, status.TaskMode);
    }
```

- [ ] **Step 2: Verify compile failure**

```bash
dotnet test tests/Lattice.Tests -c Release --filter "FullyQualifiedName~IGuiRpcClient" 2>&1 | tail -5
```

Expected: CS0246 — `IGuiRpcClient` not found.

- [ ] **Step 3: Create the interface and implement it**

Create `src/Lattice.Boinc.GuiRpc/IGuiRpcClient.cs`:

```csharp
namespace Lattice.Boinc.GuiRpc;

/// <summary>
/// The GUI RPC surface of one BOINC core client connection.
/// Extracted so higher layers can substitute a fake for testing;
/// <see cref="BoincGuiRpcClient"/> is the production implementation.
/// </summary>
public interface IGuiRpcClient : IAsyncDisposable
{
    /// <summary>Connects to a BOINC core client.</summary>
    Task ConnectAsync(string host, int port = 31416, CancellationToken ct = default);

    /// <summary>Authenticates with the daemon using a password. Returns true on success.</summary>
    Task<bool> AuthorizeAsync(string password, CancellationToken ct = default);

    /// <summary>Exchanges version information with the daemon.</summary>
    Task<VersionInfo> ExchangeVersionsAsync(CancellationToken ct = default);

    /// <summary>Full state snapshot. Several MB on busy hosts — call once per connection, then poll deltas.</summary>
    Task<CcState> GetStateAsync(CancellationToken ct = default);

    /// <summary>Returns the core client status: task mode, network status, suspend reasons.</summary>
    Task<CcStatus> GetCcStatusAsync(CancellationToken ct = default);

    /// <summary>Returns the list of results (tasks) on the core client.</summary>
    Task<IReadOnlyList<Result>> GetResultsAsync(bool activeOnly = false, CancellationToken ct = default);

    /// <summary>Returns messages with seqno greater than the given value. Seqno is monotonic.</summary>
    Task<IReadOnlyList<Message>> GetMessagesAsync(int seqno = 0, CancellationToken ct = default);

    /// <summary>Returns in-progress file uploads and downloads.</summary>
    Task<IReadOnlyList<FileTransfer>> GetFileTransfersAsync(CancellationToken ct = default);
}
```

In `BoincGuiRpcClient.cs`, change the declaration:

```csharp
public sealed class BoincGuiRpcClient : IGuiRpcClient
```

(`IAsyncDisposable` comes via the interface; no other change.)

- [ ] **Step 4: Run the full suite, expect green, commit**

```bash
dotnet test tests/Lattice.Tests -c Release 2>&1 | tail -3
git add src/Lattice.Boinc.GuiRpc/IGuiRpcClient.cs src/Lattice.Boinc.GuiRpc/BoincGuiRpcClient.cs tests/Lattice.Tests/BoincGuiRpcClientRpcTests.cs
git commit -m "feat: extract IGuiRpcClient interface"
```

### Task 6: Smoke test section, version bump, live verification

**Files:**
- Modify: `tools/Lattice.SmokeTest/Program.cs` (after the messages section)
- Modify: `src/Lattice.Boinc.GuiRpc/Lattice.Boinc.GuiRpc.csproj:8` (`<Version>`)

**Interfaces:**
- Consumes: `GetFileTransfersAsync` (Task 4), `FileTransfer` members (Task 3)

- [ ] **Step 1: Add the transfers section to the smoke test**

In `tools/Lattice.SmokeTest/Program.cs`, after the messages block (find `IReadOnlyList<Message> messages = ...` and its output loop), add:

```csharp
    IReadOnlyList<FileTransfer> transfers = await client.GetFileTransfersAsync();
    Console.WriteLine($"Transfers ({transfers.Count}):");
    foreach (FileTransfer t in transfers)
    {
        string direction = t.IsUpload ? "up" : "down";
        string state = t.XferActive
            ? $"active {t.XferSpeed / 1048576:F2} MB/s"
            : t.NextRequestTime is { } next && next > DateTimeOffset.UtcNow
                ? $"retry at {next:HH:mm:ss} (attempt {t.NumRetries})"
                : "queued";
        Console.WriteLine($"  {t.Name,-50} {direction,-4} {t.BytesXferred / 1048576.0:F1}/{t.Nbytes / 1048576.0:F1} MB  {state}");
    }
```

- [ ] **Step 2: Bump the package version**

In `src/Lattice.Boinc.GuiRpc/Lattice.Boinc.GuiRpc.csproj`, change:

```xml
    <Version>0.2.0</Version>
```

- [ ] **Step 3: Full local build + tests under CI configuration**

```bash
dotnet build Lattice.sln -c Release -warnaserror 2>&1 | tail -3
dotnet test Lattice.sln -c Release --no-build 2>&1 | tail -3
```

Expected: zero warnings, `已通过!` with 0 failures.

- [ ] **Step 4: Run the smoke test against the live local daemon**

```bash
dotnet run --project tools/Lattice.SmokeTest -c Release 2>&1 | tail -15
```

The tool reads the password from `D:\BOINCData\gui_rpc_auth.cfg` automatically — NEVER print or echo the password itself. Expected: existing sections plus `Transfers (N):` — N may legitimately be 0 on an idle host; any exception is a failure.

- [ ] **Step 5: Verify packaging still works, then commit**

```bash
dotnet pack src/Lattice.Boinc.GuiRpc -c Release -o /tmp/lattice-pack 2>&1 | tail -2
git add tools/Lattice.SmokeTest/Program.cs src/Lattice.Boinc.GuiRpc/Lattice.Boinc.GuiRpc.csproj
git commit -m "feat: smoke-test transfers section, bump package to 0.2.0"
```

Expected pack output: `Lattice.Boinc.GuiRpc.0.2.0.nupkg` created.

### Task 7: PR

**Files:** none (GitHub operation)

- [ ] **Step 1: Push and open the PR**

```bash
git push -u origin m2a-guirpc-extensions
gh pr create --base main --head m2a-guirpc-extensions \
  --title "M2a: GuiRpc protocol extensions for the dashboard" \
  --body "Implements docs/superpowers/specs/2026-07-04-m2a-guirpc-extensions-design.md:

- Result: EstimatedCpuTimeRemaining, FinalElapsedTime, VersionNum, PlanClass
- Project: ResourceShare
- New FileTransfer model + get_file_transfers RPC (flat lenient parsing per the BOINC reference parser)
- IGuiRpcClient interface extracted for M2b testability
- Smoke test gains a transfers section; package bumped to 0.2.0

All fields verified against fixtures and lib/gui_rpc_client_ops.cpp. Smoke-tested against a live BOINC 8.2.11 daemon.

🤖 Generated with [Claude Code](https://claude.com/claude-code)"
gh pr checks --watch
```

Expected: `build-test (ubuntu-latest)` green.

- [ ] **Step 2: Wait for Codex Review before merging**

CI green is not sufficient. Wait for Codex's result (`gh api repos/0x8A63F77D/Lattice/issues/<pr>/comments` — results may arrive as an issue comment, not a review). Triage findings; the USER merges (or explicitly authorizes the merge). If Codex needs a re-trigger after new commits, the USER must comment `@codex review again` in the web UI (API-posted comments do not trigger it).
