# M2c-2 Wave 1 — Scaffold + Tasks View Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver the shared view scaffold (ResX i18n seam, command-bar pattern, DataGrid infrastructure, status-bar control, global scope + "All hosts" rail entry) and the complete Tasks view, with journey-level test infrastructure — one PR to main.

**Architecture:** Everything rides the M2c-1 graph: `HostStore` (UI-thread cache of config/status/snapshot per host) is the single data source; ViewModels project it; views are thin XAML. Core already delivers view-ready `TaskSnapshot` rows (join + deadline-at-risk computed in `SnapshotBuilder`) — the Tasks pipeline is pure projection, no new Core semantics (HostMonitor untouched → verification sync rule not triggered; the only Core-adjacent change is an App-layer passthrough to the existing `HostMonitor.RequestRefresh()`).

**Tech Stack:** Avalonia 12.1 + FluentAvaloniaUI 3.0.1 (FA-prefixed controls), `Avalonia.Controls.DataGrid`, `VocaDb.ResXFileCodeGenerator` 3.2.1, CommunityToolkit.Mvvm, xunit v3 + Avalonia.Headless for App tests.

**Spec:** `docs/superpowers/specs/2026-07-10-m2c2-data-views-design.md` (Wave 1 section). Visual truth: `docs/design/m2/README.md` §Tasks view, §App shell, §States, §Interactions, §Responsive + `Lattice M2 Spec.html`.

**Branch:** `m2c2-scaffold-tasks` off latest main (contains the planning-docs commit once `m2c2-planning` merges; if not merged yet, branch off `m2c2-planning`).

**Standing rules for every task:** red-first (run the new test, see it fail for the stated reason, then implement); `dotnet build Lattice.sln -c Release -warnaserror` clean before every commit; conventional commit messages ending with the Claude co-author trailer; FluentAvalonia controls carry the FA prefix; no literal hex colors outside `Tokens.axaml`; every NEW user-facing string goes through `Strings.resx` from the moment it is written. When an Avalonia/FluentAvalonia API detail doesn't match this plan, consult the avalonia-docs MCP server, follow reality, and record the deviation in the ledger.

---

## File structure (created/modified this wave)

```
src/Lattice.App/
├── Localization/Strings.resx                 # NEW — single string table (default culture)
├── Infrastructure/
│   ├── HostStore.cs                          # MODIFY — RequestRefresh passthrough
│   ├── TimeText.cs                           # MODIFY — strings via Strings.resx + duration format
│   └── UiStateStore.cs                       # NEW — ui-state.json (density, column prefs)
├── Controls/StatusBarControl.cs              # NEW — 28px status strip (templated)
├── Theming/
│   ├── Icons.axaml                           # MODIFY — +8 icon keys
│   ├── DataGridStyles.axaml                  # NEW — grid theming per tokens
│   └── ControlStyles.axaml                   # NEW — StatusBarControl template
├── ViewModels/
│   ├── ShellViewModel.cs                     # MODIFY — RailEntries (All-hosts sentinel), scope, Tasks VM wiring
│   ├── AllHostsRailItemViewModel.cs          # NEW — aggregate rail entry
│   ├── TaskRowViewModel.cs                   # NEW — one grid row (pure projection)
│   ├── TaskStateKind.cs                      # NEW — Running/Waiting/Suspended/Uploading + mapping
│   └── TasksViewModel.cs                     # NEW — aggregation/scope/filter/sort/freshness/partial
└── Views/
    ├── ShellWindow.axaml(.cs)                # MODIFY — rail entries retemplate, filled-icon swap, nav count, Tasks template
    └── TasksView.axaml(.cs)                  # NEW — command bar + DataGrid + status bar + states

tests/Lattice.App.Tests/
├── LocalizationTests.cs                      # NEW
├── AllHostsRailTests.cs                      # NEW (VM)
├── TaskRowViewModelTests.cs                  # NEW (pure)
├── TasksViewModelTests.cs                    # NEW (store-driven)
├── UiStateStoreTests.cs                      # NEW
├── Headless/TasksViewTests.cs                # NEW
├── Headless/ShellRailTests.cs                # NEW (All-hosts entry, filled icons, count)
└── Headless/Journeys/
    ├── JourneyHarness.cs                     # NEW — mirrors App.axaml.cs graph with fakes
    └── *.cs                                  # NEW — 5 journeys (Task 13)
```

Responsibilities: `TaskRowViewModel` = one snapshot row → display values, no events; `TasksViewModel` = the only class that watches `HostStore.Changed`/clock for the Tasks page; `StatusBarControl` = dumb templated strip, all text set by consumers; `UiStateStore` = best-effort JSON persistence, never throws to callers.

---

### Task 1: ResX seam — generator package, Strings.resx, first key

**Files:**
- Modify: `src/Lattice.App/Lattice.App.csproj`
- Create: `src/Lattice.App/Localization/Strings.resx`
- Create: `tests/Lattice.App.Tests/LocalizationTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Lattice.App.Localization;
using Xunit;

namespace Lattice.App.Tests;

public class LocalizationTests
{
    [Fact]
    public void Strings_class_is_generated_public_and_resolves_keys()
    {
        Assert.Equal("Lattice", Strings.AppTitle);
    }

    [Fact]
    public void Every_resx_key_resolves_to_a_nonempty_string()
    {
        // Drift guard: enumerate the embedded table and pull every entry through
        // the generated accessor path (ResourceManager), catching bad renames.
        var rm = Strings.ResourceManager;
        var set = rm.GetResourceSet(System.Globalization.CultureInfo.InvariantCulture, true, true)!;
        var count = 0;
        foreach (System.Collections.DictionaryEntry entry in set)
        {
            Assert.False(string.IsNullOrEmpty(entry.Value as string),
                $"resx key '{entry.Key}' has an empty value");
            count++;
        }
        Assert.True(count >= 1);
    }
}
```

- [ ] **Step 2: Run it — expect compile FAIL (`Lattice.App.Localization` namespace does not exist)**

Run: `dotnet test tests/Lattice.App.Tests -c Release --filter "FullyQualifiedName~LocalizationTests"`

- [ ] **Step 3: Add the generator + resx**

In `Lattice.App.csproj` add to the existing `<PropertyGroup>`:

```xml
    <ResXFileCodeGenerator_PublicClass>true</ResXFileCodeGenerator_PublicClass>
    <ResXFileCodeGenerator_NullForgivingOperators>true</ResXFileCodeGenerator_NullForgivingOperators>
```

and a new item group:

```xml
  <ItemGroup>
    <PackageReference Include="VocaDb.ResXFileCodeGenerator" Version="3.2.1" PrivateAssets="all" />
  </ItemGroup>
```

Create `src/Lattice.App/Localization/Strings.resx` (resx XML envelope; Rider/`dotnet` treat it as EmbeddedResource automatically — verify it appears as `EmbeddedResource` in the build, add an explicit item only if it doesn't):

```xml
<?xml version="1.0" encoding="utf-8"?>
<root>
  <resheader name="resmimetype"><value>text/microsoft-resx</value></resheader>
  <resheader name="version"><value>2.0</value></resheader>
  <resheader name="reader"><value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
  <resheader name="writer"><value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
  <data name="AppTitle" xml:space="preserve"><value>Lattice</value></data>
</root>
```

The generator emits `namespace Lattice.App.Localization { public static class Strings { public static string AppTitle => ...; } }` (namespace = root namespace + folder, class = file name). If the generated namespace differs, fix the test's `using`, not the folder convention.

- [ ] **Step 4: Run the tests — expect PASS**

- [ ] **Step 5: x:Static smoke — point ShellWindow's `Title` at it**

In `ShellWindow.axaml` root element: add `xmlns:loc="using:Lattice.App.Localization"` and change `Title="Lattice"` → `Title="{x:Static loc:Strings.AppTitle}"`. Run the existing headless suite (`dotnet test tests/Lattice.App.Tests -c Release`) — all green proves compiled XAML sees the generated class.

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "feat(app): ResX i18n seam — VocaDb generator + Strings.resx + x:Static smoke"
```

---

### Task 2: ResX migration — M2c-1 XAML literals

**Files:**
- Modify: `src/Lattice.App/Localization/Strings.resx`
- Modify: `src/Lattice.App/Views/FirstRunView.axaml`, `PlaceholderView.axaml`, `ShellWindow.axaml`, `SettingsView.axaml`, `AddHostDialog.axaml`
- Modify: any headless test asserting these literals (reference `Strings.*` instead)

- [ ] **Step 1: Inventory.** `grep -n 'Text="[A-Z]' src/Lattice.App/Views/*.axaml` plus `Content=`, `ToolTip.Tip=`, `PlaceholderText=`, `Header=`, `Title=` string literals. Known set from FirstRunView: `FirstRunTitle` = "Connect your first host", `FirstRunCaption` = "Lattice monitors BOINC clients over GUI RPC (port 31416).", `FirstRunAddHost` = "Add a host"; from ShellWindow: `HostsHeader` = "Hosts", `AddHostTooltip` = "Add a host", nav labels `NavTasks/NavProjects/NavTransfers/NavEventLog/NavSettings` = "Tasks"/"Projects"/"Transfers"/"Event log"/"Settings". Sweep SettingsView + AddHostDialog the same way (field labels, button captions, dialog title, info strip, placeholder texts — key names `Settings*` / `AddHost*`).

- [ ] **Step 2: Add every key to `Strings.resx`; swap each literal for `{x:Static loc:Strings.<Key>}`** (add the `loc` xmlns per file). Values must stay byte-identical to today's UI text.

- [ ] **Step 3: Update tests that assert these strings** (e.g. dialog-flow tests matching button captions) to use `Strings.<Key>`, so copy edits never break tests.

- [ ] **Step 4: Full App test suite green; `-warnaserror` build clean.**

Run: `dotnet build Lattice.sln -c Release -warnaserror && dotnet test tests/Lattice.App.Tests -c Release`

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "refactor(app): migrate M2c-1 XAML strings through Strings.resx"
```

---

### Task 3: ResX migration — VM-composed strings

**Files:**
- Modify: `src/Lattice.App/Localization/Strings.resx`
- Modify: `src/Lattice.App/ViewModels/HostRailItemViewModel.cs`, `src/Lattice.App/Infrastructure/TimeText.cs`, `src/Lattice.App/ViewModels/SettingsViewModel.cs`, `src/Lattice.App/ViewModels/AddHostViewModel.cs`, `src/Lattice.App/ViewModels/HostSettingsItemViewModel.cs`
- Modify: their test files (compose expectations via `Strings.*`)

- [ ] **Step 1: Add format-string keys.** Exact table for the files already inventoried — extend the same way for the Settings/AddHost VMs' user-facing literals (error messages, dialog titles; exception `.Message` pass-throughs from the protocol layer are NOT localized — they're data, not chrome):

| Key | Value |
|---|---|
| `RailConnectedFmt` | `Connected · {0} tasks` |
| `RailConnecting` | `Connecting…` |
| `RailRetrying` | `Retrying…` |
| `RailRetryingFmt` | `Retrying in {0}s (attempt {1})` |
| `RailUnreachable` | `Unreachable` |
| `RailAuthFailed` | `Wrong password` |
| `UpdatedSecondsFmt` | `Updated {0}s ago` |
| `UpdatedMinutesFmt` | `Updated {0}m ago` |
| `UpdatedHoursFmt` | `Updated {0}h ago` |

- [ ] **Step 2: Swap compositions.** e.g. in `HostRailItemViewModel.Refresh()`: `$"Connected · {n} tasks"` → `string.Format(Strings.RailConnectedFmt, n)`; `TimeText.UpdatedAgo`/`RetryCountdown` use the `Updated*Fmt`/`RailRetryingFmt` keys. Update the corresponding tests (`RailStateTests`, `TimeTextTests`, `SettingsViewModelTests`, `AddHostViewModelTests`) to build expectations from `Strings.*` — behavior unchanged, so tests stay green throughout (this is a refactor task, not red-first).

- [ ] **Step 3: Full suite green + `-warnaserror`; commit**

```bash
git add -A && git commit -m "refactor(app): route VM-composed strings through Strings.resx"
```

---

### Task 4: HostStore.RequestRefresh passthrough

**Files:**
- Modify: `src/Lattice.App/Infrastructure/HostStore.cs`
- Test: `tests/Lattice.App.Tests/HostStoreTests.cs`

- [ ] **Step 1: Write the failing test** (HostStoreTests idiom — real manager, fake clients):

```csharp
    [Fact]
    public async Task RequestRefresh_wakes_the_scoped_monitor()
    {
        var fake = new FakeGuiRpcClient();
        _manager = new HostMonitorManager(_registry, () => fake, TimeProvider.System);
        var host = TestData.MakeHostConfig();
        _registry.AddHost(host);
        var store = new HostStore(_registry, _manager, new ImmediateUiDispatcher());
        _manager.Start();
        await Wait.UntilAsync(
            () => store.Hosts[0].Status.State == HostConnectionState.Connected,
            "connect first");
        var before = fake.Calls.Count(c => c == "get_results");

        store.RequestRefresh(host.Id);

        await Wait.UntilAsync(
            () => fake.Calls.Count(c => c == "get_results") > before,
            "an immediate poll should follow the refresh request");
    }
```

Note: the fixture's `InitializeAsync` builds `_manager` with a per-call `new FakeGuiRpcClient()`; this test rebuilds it with a shared instance so `Calls` is observable. Follow whatever rebuild idiom the file already uses (see `Status_and_snapshot_flow_from_a_running_monitor`).

- [ ] **Step 2: Run — expect compile FAIL (`RequestRefresh` not defined).**

- [ ] **Step 3: Implement** in `HostStore` (after `Hosts` property):

```csharp
    /// <summary>Ask the scoped monitor(s) to poll now. Null = all hosts.</summary>
    public void RequestRefresh(Guid? hostId = null)
    {
        foreach (HostMonitor monitor in _manager.Monitors)
            if (hostId is null || monitor.HostId == hostId)
                monitor.RequestRefresh();
    }
```

(`using Lattice.Core;` is already present.)

- [ ] **Step 4: Run — PASS. Full suite + `-warnaserror`.**

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(app): HostStore.RequestRefresh passthrough for F5/refresh button"
```

---

### Task 5: All-hosts rail entry + scope wiring

**Files:**
- Create: `src/Lattice.App/ViewModels/AllHostsRailItemViewModel.cs`
- Modify: `src/Lattice.App/ViewModels/ShellViewModel.cs`
- Modify: `src/Lattice.App/Views/ShellWindow.axaml` + `.axaml.cs`
- Test: `tests/Lattice.App.Tests/AllHostsRailTests.cs`, `tests/Lattice.App.Tests/Headless/ShellRailTests.cs`

Design (§App shell zone 2): first entry in the Hosts section is **All hosts** (aggregate), subtext shows partial state ("3 of 5 connected", or "N hosts" when all reachable); selecting it sets scope AllHosts; selected state uses brand tint. Selecting any entry scopes ALL views and persists across view switches.

- [ ] **Step 1: Failing VM tests** (`AllHostsRailTests.cs`):

```csharp
using Lattice.App.Infrastructure;
using Lattice.App.Tests.Fakes;
using Lattice.App.ViewModels;
using Lattice.Core;
using Lattice.Tests;
using Xunit;

namespace Lattice.App.Tests;

public class AllHostsRailTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}.json");
    private HostRegistry _registry = null!;
    private HostMonitorManager _manager = null!;
    private HostStore _store = null!;

    public ValueTask InitializeAsync()
    {
        _registry = new HostRegistry(new LatticeConfig(5, []), _path);
        _manager = new HostMonitorManager(_registry, () => new FakeGuiRpcClient(), TimeProvider.System);
        _store = new HostStore(_registry, _manager, new ImmediateUiDispatcher());
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _manager.DisposeAsync();
        File.Delete(_path);
    }

    private ShellViewModel MakeShell() =>
        new(_registry, _store, new ManualUiClock(), () => new FakeGuiRpcClient());

    [Fact]
    public void Rail_entries_lead_with_the_all_hosts_sentinel()
    {
        _registry.AddHost(TestData.MakeHostConfig());
        var shell = MakeShell();
        Assert.IsType<AllHostsRailItemViewModel>(shell.RailEntries[0]);
        Assert.IsType<HostRailItemViewModel>(shell.RailEntries[1]);
        Assert.Equal(2, shell.RailEntries.Count);
    }

    [Fact]
    public void Selecting_a_host_scopes_and_selecting_all_hosts_unscopes()
    {
        var host = TestData.MakeHostConfig();
        _registry.AddHost(host);
        var shell = MakeShell();

        shell.SelectedRailEntry = shell.RailEntries[1];
        Assert.Equal(host.Id, shell.Scope.HostId);

        shell.SelectedRailEntry = shell.RailEntries[0];
        Assert.True(shell.Scope.IsAllHosts);
    }

    [Fact]
    public void All_hosts_subtext_reports_partial_connectivity()
    {
        _registry.AddHost(TestData.MakeHostConfig(name: "a"));
        _registry.AddHost(TestData.MakeHostConfig(name: "b"));
        var shell = MakeShell();
        var all = (AllHostsRailItemViewModel)shell.RailEntries[0];
        // Both hosts are Disconnected (manager not started): 0 of 2 connected.
        Assert.Equal(string.Format(Lattice.App.Localization.Strings.AllHostsPartialFmt, 0, 2), all.Subtext);
    }
}
```

- [ ] **Step 2: Run — compile FAIL (`RailEntries`, `SelectedRailEntry`, `AllHostsRailItemViewModel` missing).**

- [ ] **Step 3: Implement.**

New resx keys: `AllHosts` = `All hosts`, `AllHostsCountFmt` = `{0} hosts`, `AllHostsPartialFmt` = `{0} of {1} connected`.

`AllHostsRailItemViewModel.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using Lattice.App.Localization;

namespace Lattice.App.ViewModels;

/// <summary>The aggregate first entry of the hosts rail (design §App shell zone 2).</summary>
public sealed partial class AllHostsRailItemViewModel : ObservableObject
{
    public string Name => Strings.AllHosts;

    [ObservableProperty] private string _subtext = "";
    [ObservableProperty] private string? _tooltip;

    /// <summary>connected/total drive both the subtext and the partial styling.</summary>
    public void Update(int connected, int total)
    {
        Subtext = connected == total
            ? string.Format(Strings.AllHostsCountFmt, total)
            : string.Format(Strings.AllHostsPartialFmt, connected, total);
        Tooltip = $"{Name} — {Subtext}";
    }
}
```

`ShellViewModel` changes:
- Add `public ObservableCollection<object> RailEntries { get; } = [];` seeded with a single `AllHostsRailItemViewModel` (field `_allHosts`) in the constructor, followed by host items. Keep `HostItems` REMOVED — fold the reconcile into `RailEntries` (indices shift by one: entry `i+1` ↔ `_store.Hosts[i]`). Update `ReconcileHosts` accordingly and call `_allHosts.Update(connected, total)` at its end, where `connected = _store.Hosts.Count(h => RailStateProjection.From(h.Status) == RailState.Connected)`.
- Add `[ObservableProperty] private object? _selectedRailEntry;` initialized to the sentinel; `partial void OnSelectedRailEntryChanged(object? value)` sets `Scope = value switch { HostRailItemViewModel h => new ScopeSelection(h.HostId), _ => ScopeSelection.AllHosts };` (null → AllHosts too, so removing the scoped host degrades safely — add that reassignment in `ReconcileHosts` when the scoped id vanished).
- `ShellWindow.axaml`: `HostList` binds `ItemsSource="{Binding RailEntries}"` + `SelectedItem="{Binding SelectedRailEntry, Mode=TwoWay}"`; give the ListBox **two** DataTemplates (`DataTemplate x:DataType="vm:AllHostsRailItemViewModel"` = 40px two-line entry, apps-list style icon `IconAppsListRegular` (added in Task 8; use `IconGridRegular` until then), name 13px + subtext 11px, same `IsVisible="{Binding #Nav.IsPaneOpen}"` text-hiding and `ToolTip.Tip="{Binding Tooltip}"` as host entries). Existing host template unchanged.
- `ShellWindow.axaml.cs` `OnHostSelectionChanged`: keep the auth-failed settings-navigation behavior for `HostRailItemViewModel`; the sentinel never navigates.
- Update existing tests that referenced `HostItems` (ShellViewModelTests, ShellWindowTests, AuthFailedLinkageTests use the rail list) — mechanical rename to `RailEntries` with the +1 offset or a `OfType<HostRailItemViewModel>()` helper.

- [ ] **Step 4: Headless pin** (`Headless/ShellRailTests.cs`): render `ShellWindow` with two fake hosts, assert (a) first rail row shows `Strings.AllHosts`, (b) clicking a host row then switching views (`shell.SelectViewCommand`) leaves `Scope` unchanged, (c) collapsed pane keeps the sentinel icon-only (reuse the geometry assertions from `Collapsed_pane_shows_host_state_icon_only` as the template).

- [ ] **Step 5: Full suite + `-warnaserror`; commit**

```bash
git add -A && git commit -m "feat(app): All-hosts rail entry, RailEntries sentinel list, scope wiring"
```

---

### Task 6: TaskRowViewModel + state mapping (pure projection)

**Files:**
- Create: `src/Lattice.App/ViewModels/TaskStateKind.cs`, `src/Lattice.App/ViewModels/TaskRowViewModel.cs`
- Modify: `src/Lattice.App/Infrastructure/TimeText.cs` (duration formatter)
- Test: `tests/Lattice.App.Tests/TaskRowViewModelTests.cs`

State mapping (design §Tasks rows; BOINC semantics from `ResultState` + `ActiveTask`):

| Condition (first match wins) | Kind | Icon key | Brush token |
|---|---|---|---|
| `Result.SuspendedViaGui` | Suspended | `IconPauseRegular` | `LatticeNeutralFgBrush` |
| `State == FilesUploading or FilesUploaded or UploadFailed` | Uploading | `IconArrowUploadRegular` | `LatticeAccentBrush` |
| `ActiveTask is { ActiveTaskState: 1 }` (EXECUTING) | Running | `IconPlayRegular` | `LatticeSuccessBrush` |
| everything else (downloading, queued, errors, aborted) | Waiting | `IconClockRegular` | `LatticeNeutralFgBrush` |

`StateText`: Suspended → `Strings.TaskStateSuspended` ("Suspended"); Uploading → `Strings.TaskStateUploading` ("Uploading"); Running → `Strings.TaskStateRunning` ("Running"); Waiting → per underlying state: `ComputeError` → "Error", `Aborted` → "Aborted", else "Waiting" (keys `TaskStateError`, `TaskStateAborted`, `TaskStateWaiting`).

- [ ] **Step 1: Failing tests** (pure — build `TaskSnapshot` around `TestData.MakeResult()` and record `with { }` mutations):

```csharp
using Lattice.App.Localization;
using Lattice.App.ViewModels;
using Lattice.Boinc.GuiRpc;
using Lattice.Core;
using Lattice.Tests;
using Xunit;

namespace Lattice.App.Tests;

public class TaskRowViewModelTests
{
    private static TaskSnapshot Snap(Result r, bool atRisk = false, double elapsed = 90) =>
        new(r, "SETI", "astropulse", elapsed, atRisk);

    [Fact]
    public void Running_task_maps_to_running_kind_with_progress()
    {
        var r = TestData.MakeResult() with
        {
            ActiveTask = new ActiveTask(1, 0.42, 10, 90),
            SuspendedViaGui = false,
        };
        var row = TaskRowViewModel.From(Snap(r), "office-pc");
        Assert.Equal(TaskStateKind.Running, row.StateKind);
        Assert.Equal(Strings.TaskStateRunning, row.StateText);
        Assert.Equal(0.42, row.Fraction);
        Assert.Equal("42%", row.PercentText);
        Assert.Equal("office-pc", row.Host);
    }

    [Fact]
    public void Suspended_beats_running()
    {
        var r = TestData.MakeResult() with
        {
            ActiveTask = new ActiveTask(1, 0.42, 10, 90),
            SuspendedViaGui = true,
        };
        Assert.Equal(TaskStateKind.Suspended, TaskRowViewModel.From(Snap(r), "h").StateKind);
    }

    [Fact]
    public void Unknown_fraction_renders_dash_never_indeterminate()
    {
        var r = TestData.MakeResult() with { ActiveTask = null };
        var row = TaskRowViewModel.From(Snap(r), "h");
        Assert.Null(row.Fraction);
        Assert.Equal("—", row.PercentText);
    }

    [Fact]
    public void Deadline_at_risk_flag_passes_through()
    {
        var row = TaskRowViewModel.From(Snap(TestData.MakeResult(), atRisk: true), "h");
        Assert.True(row.IsDeadlineAtRisk);
    }

    [Theory]
    [InlineData(45, "45s")]
    [InlineData(3 * 60 + 20, "3m 20s")]
    [InlineData(2 * 3600 + 5 * 60, "2h 05m")]
    [InlineData(26 * 3600, "1d 02h")]
    public void Durations_format_per_design(double seconds, string expected) =>
        Assert.Equal(expected, TimeText.Duration(seconds));
}
```

- [ ] **Step 2: Run — compile FAIL.**

- [ ] **Step 3: Implement.**

`TaskStateKind.cs`:

```csharp
using Lattice.App.Localization;
using Lattice.Boinc.GuiRpc;

namespace Lattice.App.ViewModels;

public enum TaskStateKind { Running, Waiting, Suspended, Uploading }

public static class TaskStateMapping
{
    public static TaskStateKind From(Result r) => r switch
    {
        { SuspendedViaGui: true } => TaskStateKind.Suspended,
        { State: ResultState.FilesUploading or ResultState.FilesUploaded or ResultState.UploadFailed }
            => TaskStateKind.Uploading,
        { ActiveTask.ActiveTaskState: 1 } => TaskStateKind.Running,
        _ => TaskStateKind.Waiting,
    };

    public static string Text(TaskStateKind kind, Result r) => kind switch
    {
        TaskStateKind.Running => Strings.TaskStateRunning,
        TaskStateKind.Suspended => Strings.TaskStateSuspended,
        TaskStateKind.Uploading => Strings.TaskStateUploading,
        _ => r.State switch
        {
            ResultState.ComputeError => Strings.TaskStateError,
            ResultState.Aborted => Strings.TaskStateAborted,
            _ => Strings.TaskStateWaiting,
        },
    };
}
```

`TimeText.Duration` (append to TimeText):

```csharp
    /// <summary>Compact duration for Elapsed/Remaining cells: 45s · 3m 20s · 2h 05m · 1d 02h.</summary>
    public static string Duration(double seconds)
    {
        var s = (long)Math.Max(0, seconds);
        return s switch
        {
            < 60 => $"{s}s",
            < 3600 => $"{s / 60}m {s % 60:00}s",
            < 86400 => $"{s / 3600}h {s % 3600 / 60:00}m",
            _ => $"{s / 86400}d {s % 86400 / 3600:00}h",
        };
    }
```

`TaskRowViewModel.cs` — immutable row, `From(TaskSnapshot, string host)` factory computing: `Project` (=ProjectName), `Application`, `Name` (Result.Name), `Fraction` (double?, from `ActiveTask.FractionDone`, null when no active task and state < FilesUploading; a finished/uploading task shows 1.0), `PercentText` (`"—"` when null else `$"{Fraction:P0}"` without space — use `Math.Round(f * 100)` + `"%"` to avoid culture spacing), `ElapsedText` (`TimeText.Duration(ElapsedSeconds)`), `RemainingText` (`Result.EstimatedCpuTimeRemaining` > 0 → Duration, else "—"), `DeadlineText` (`Result.ReportDeadline?.ToLocalTime().ToString("MM-dd HH:mm")` else "—"), `Deadline` (raw `DateTimeOffset?` for sorting), `StateKind`, `StateText`, `IsDeadlineAtRisk`, `IsSuspended` (=> StateKind==Suspended), `Host`. New resx keys per the table above plus none others.

- [ ] **Step 4: Run — PASS. Commit**

```bash
git add -A && git commit -m "feat(app): TaskRowViewModel projection + state mapping + duration format"
```

---

### Task 7: TasksViewModel — scope, merge, filter, sort, counts, freshness, partial

**Files:**
- Create: `src/Lattice.App/ViewModels/TasksViewModel.cs`
- Test: `tests/Lattice.App.Tests/TasksViewModelTests.cs`

Contract: constructor `(HostStore store, IUiClock clock, ShellViewModel shell)` is wrong coupling — take `(HostStore store, IUiClock clock)` and expose `public ScopeSelection Scope { get; set; }` that ShellViewModel pushes on change (keeps TasksViewModel shell-agnostic and testable). Rebuild rows on: `store.Changed`, scope set, filter/state-filter change. Subscribes in ctor, `IDisposable` unsubscribes (dispose pattern copied from HostRailItemViewModel).

Public surface (all `[ObservableProperty]` unless noted): `Rows` (`ObservableCollection<TaskRowViewModel>`, wholesale `Clear()+Add` rebuild — 500 rows is fine, no incremental diffing), `FilterText` (string), `StateFilter` (`TaskStateKind?` — null = All; view maps combo index), `IsAllHostsScope` (drives Host column), `CountsText`, `AtRiskText` (empty when 0), `PollingText`, `UpdatedText`, `IsUpdateStale` (bool — any scoped host Retrying/Unreachable), `ShowPartialBar`, `PartialBarText`, `IsLoading` (scope has no snapshot yet), `IsEmpty`, `RefreshCommand` (`store.RequestRefresh(Scope.HostId)`), `DismissPartialCommand`.

Semantics pinned by tests below: rows sorted Deadline ascending (nulls last) as the DEFAULT — DataGrid user sorting happens view-side on top of this; filter = OrdinalIgnoreCase substring on Name OR ProjectName; counts cover reachable (Connected) hosts only and read "47 tasks · 8 running · 2 uploading · 1 suspended" (resx `CountsFmt` = `{0} tasks · {1} running · {2} uploading · {3} suspended`, `AtRiskFmt` = `⚠ {0} deadline at risk`, `PollingFmt` = `Polling every {0}s`, `PartialFmt` = `{0} of {1} hosts aren't reachable — tasks below cover {2} hosts.`, `PartialTitle` = `Partial results.`); partial bar only when `Scope.IsAllHosts && unreachable >= 1` where unreachable = RailStateProjection Unreachable OR AuthFailed tier; dismissing hides it until the unreachable id-set changes (compare `HashSet<Guid>`).

- [ ] **Step 1: Failing tests** — same fixture idiom as AllHostsRailTests (registry + manager w/ per-host fake clients + store). Feed data by configuring each host's fake: `fake.OnGetResults = _ => Task.FromResult<IReadOnlyList<Result>>([TestData.MakeResult() with { ... }])` **before** `_manager.Start()`, then `Wait.UntilAsync` for snapshots. Cover: (a) all-hosts merge shows rows from both hosts with Host set + `IsAllHostsScope`; (b) setting `Scope` to host A hides B's rows and flips `IsAllHostsScope`; (c) deadline ascending default with null-deadline rows last; (d) `FilterText="seti"` matches project case-insensitively; (e) `StateFilter=Suspended` filters; (f) counts string exact-matches `string.Format(Strings.CountsFmt, ...)` for a crafted mix; (g) partial bar appears when one host's fake `OnConnect` throws (`BoincConnectException` — reuse whatever ConfigFallback/SettingsViewModel tests throw) and scope is AllHosts, `DismissPartialCommand` hides it, and it reappears after the OTHER host also fails (set-change); (h) `UpdatedText` ticks via `ManualUiClock.Advance` (assert `string.Format(Strings.UpdatedSecondsFmt, 3)` after advancing 3 s past the snapshot timestamp); (i) `IsLoading` true before first scoped snapshot, `IsEmpty` when connected with zero tasks. Write each as its own `[Fact]`, following the arrange helpers of TasksViewModelTests' sibling files.

- [ ] **Step 2: Run — compile FAIL.**

- [ ] **Step 3: Implement `TasksViewModel`** per the contract above. Rebuild pipeline (single private `Rebuild()`):

```csharp
    private void Rebuild()
    {
        var scoped = Scope.IsAllHosts
            ? _store.Hosts
            : _store.Hosts.Where(h => h.Config.Id == Scope.HostId).ToList();
        IsAllHostsScope = Scope.IsAllHosts;

        var reachable = scoped.Where(h =>
            RailStateProjection.From(h.Status) == RailState.Connected).ToList();
        var rows = reachable
            .Where(h => h.Snapshot is not null)
            .SelectMany(h => h.Snapshot!.Tasks.Select(t =>
                TaskRowViewModel.From(t, h.Config.DisplayName)))
            .Where(MatchesFilters)
            .OrderBy(r => r.Deadline is null)
            .ThenBy(r => r.Deadline)
            .ToList();

        Rows.Clear();
        foreach (var row in rows) Rows.Add(row);
        // counts / partial / freshness / loading+empty recomputed here too —
        // one entry point, no partial invalidation bugs.
        ...
    }
```

(The `...` is shorthand ONLY in this plan excerpt for the counts/partial/freshness blocks whose exact strings and predicates are pinned by Step 1's tests — implement them in the same method until every Step 1 test passes; no other behavior.)

- [ ] **Step 4: Run — all TasksViewModelTests PASS; full suite; `-warnaserror`.**

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(app): TasksViewModel — scope/merge/filter/sort/counts/freshness/partial"
```

---

### Task 8: Icon batch

**Files:**
- Modify: `src/Lattice.App/Theming/Icons.axaml` (via `generate-icons.sh`)
- Modify: `tests/Lattice.App.Tests/Headless/ThemeResourceTests.cs`

- [ ] **Step 1: Failing test rows** — extend the existing ThemeResourceTests icon-key theory with: `IconPlayRegular`, `IconPauseRegular`, `IconClockRegular`, `IconArrowUploadRegular`, `IconWarningFilled`, `IconSearchRegular`, `IconMoreHorizontalRegular`, `IconTextLineSpacingRegular`, `IconAppsListRegular`. Run — FAIL (keys missing).

- [ ] **Step 2: Generate.** Read `src/Lattice.App/Theming/generate-icons.sh` header for its usage (it fetches path data from microsoft/fluentui-system-icons); add the nine names (fluent names: `play`, `pause`, `clock`, `arrow_upload`, `warning` filled variant, `search`, `more_horizontal`, `text_line_spacing`, `apps_list`) and regenerate. If the network is unavailable, fetch the SVGs manually from the repo and transcribe the path data — but keep the script's list in sync either way.

- [ ] **Step 3: Tests PASS; swap the Task-5 placeholder** (`IconGridRegular` on the All-hosts template → `IconAppsListRegular`).

- [ ] **Step 4: Commit**

```bash
git add -A && git commit -m "feat(theming): icon batch for Tasks view + All-hosts entry"
```

---

### Task 9: DataGrid infrastructure

**Files:**
- Modify: `src/Lattice.App/Lattice.App.csproj` (+`Avalonia.Controls.DataGrid` — same version as the Avalonia packages)
- Create: `src/Lattice.App/Theming/DataGridStyles.axaml`
- Modify: `src/Lattice.App/App.axaml` (theme include + styles include)
- Test: `tests/Lattice.App.Tests/Headless/DataGridInfraTests.cs`

- [ ] **Step 1: Failing headless test** — render a bare `DataGrid` with two `TaskRowViewModel`-shaped columns inside a themed window (TestAppBuilder session), assert it materializes rows and the Lattice header style resolves (find a `DataGridColumnHeader` in the visual tree with FontSize 11). Expect FAIL: DataGrid template missing (package/theme not referenced → control renders empty or throws).

- [ ] **Step 2: Wire the package + theme.** csproj: `<PackageReference Include="Avalonia.Controls.DataGrid" Version="12.0.5" />` (match the solution's Avalonia version). `App.axaml` styles, after FluentAvaloniaTheme:

```xml
    <StyleInclude Source="avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml" />
    <StyleInclude Source="avares://Lattice.App/Theming/DataGridStyles.axaml" />
```

- [ ] **Step 3: `DataGridStyles.axaml`** — Styles file (not ResourceDictionary): header 32px/11 SemiBold secondary; row height via `Lattice` classes (`DataGrid.lattice` rows 36, `DataGrid.lattice.compact` rows 28 — set `RowHeight` in the two class styles); cells 13px primary, `Padding 8,0`, left-aligned; tabular numerals on `.numeric` cells (`FontFeatures="tnum"` — verify the property exists on Avalonia 12 TextBlock via avalonia-docs; if not, drop to a comment and rely on the UI font's default figures); selected row background `LatticeSelectedTintBrush`; gridlines `LatticeStrokeSubtleBrush`, horizontal only. All colors via `DynamicResource` tokens.

- [ ] **Step 4: Test PASS; full suite; commit**

```bash
git add -A && git commit -m "feat(theming): DataGrid package + Lattice grid styles"
```

---

### Task 10: UiStateStore + StatusBarControl

**Files:**
- Create: `src/Lattice.App/Infrastructure/UiStateStore.cs`
- Create: `src/Lattice.App/Controls/StatusBarControl.cs`, `src/Lattice.App/Theming/ControlStyles.axaml` (+ include in App.axaml styles)
- Test: `tests/Lattice.App.Tests/UiStateStoreTests.cs`, `tests/Lattice.App.Tests/Headless/StatusBarControlTests.cs`

- [ ] **Step 1: UiStateStore failing tests** — round-trips `UiState(bool CompactDensity, Dictionary<string, bool> ColumnVisibility, Dictionary<string, double> ColumnWidths)` to a JSON path; missing file → defaults; write failure (dir made read-only à la ConfigFallbackTests' platform-forked locking) → `Save` returns false, never throws; corrupt JSON → defaults (no quarantine drama — UI state is disposable, unlike the host registry).

- [ ] **Step 2: Implement** — `System.Text.Json`, path `Path.Combine(Path.GetDirectoryName(LatticeConfig.DefaultPath)!, "ui-state.json")` by default, injectable path for tests; catch-set identical to RegistryGuard's (`IOException`, `UnauthorizedAccessException`, `JsonException` on load). Run — PASS.

- [ ] **Step 3: StatusBarControl failing headless test** — a `TemplatedControl` with three string StyledProperties (`LeftText`, `WarningText`, `RightText`); template = 28px `DockPanel`: left text 12px secondary, warning (visible when non-empty) 12px `LatticeWarningFgBrush` + `IconWarningFilled` 12px, right text docked right. Test renders it with all three set and asserts the three TextBlocks + height 28.

- [ ] **Step 4: Implement** (`StyledProperty<string>` × 3, `ControlStyles.axaml` holds the ControlTheme; include after DataGridStyles). Run — PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(app): UiStateStore + StatusBarControl (28px strip)"
```

---

### Task 11: TasksView — command bar, grid, status bar, states

**Files:**
- Create: `src/Lattice.App/Views/TasksView.axaml` + `.axaml.cs`
- Modify: `src/Lattice.App/Localization/Strings.resx` (command-bar keys below)
- Test: `tests/Lattice.App.Tests/Headless/TasksViewTests.cs`

New resx keys: `TasksTitle` = `Tasks`, `Suspend` = `Suspend`, `Abort` = `Abort`, `FilterPlaceholder` = `Filter by name or project`, `StateAll` = `State: All`, `TasksEmpty` = `No tasks on this host.`, `TasksEmptyAll` = `No tasks on any reachable host.`, `LoadingFromFmt` = `Fetching state from {0}…`, `RetryNow` = `Retry now`, plus column headers `ColProject/ColApplication/ColTask/ColProgress/ColElapsed/ColRemaining/ColDeadline/ColState/ColHost` = `Project/Application/Task/Progress/Elapsed/Remaining/Deadline/State/Host`.

Layout (all sizes from tokens; `x:DataType="vm:TasksViewModel"`):

```
DockPanel
├── Top: command bar Border (52px, LatticeSurfaceBrush, bottom stroke)
│     StackPanel: title 16 SemiBold · [Suspend][Abort] disabled Buttons ·
│     Separator · AutoSuggestBox Width=220 Text={Binding FilterText} ·
│     ComboBox (All/Running/Waiting/Suspended/Uploading → StateFilter) ·
│     spacer · Updated TextBlock {Binding UpdatedText} (+ warning icon+color
│     when IsUpdateStale) · refresh Button → RefreshCommand (IconArrowClockwiseRegular) ·
│     density ToggleButton (IconTextLineSpacingRegular) · overflow Button (IconMoreHorizontalRegular)
│     with MenuFlyout of CheckBox items per hideable column
├── Top: FAInfoBar Severity=Warning IsOpen={Binding ShowPartialBar}
│     Title={x:Static loc:Strings.PartialTitle} Message={Binding PartialBarText}
│     ActionButton → RefreshCommand (Strings.RetryNow); Closed → DismissPartialCommand
├── Bottom: controls:StatusBarControl LeftText={Binding CountsText}
│     WarningText={Binding AtRiskText} RightText={Binding PollingText}
└── Fill: Panel
      ├── DataGrid Classes="lattice" Classes.compact={Binding IsCompact}
      │    ItemsSource={Binding Rows} IsReadOnly CanUserSortColumns
      │    columns per design: Project 108 · Application 118 · Task * min160
      │    (TextTrimming + ToolTip full name) · Progress 112 (template below) ·
      │    Elapsed 68 · Remaining 74 · Deadline 100 (template: warning filled icon
      │    + SemiBold warning fg when IsDeadlineAtRisk) · State 112 (icon+text
      │    per StateKind, four stacked PathIcons pattern from the rail) ·
      │    Host 76 IsVisible←IsAllHostsScope
      ├── loading overlay (IsVisible={Binding IsLoading}): ProgressRing +
      │    TextBlock LoadingFromFmt + 3 shimmer Border lines
      └── empty overlay (IsVisible={Binding IsEmpty}): centered secondary text
```

Progress cell template: `StackPanel Orientation=Horizontal Spacing=8` → `Border` track 56×3 `LatticeStrokeBrush` CornerRadius 2 containing left-aligned fill `Border` (`Width = Fraction*56` via a small `FractionToWidthConverter` in the view's Resources; null → 0) `LatticeAccentBrush` (gray `LatticeDisabledBrush` when row suspended), + `TextBlock PercentText` 12px classes `numeric`.

Row states: `DataGrid.Styles` row style — at-risk rows `Background=LatticeWarningTintBrush`, suspended rows foreground `LatticeTextSecondaryBrush` (bind via row classes: `RowClass` isn't a DataGrid concept — use `DataGridRow` style selectors on computed properties via `Classes.atRisk`/`Classes.suspended` set in `LoadingRow` event in code-behind, the standard Avalonia DataGrid idiom; verify with avalonia-docs).

Code-behind responsibilities (keep thin): `LoadingRow` row-class assignment; density toggle → VM `IsCompact` (add `[ObservableProperty] bool isCompact` to TasksViewModel + persist through `UiStateStore` — wire store in Task 12); overflow column-visibility checkboxes ↔ `DataGridColumn.IsVisible` + UiStateStore; breakpoints: subscribe `BoundsProperty` of the view — width < 1100 hides Elapsed, < 1000 also hides Application, unless the user set an explicit preference (UiStateStore value wins); KeyBindings: `Ctrl+F` focuses the filter box, `F5` → `RefreshCommand`; `SelectionChanged` nothing (read-only M2).

- [ ] **Step 1: Failing headless tests** (`TasksViewTests.cs`, TestAppBuilder session; instantiate `TasksView` with a real TasksViewModel fed by the Task-7 fixture): (a) themed render shows 9 column headers matching `Strings.Col*`; (b) Host column hidden when scope = single host; (c) at-risk row carries `atRisk` class after `LoadingRow`; (d) density toggle flips row height 36→28; (e) empty overlay text appears for connected-zero-tasks; (f) filter TextBox receives focus on Ctrl+F. Run — compile FAIL (`TasksView` missing).

- [ ] **Step 2: Implement the XAML + code-behind above.** Run per-test until all PASS.

- [ ] **Step 3: Full suite + `-warnaserror`; commit**

```bash
git add -A && git commit -m "feat(app): TasksView — command bar, DataGrid, status bar, loading/empty/partial states"
```

---

### Task 12: Shell wiring — Tasks page, filled icons, nav count

**Files:**
- Modify: `src/Lattice.App/ViewModels/ShellViewModel.cs`, `src/Lattice.App/Views/ShellWindow.axaml` + `.axaml.cs`, `src/Lattice.App/App.axaml.cs`
- Test: `tests/Lattice.App.Tests/Headless/ShellRailTests.cs` (extend), `tests/Lattice.App.Tests/ShellViewModelTests.cs` (extend)

- [ ] **Step 1: Failing tests:** (a) VM: `Views[0].Page` is a `TasksViewModel` (not Placeholder) and setting `shell.Scope` propagates to it; (b) VM: `shell.TasksCount` mirrors the Tasks VM row count (drives the nav inline count); (c) headless: navigating to Tasks renders a `TasksView`; (d) headless: selecting the Tasks nav item swaps its icon to `IconTaskListSquareLtrFilled` (compare `FAPathIconSource.Data` reference against the resource), deselecting restores Regular.

- [ ] **Step 2: Implement.**
- ShellViewModel: construct `Tasks = new TasksViewModel(store, clock)` (+ `UiStateStore` param threaded from composition for density/columns), pass it as `Views[0]`'s page; `OnScopeChanged` partial pushes `Scope` into `Tasks.Scope`; expose `TasksCount` (subscribe `Tasks.Rows.CollectionChanged`); dispose Tasks in `Dispose()`.
- ShellWindow.axaml: `DataTemplate DataType="vm:TasksViewModel" → v:TasksView`; Tasks nav item Content becomes a 2-column DockPanel (label + right-aligned 12px secondary count bound to `TasksCount`, hidden when 0) — FANavigationViewItem accepts arbitrary Content; keep `Tag="0"`.
- ShellWindow.axaml.cs `OnNavSelectionChanged`: after the existing page switch, swap each menu item's `IconSource` `Data` between the Regular/Filled resource pair from its `NavItemViewModel` (`Views[i].RegularIcon/FilledIcon` names already exist — `TryFindResource` both once, cache).
- App.axaml.cs: construct `UiStateStore` beside the registry and pass through ShellViewModel; extend `desktop.Exit` teardown accordingly (JourneyHarness in Task 13 must mirror this line — grep pin below).

- [ ] **Step 3: All tests green; full suite; `-warnaserror`; commit**

```bash
git add -A && git commit -m "feat(app): wire Tasks view into shell — filled icons, nav count, scope push"
```

---

### Task 13: Journey test infrastructure + five journeys

**Files:**
- Create: `tests/Lattice.App.Tests/Headless/Journeys/JourneyHarness.cs`
- Create: `tests/Lattice.App.Tests/Headless/Journeys/AddHostJourney.cs`, `AuthFailJourney.cs`, `PersistenceJourney.cs`, `TasksScopeJourney.cs`, `PartialResultsJourney.cs`

`JourneyHarness` (IAsyncDisposable): builds the FULL production graph exactly as `App.OnFrameworkInitializationCompleted` does — `LoadRegistryWithFallback(tempPath)` (App's internal, visible to tests) → `HostMonitorManager(registry, factoryFn, TimeProvider.System)` → `HostStore(registry, manager, ImmediateUiDispatcher)` → `ManualUiClock` → `ShellViewModel` → real `ShellWindow` shown headless — with exactly two substitutions: client factory (routes per host address to a caller-configured `FakeGuiRpcClient`) and temp config path. Add a comment pinning it to App.axaml.cs: **any composition change there must be mirrored here in the same commit** (this is the composition-root coverage the ledger asked for). Expose `Window`, `Shell`, `Registry`, `ClientFor(string address)`, `manager.Start()` passthrough, and `SettleAsync(predicate, reason)` delegating to `HeadlessSync.WaitUntilAsync`.

- [ ] **Step 1: Harness + first journey red-first.** `AddHostJourney`: first-run window shows FirstRunView → invoke add-host flow programmatically through the real dialog VM path (reuse AddHostDialogTests' driving idiom) with a fake that connects and returns 2 tasks → assert rail gains the host entry with "Connected · 2 tasks" (via `Strings.RailConnectedFmt`), nav count shows 2, TasksView shows 2 rows, and `config.json` on disk now contains the host. Run against a stub harness first to see it fail meaningfully (e.g. assert before wiring `Start()` — verify the failure is the awaited condition timing out, not a harness crash), then finish the harness until green.

- [ ] **Step 2: `AuthFailJourney`** — fake `OnAuthorize = _ => false` → rail shows AuthFailed (`Strings.RailAuthFailed`) → simulate the rail click path (`shell.NavigateToSettings(hostId)` via the same code path the click handler uses) → Settings page current + expander focused (assert `Settings.ExpandHost` effect the way AuthFailedLinkageTests does) → correct the password via the settings VM + save → flip the fake to authorize → assert rail reaches Connected.

- [ ] **Step 3: `PersistenceJourney`** — add host, dispose harness, build a SECOND harness on the same temp path → host present and reconnecting at startup; polling interval survives too.

- [ ] **Step 4: `TasksScopeJourney`** — two hosts (3 + 2 tasks) → All hosts: 5 rows, Host column visible; select host B in rail: 2 rows, Host column hidden; switch view Projects-placeholder and back: scope still host B.

- [ ] **Step 5: `PartialResultsJourney`** — two hosts, one refuses connections → All-hosts scope shows the partial InfoBar with `string.Format(Strings.PartialFmt, 1, 2, 1)`; dismiss hides; kill the second host's fake (make its next poll throw + `RequestRefresh`) → bar reappears (set changed); All-hosts rail subtext shows `0 of 2 connected`.

- [ ] **Step 6: Full suite ×3 consecutive runs green (flake check: `for i in 1 2 3; do dotnet test tests/Lattice.App.Tests -c Release || break; done`); commit**

```bash
git add -A && git commit -m "test(app): journey harness mirroring the composition root + five journeys"
```

---

### Task 14: Final sweep — ledger, PR, gates

- [ ] **Step 1: Full verification.** `dotnet build Lattice.sln -c Release -warnaserror` + `dotnet test Lattice.sln -c Release` (all three test projects: 193 Lattice.Tests + 36 Verification untouched and green, App.Tests grown). Debug configuration builds too.

- [ ] **Step 2: Ledger update** (`.superpowers/sdd/progress.md`): add "M2c-2 Wave 1" section — task list, deviations, new-test counts, W0 issue numbers (#11–#18) noted as the migrated follow-up tracking.

- [ ] **Step 3: Push + PR** to main titled `M2c-2 W1: view scaffold + Tasks view (ResX seam, DataGrid infra, All-hosts scope, journey tests)`; body: summary, key decisions (VocaDb generator, RailEntries sentinel, UiStateStore), test counts, and the user-demo checklist: add localhost host → Tasks view live rows vs BOINC Manager; All-hosts vs single-host scope; filter/state combo; density toggle; column hide via overflow; window narrow → breakpoint hiding; unplug daemon → partial InfoBar + Updated staleness; F5/Ctrl+F.

- [ ] **Step 4: Gates.** Dispatch pr-monitor (burst pattern) for Codex + 4-leg CI; fix findings per the standing review rules (fix direct on the open PR); after green+clean, hand to the user for the SOLO DEMO GATE (spec decision 3). Do not merge before Codex posts and the user approves.

---

## Self-review notes (kept for the record)

- Spec coverage: ResX seam (T1–3), RequestRefresh/F5 (T4+11), All-hosts + partial three-layer (T5+7+11: nav subtext / InfoBar / status-bar counts), command bar (T11), DataGrid infra (T9), status bar control (T10), Tasks view + row states + densities + breakpoints (T6/7/11), filled icons (T12), journey infra + journeys (T13), gates (T14). Column-width persistence: UiStateStore stores widths (T10) but no column supports user drag-resize wiring in this wave's XAML — widths persist via overflow-visibility only; full drag-width persistence lands with the Wave-2 views when the column set stabilizes (recorded as a deliberate deferral, note it in the PR body).
- Types cross-checked: `ScopeSelection(Guid?)`, `RailStateProjection.From(ConnectionStatus)`, `TaskSnapshot(Result, string, string, double, bool)`, `FakeGuiRpcClient.OnGetResults(Func<bool, Task<IReadOnlyList<Result>>>)`, `HostMonitor.RequestRefresh()`, `manager.Monitors` — all verified against current source this session.
- Known API-risk points flagged inline for avalonia-docs lookup: `FontFeatures` on TextBlock, DataGrid `LoadingRow` row-class idiom, FANavigationViewItem custom Content, AutoSuggestBox binding shape.
