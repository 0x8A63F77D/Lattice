# M2c-2 Wave 2c — Transfers View

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The Transfers data view — FileTransfer rows with "34.2 / 51.7 MB" progress, per-second retry countdowns, and the (common-case) empty state — stamped from the Tasks pattern onto the shared aggregation core.

**Architecture:** A pure C# row projection (`TransferRowViewModel.From`, mirroring `TaskRowViewModel.From`) over the existing `TransferSnapshot` (`HostSnapshot.Transfers` already ships `TransferUiState` Active/Retrying/Queued from Core). The VM is a straight `TasksViewModel` stamp: `HostFacts` → `ViewSlice.compute` → sort → `Reconcile.diff` + `CollectionReconciler`. The retry countdown re-renders because the VM already rebuilds on every `IUiClock.Tick` (the Tasks freshness mechanism) — `From` takes `now` and derives "Retry in 02:41 (attempt 3)" from `NextRequestTime − now`. No new F# is needed: this view has no aggregation decisions beyond what ViewSlice already answers.

**Tech Stack:** C# / Avalonia 12 DataGrid 12.1.0, CommunityToolkit.Mvvm, existing `Lattice.App.Aggregation`.

**Authoritative sources:** issue #31 (scope), spec `docs/superpowers/specs/2026-07-10-m2c2-data-views-design.md` §Wave 2, design package `docs/design/m2/README.md` §"Transfers view (2b)". The design package wins over this plan when they conflict.


**Sequencing:** Blocked by the w2a2 scaffold-extraction PR (`2026-07-11-m2c2-w2a2-view-scaffold-extraction.md`) — `RowClassBinder` / `ViewSliceProjection` / `PartialBarState` must be on main before this branch starts; this plan consumes them instead of transcribing the Tasks pattern (PR #42 round-1 root cause).

**Standing rules that bind every task:**
- Red-first: run the failing test and see it fail before implementing. Reviewer repeats falsification.
- `-warnaserror` clean, Debug + Release, on every commit.
- Nothing here touches `src/Lattice.Core/HostMonitor.cs` or `HostMachine.fs`; if a task somehow does, the verification sync rule (CLAUDE.md) applies — stop and escalate.
- Avalonia API questions go to the avalonia-docs MCP, never guessed.
- Conflict isolation (parallel Wave-2 worktrees): this PR owns only its View/ViewModel/test files; shared touch points (`ShellViewModel.Views[2]`, one DataTemplate block in `ShellWindow.axaml`, resx keys prefixed `Transfers*`) are additive one-liners; rebase on main before opening the PR.
- Culture: byte counts, speeds and countdowns render with InvariantCulture (the ar-SA calendar pin is a true pin — see `TaskRowViewModel`'s deadline comment).
- Repo language: commits/comments in English.

---

## File structure

```
src/Lattice.App/
├── ViewModels/TransferRow.cs           NEW — TransferRowKey + closed holder + row record
├── ViewModels/TransfersViewModel.cs    NEW — stamped from TasksViewModel.cs
├── Views/TransfersView.axaml           NEW — stamped from TasksView.axaml
├── Views/TransfersView.axaml.cs        NEW — row-class liveness (retrying tint)
├── ViewModels/ShellViewModel.cs        MODIFY — Views[2] swap + scope push (2 lines)
├── Views/ShellWindow.axaml             MODIFY — one DataTemplate block
└── Localization/Strings.resx           MODIFY — Transfers* keys

tests/Lattice.App.Tests/
├── TransferRowViewModelTests.cs        NEW
├── TransfersViewModelTests.cs          NEW
├── Headless/TransfersViewTests.cs      NEW
└── Headless/Journeys/TransfersJourney.cs  NEW
```

---

### Task 1: `TransferRow` holder + row record

**Files:**
- Create: `src/Lattice.App/ViewModels/TransferRow.cs`
- Create: `tests/Lattice.App.Tests/TransferRowViewModelTests.cs`

- [ ] **Step 1: Write failing tests**

`tests/Lattice.App.Tests/TransferRowViewModelTests.cs`:

```csharp
using Lattice.App.ViewModels;
using Lattice.Boinc.GuiRpc;
using Lattice.Core;

namespace Lattice.Tests;

public class TransferRowViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);

    private static TransferSnapshot Snap(
        string name = "wu_1_0", string project = "P1", bool upload = true,
        double nbytes = 51.7 * 1024 * 1024, double xferred = 34.2 * 1024 * 1024,
        double speed = 0, int retries = 0, DateTimeOffset? nextRequest = null,
        TransferUiState state = TransferUiState.Queued)
    {
        var t = new FileTransfer(name, "http://p1/", project, nbytes, 0, upload, retries,
            null, nextRequest, 0, xferred, 0, speed, 0,
            PersXferActive: state == TransferUiState.Retrying, XferActive: state == TransferUiState.Active);
        return new TransferSnapshot(t, project, state);
    }

    [Fact]
    public void Progress_renders_transferred_over_total_megabytes()
    {
        var row = TransferRowViewModel.From(Snap(), Guid.NewGuid(), "host-a", Now);
        Assert.Equal("34.2 / 51.7 MB", row.ProgressText);
        Assert.True(row.Fraction is > 0.66 and < 0.67);
    }

    [Fact]
    public void Active_shows_live_speed_and_upload_direction()
    {
        var row = TransferRowViewModel.From(
            Snap(speed: 1.5 * 1024 * 1024, state: TransferUiState.Active), Guid.NewGuid(), "host-a", Now);
        Assert.Equal(TransferUiState.Active, row.UiState);
        Assert.Equal("1.5 MB/s", row.SpeedText);
        Assert.Equal("Upload", row.DirectionText);
    }

    [Fact]
    public void Retrying_counts_down_from_next_request_time()
    {
        var row = TransferRowViewModel.From(
            Snap(retries: 3, nextRequest: Now.AddSeconds(161), state: TransferUiState.Retrying),
            Guid.NewGuid(), "host-a", Now);
        Assert.Equal("Retry in 02:41 (attempt 3)", row.StatusText);
        Assert.Equal("—", row.SpeedText);
    }

    [Fact]
    public void Retry_moment_passed_clamps_to_zero()
    {
        var row = TransferRowViewModel.From(
            Snap(retries: 1, nextRequest: Now.AddSeconds(-5), state: TransferUiState.Retrying),
            Guid.NewGuid(), "host-a", Now);
        Assert.Equal("Retry in 00:00 (attempt 1)", row.StatusText);
    }

    [Fact]
    public void Queued_renders_dash_speed_and_queued_status()
    {
        var row = TransferRowViewModel.From(Snap(), Guid.NewGuid(), "host-a", Now);
        Assert.Equal("—", row.SpeedText);
        Assert.Equal("Queued", row.StatusText);
    }

    [Fact]
    public void Key_is_host_project_name_direction()
    {
        var hostId = Guid.NewGuid();
        var row = TransferRowViewModel.From(Snap(), hostId, "host-a", Now);
        Assert.Equal(new TransferRowKey(hostId, "http://p1/", "wu_1_0", IsUpload: true), row.Key);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Lattice.App.Tests --filter TransferRowViewModel -v minimal`
Expected: FAIL — types not defined.

- [ ] **Step 3: Implement**

`src/Lattice.App/ViewModels/TransferRow.cs`:

```csharp
using System.Globalization;
using Lattice.App.Infrastructure;
using Lattice.App.Localization;
using Lattice.Core;

namespace Lattice.App.ViewModels;

/// <summary>
/// Row identity in the Transfers grid: the daemon keys a transfer by
/// (project, file name, direction); the same file name can exist on
/// multiple hosts and as both upload and download.
/// </summary>
public readonly record struct TransferRowKey(Guid HostId, string ProjectUrl, string Name, bool IsUpload);

/// <summary>Closed holder so XAML can use x:DataType.</summary>
public sealed class TransferRow(TransferRowKey key, TransferRowViewModel data)
    : RowHolder<TransferRowKey, TransferRowViewModel>(key, data);

/// <summary>
/// Immutable row projection for a file transfer. Pure over (snapshot, host,
/// now); "now" drives the retry countdown, re-rendered by the VM's per-tick
/// Rebuild — the same mechanism that keeps the Tasks freshness text moving.
/// </summary>
public sealed record TransferRowViewModel(
    TransferRowKey Key,
    string Name,
    string Project,
    string DirectionText,
    string ProgressText,
    double Fraction,
    string SpeedText,
    TransferUiState UiState,
    string StatusText,
    Guid HostId,
    string Host)
{
    public static TransferRowViewModel From(TransferSnapshot snap, Guid hostId, string host, DateTimeOffset now)
    {
        var t = snap.Transfer;
        var fraction = t.Nbytes > 0 ? Math.Clamp(t.BytesXferred / t.Nbytes, 0, 1) : 0;

        var statusText = snap.UiState switch
        {
            TransferUiState.Active => Strings.TransfersStatusActive,
            TransferUiState.Retrying => RetryText(t.NextRequestTime, t.NumRetries, now),
            TransferUiState.Queued => Strings.TransfersStatusQueued,
            _ => throw new InvalidOperationException("unreachable: closed enum"),
        };

        return new(
            Key: new TransferRowKey(hostId, t.ProjectUrl, t.Name, t.IsUpload),
            Name: t.Name,
            Project: snap.ProjectName,
            DirectionText: t.IsUpload ? Strings.TransfersUpload : Strings.TransfersDownload,
            ProgressText: string.Format(Strings.TransfersProgressFmt, Mb(t.BytesXferred), Mb(t.Nbytes)),
            Fraction: fraction,
            SpeedText: snap.UiState == TransferUiState.Active && t.XferSpeed > 0
                ? string.Format(Strings.TransfersSpeedFmt, Mb(t.XferSpeed))
                : "—",
            UiState: snap.UiState,
            StatusText: statusText,
            HostId: hostId,
            Host: host);
    }

    private static string RetryText(DateTimeOffset? nextRequest, int attempt, DateTimeOffset now)
    {
        var remaining = nextRequest is { } at && at > now ? at - now : TimeSpan.Zero;
        // mm:ss with minutes allowed past 59 (backoffs can exceed an hour).
        var mmss = string.Create(CultureInfo.InvariantCulture,
            $"{(int)remaining.TotalMinutes:00}:{remaining.Seconds:00}");
        return string.Format(Strings.TransfersRetryFmt, mmss, attempt);
    }

    private static string Mb(double bytes) =>
        (bytes / (1024.0 * 1024.0)).ToString("0.#", CultureInfo.InvariantCulture);
}
```

resx keys this task needs (add now; `Transfers` prefix, T1 group banner):

| Key | Value |
|---|---|
| `TransfersStatusActive` | `Transferring` |
| `TransfersStatusQueued` | `Queued` |
| `TransfersRetryFmt` | `Retry in {0} (attempt {1})` |
| `TransfersProgressFmt` | `{0} / {1} MB` |
| `TransfersSpeedFmt` | `{0} MB/s` |
| `TransfersUpload` | `Upload` |
| `TransfersDownload` | `Download` |

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/Lattice.App.Tests --filter TransferRowViewModel -v minimal`
Expected: PASS (6).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(app): TransferRow key + row record with countdown rendering"
```

---

### Task 2: `TransfersViewModel`

**Files:**
- Create: `src/Lattice.App/ViewModels/TransfersViewModel.cs`
- Create: `tests/Lattice.App.Tests/TransfersViewModelTests.cs`

**Stamp source:** `src/Lattice.App/ViewModels/TasksViewModel.cs`, with:
- rows via the shared projection (w2a2): `var slice = ViewSliceProjection.Compute(_store.Hosts, Scope, h => h.Snapshot!.Transfers.Select(t => TransferRowViewModel.From(t, h.Config.Id, h.Config.DisplayName, _clock.Now)).ToArray());` — the `inScope && isRowSource` gate lives in the helper, not here;
- no text filter, no state filter, no column-visibility persistence (design 2b has none); density toggle kept;
- sort: `ProjectName` then `Name` ascending (design 2b names no default sort; this is the recorded decision — stable and scan-friendly);
- counts: `TransfersCountsFmt` = `{0} transfers · {1} up · {2} down` over the unfiltered row set;
- partial bar via the shared `PartialBarState` (w2a2): one `_partialBar` field, `ShowPartialBar = _partialBar.Advance(slice.UnreachableIds, slice.CoveredIds, Scope.IsAllHosts);` in Rebuild, `_partialBar.Dismiss()` in DismissPartial — the retrofitted `TasksViewModel` on main is the reference;
- everything else (Scope, TasksOverlayPolicy overlay block, UpdatedText/IsUpdateStale, PollingText, Refresh command, Dispose) is a verbatim stamp of the retrofitted TasksViewModel.

- [ ] **Step 1: Write failing tests** — mirror `TasksViewModelTests.cs` fixture idiom. Pin, each as a real test:

1. Rows merge across in-scope hosts only; scope change re-filters.
2. Steady-state poll with identical transfer data → zero CollectionChanged events; holder identity survives a progress change (`Assert.Same` + Data.ProgressText updated).
3. Countdown ticks: a Retrying row's StatusText moves from "Retry in 00:10 (attempt 2)" to "Retry in 00:09 (attempt 2)" after `_clock` advances 1 s (ManualUiClock.Advance + Tick — the fixture's clock idiom).
4. Completed transfer (absent from next poll) is removed without Reset.
5. IsEmpty is true with connected hosts and zero transfers (THE common case, design 2b).

- [ ] **Step 2: Run to verify failure** — compile error red.

- [ ] **Step 3: Implement** per the stamp description above (the full-body shape is `TasksViewModel.cs` minus filters; `ProjectsViewModel` in the sibling plan shows the same reduction pattern if in doubt — but do not wait on that PR; stamp from Tasks directly).

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/Lattice.App.Tests --filter TransfersViewModel -v minimal`
Expected: PASS (5).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(app): TransfersViewModel over shared aggregation core with per-tick countdown"
```

---

### Task 3: `TransfersView` (XAML + retrying row tint)

**Files:**
- Create: `src/Lattice.App/Views/TransfersView.axaml`
- Create: `src/Lattice.App/Views/TransfersView.axaml.cs`
- Create: `tests/Lattice.App.Tests/Headless/TransfersViewTests.cs`

**Stamp source:** `src/Lattice.App/Views/TasksView.axaml` (+ `.axaml.cs`). Keep: command bar layout (title + disabled `Strings.TransfersRetryNow` M3 placeholder + updated/refresh/density from TasksView.axaml:69-110), F5 KeyBinding, FAInfoBar partial bar + `OnPartialBarClosed`, StatusBarControl, loading/empty overlays, row-class liveness via the shared `RowClassBinder` (w2a2): attach once in the constructor, forward `internal int RowSubscriptionCount => _rowBinder.Count;`, ship the stamped teardown-drain test (render → detach → assert count 0) to pin the attachment. Grid: `CanUserSortColumns="True"` with `Data.*` bindings (nested sort paths natively supported — DataGrid 12.1.0, established in PR #37).

- [ ] **Step 1: Write the failing headless tests** (follow `Headless/TasksViewTests.cs` idiom):

1. Retrying row carries class `retrying` and its Background resolves to the warning tint (`LatticeWarningTintBrush`) — stamp the atRisk assertion pattern.
2. In-place UiState change (holder.Data mutation) flips the class without LoadingRow re-firing (the Task-7 regression stamped here).
3. Empty state renders the design copy: icon + "No active transfers" + caption, no button.

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: Implement the XAML.** Columns (design 2b: File * · Project 140 · Direction 80 · Progress 190 · Speed 90 · Status 210 · Host 80, all left-aligned):

```xml
<DataGrid.Columns>
  <DataGridTemplateColumn Header="{x:Static loc:Strings.TransfersColFile}" Width="*" MinWidth="140">
    <DataGridTemplateColumn.CellTemplate>
      <DataTemplate x:DataType="vm:TransferRow">
        <TextBlock Text="{Binding Data.Name}" VerticalAlignment="Center"
                   TextTrimming="CharacterEllipsis" ToolTip.Tip="{Binding Data.Name}" />
      </DataTemplate>
    </DataGridTemplateColumn.CellTemplate>
  </DataGridTemplateColumn>
  <DataGridTextColumn Header="{x:Static loc:Strings.ColProject}"
                       Binding="{Binding Data.Project}" Width="140" />
  <DataGridTextColumn Header="{x:Static loc:Strings.TransfersColDirection}"
                       Binding="{Binding Data.DirectionText}" Width="80" />
  <!-- Progress 190: bar + "34.2 / 51.7 MB" (stamp the Tasks progress cell,
       width 60 bar + text; FractionToWidthConverter reused) -->
  <DataGridTemplateColumn Header="{x:Static loc:Strings.ColProgress}" Width="190">
    <DataGridTemplateColumn.CellTemplate>
      <DataTemplate x:DataType="vm:TransferRow">
        <StackPanel Orientation="Horizontal" Spacing="8" VerticalAlignment="Center">
          <Border Width="56" Height="3" CornerRadius="2" Background="{DynamicResource LatticeStrokeBrush}">
            <Border Classes="progressFill" HorizontalAlignment="Left" Height="3" CornerRadius="2"
                    Width="{Binding Data.Fraction, Converter={x:Static views:FractionToWidthConverter.Instance}}" />
          </Border>
          <TextBlock Text="{Binding Data.ProgressText}" FontSize="12" Classes="numeric" />
        </StackPanel>
      </DataTemplate>
    </DataGridTemplateColumn.CellTemplate>
  </DataGridTemplateColumn>
  <DataGridTextColumn Header="{x:Static loc:Strings.TransfersColSpeed}"
                       Binding="{Binding Data.SpeedText}" Width="90" />
  <!-- Status 210: state icon + text; Retrying text semibold warning -->
  <DataGridTemplateColumn Header="{x:Static loc:Strings.ColState}" Width="210">
    <DataGridTemplateColumn.CellTemplate>
      <DataTemplate x:DataType="vm:TransferRow">
        <StackPanel Orientation="Horizontal" Spacing="4" VerticalAlignment="Center">
          <Panel Width="12" Height="12">
            <PathIcon Width="12" Height="12" Data="{StaticResource IconArrowSyncRegular}"
                      Foreground="{DynamicResource LatticeSuccessBrush}"
                      IsVisible="{Binding Data.UiState, Converter={x:Static views:EnumMatchConverter.Instance}, ConverterParameter=Active}" />
            <PathIcon Width="12" Height="12" Data="{StaticResource IconArrowClockwiseRegular}"
                      Foreground="{DynamicResource LatticeWarningFgBrush}"
                      IsVisible="{Binding Data.UiState, Converter={x:Static views:EnumMatchConverter.Instance}, ConverterParameter=Retrying}" />
            <PathIcon Width="12" Height="12" Data="{StaticResource IconClockRegular}"
                      Foreground="{DynamicResource LatticeNeutralFgBrush}"
                      IsVisible="{Binding Data.UiState, Converter={x:Static views:EnumMatchConverter.Instance}, ConverterParameter=Queued}" />
          </Panel>
          <TextBlock Text="{Binding Data.StatusText}" FontSize="12"
                     Classes.retryingText="{Binding Data.IsRetrying}" />
        </StackPanel>
      </DataTemplate>
    </DataGridTemplateColumn.CellTemplate>
  </DataGridTemplateColumn>
  <DataGridTextColumn Header="{x:Static loc:Strings.ColHost}"
                       Binding="{Binding Data.Host}" Width="80"
                       IsVisible="{Binding IsAllHostsScope}" />
</DataGrid.Columns>
```

Styles block:

```xml
<Style Selector="DataGridRow.retrying">
  <Setter Property="Background" Value="{DynamicResource LatticeWarningTintBrush}" />
</Style>
<Style Selector="TextBlock.retryingText">
  <Setter Property="FontWeight" Value="SemiBold" />
  <Setter Property="Foreground" Value="{DynamicResource LatticeWarningFgBrush}" />
</Style>
```

Empty overlay (design 2b, the common case — icon + title + caption, no button):

```xml
<Border IsVisible="{Binding IsEmpty}" Background="{DynamicResource LatticeSurfaceBrush}">
  <StackPanel HorizontalAlignment="Center" VerticalAlignment="Center" Spacing="8">
    <PathIcon Width="24" Height="24" Data="{StaticResource IconArrowSwapRegular}"
              Foreground="{DynamicResource LatticeTextDisabledBrush}" HorizontalAlignment="Center" />
    <TextBlock Text="{x:Static loc:Strings.TransfersEmpty}" FontSize="13" FontWeight="SemiBold"
               HorizontalAlignment="Center" />
    <TextBlock Text="{x:Static loc:Strings.TransfersEmptyCaption}" FontSize="12"
               Foreground="{DynamicResource LatticeTextSecondaryBrush}" HorizontalAlignment="Center" />
  </StackPanel>
</Border>
```

**Bounded lookups for the implementer:** `Data.IsRetrying` needs a `bool IsRetrying` convenience on the record (`UiState == TransferUiState.Retrying`) — add it in Task 1 or here, either way with its unit assertion. `IconArrowSyncRegular` / `LatticeTextDisabledBrush`: check the icon/token resources next to the existing entries (`IconArrowSwapRegular` exists — it's the Transfers nav icon); add missing ones following the sourcing convention.

Code-behind binder attachment (constructor, after InitializeComponent):

```csharp
_rowBinder = RowClassBinder<TransferRow>.Attach(Grid, static (row, holder) =>
    row.Classes.Set("retrying", holder.Data.UiState == TransferUiState.Retrying));
```

resx additions: `TransfersTitle` = `Transfers`, `TransfersRetryNow` = `Retry now`, `TransfersColFile` = `File`, `TransfersColDirection` = `Direction`, `TransfersColSpeed` = `Speed`, `TransfersCountsFmt` = `{0} transfers · {1} up · {2} down`, `TransfersEmpty` = `No active transfers`, `TransfersEmptyCaption` = `Uploads and downloads will appear here while in progress.`

- [ ] **Step 4: Run headless tests** — Expected: PASS (4, incl. the teardown drain).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(app): TransfersView with retrying tint + common-case empty state"
```

---

### Task 4: Shell wiring + journey + wrap-up

**Files:**
- Modify: `src/Lattice.App/ViewModels/ShellViewModel.cs`, `src/Lattice.App/Views/ShellWindow.axaml`
- Create: `tests/Lattice.App.Tests/Headless/Journeys/TransfersJourney.cs`

- [ ] **Step 1: Shell wiring (red-first via a ShellViewModelTests scope-push test):** ctor `Transfers = new TransfersViewModel(store, clock, uiState);`, `Views[2]` placeholder → `Transfers`, property, `OnScopeChanged` push, `Dispose`. ShellWindow DataTemplate: `<DataTemplate DataType="vm:TransfersViewModel"><v:TransfersView /></DataTemplate>`.

- [ ] **Step 2: Journey** (`TransfersJourney.cs`, harness idiom from `TasksScopeJourney.cs`): host with one retrying transfer → navigate to Transfers (SelectView "2") → settle on the countdown text → advance the manual clock 1 s → settle on the decremented text → fake reports no transfers → settle on the empty-state title text.

- [ ] **Step 3: Full suite** Debug+Release, `-warnaserror`. Expected green incl. LocalizationTests.

- [ ] **Step 4: Commit, rebase on main, push branch `m2c2-w2c-transfers-view`, open PR** (body: issue #31 Transfers leg; note the recorded sort-order decision and zero HostMonitor changes). Trigger `@codex review`, pr-monitor, read raw threads yourself (≥60 s re-poll), adjudicate red-first, merge on clean.

```bash
git commit -m "feat(app): Transfers view wired into shell + journey"
```

---

## Explicitly out of scope

- Completed-row fade-out animation (150 ms accelerate): design 2b names it, but ALL motion is Wave 3 (#32) per the spec's wave split — rows are removed immediately here; #32 adds the fade. State this in the PR body so Codex doesn't flag it.
- "Retry now" functionality (M3; the button ships disabled).
- Projects / Event-log views (parallel plans).
- Per-view column breakpoints: design §Responsive (2f) names only Tasks columns; the Transfers column set (790px + file star) fits the 1000px minimum window, so no width-driven column hiding ships here (design-authoritative adjudication — cite 2f in the PR body).

## Self-review notes (already applied)

- Design 2b coverage: column set/widths ↔ Task 3; progress text format ↔ Task 1 test 1; retrying treatment (tint + semibold + per-second countdown + attempt) ↔ Task 1 tests 3-4 + Task 2 test 3 + Task 3 test 1; queued dash-speed ↔ Task 1 test 5; empty-state copy ↔ Task 3 test 3; fade-out explicitly deferred with rationale.
- Known judgment calls the executor must not "fix": sort order (Project, Name); countdown clamps at 00:00 rather than flipping text (the state machine flips the row to Active/Queued on the next poll); MB-only units (design shows MB; KB/GB variants are YAGNI until real data demands them — note for the #32 walkthrough).
