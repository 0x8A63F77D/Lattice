# M2 Shell Rework (rev. B hosts rail 3a + host management 3b) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rework the NavigationView shell to the rev. B design — hosts rail bottom-docked in `PaneFooter` above Settings, height-adaptive flat↔status-group layout driven by a pure F# core, host management moved out of Settings into a right-click rail menu, and a new Theme setting.

**Architecture:** A born-pure F# decision core (`Lattice.App.Aggregation/RailLayout.fs`) decides `SingleHost | Flat | Grouped` layout from measured footer height + host tiers + persisted override/expand state. `ShellViewModel` measures/persists the inputs, calls the core, and reconciles `RailEntries` (All-hosts sentinel · host rows · group-header rows). Host add/edit/remove/test move to a mode-aware `AddHostDialog` launched from a rail `MenuFlyout`; Settings keeps global groups (Polling + new Theme) only. Compact (48 px) rail is the orthogonal pane-collapse axis and is unchanged by the core.

**Tech Stack:** .NET 10, Avalonia 12.1, FluentAvalonia 3.0.1 (`FANavigationView` / `FAContentDialog` / `MenuFlyout`), CommunityToolkit.Mvvm, F# + FsCheck (`Lattice.App.Aggregation`), xUnit + `Avalonia.Headless.XUnit`.

**Authoritative inputs (read before starting):**
- Design cards `docs/design/m2/` — `3a` (rail), `3b` (host mgmt), `1c` (shell), `2d` (settings/add-host), `1f` (dark), `1g` (tokens), `1i` (responsive). Greyed annotations are normative.
- Resolved-decisions spec: `docs/superpowers/specs/2026-07-12-m2-shell-rework-26-decisions.md` — tier taxonomy, fit-math split, `ShowToggle` rule, compact-scope deferral, the pure-core signature, persistence keys. **When card text and this plan disagree, the card wins — flag it.**
- **Do NOT touch** `src/Lattice.Core/HostMonitor.cs` / `HostMachine` (verification-sync rule). All work is App-layer + the pure `Lattice.App.Aggregation` project.

---

## File Structure

**Create:**
- `src/Lattice.App.Aggregation/RailLayout.fs` — pure grouping/fit core (types + `RailLayoutPolicy.compute`).
- `tests/Lattice.Aggregation.Tests/RailLayoutTests.fs` — FsCheck + transition-table tests for the core.
- `src/Lattice.App/ViewModels/RailTierProjection.cs` — `RailState → RailTier` (C#, total, no wildcard).
- `src/Lattice.App/ViewModels/GroupHeaderRailItemViewModel.cs` — a status-group header rail row.
- `src/Lattice.App/Infrastructure/ThemePreference.cs` — persisted `AppTheme`, applies to `Application` (two consumers: startup + Settings, so a shared class like `DensityPreference`). Rail override/expand state has a single consumer (`ShellViewModel`) and is persisted inline there via `UiStateStore.Update`, no separate class.
- `tests/Lattice.App.Tests/RailTierProjectionTests.cs`, `GroupHeaderRailItemViewModelTests.cs`, `ThemePreferenceTests.cs`.
- `tests/Lattice.App.Tests/Headless/HostRailMenuTests.cs`, `HostRailGroupingTests.cs`, `EditHostDialogTests.cs`, `ThemeSettingTests.cs`.

**Modify:**
- `src/Lattice.App.Aggregation/Lattice.App.Aggregation.fsproj` — add `RailLayout.fs` to compile order.
- `tests/Lattice.Aggregation.Tests/Lattice.Aggregation.Tests.fsproj` — add `RailLayoutTests.fs`.
- `src/Lattice.App/Views/ShellWindow.axaml` — hosts block `PaneCustomContent` → `PaneFooter`; group-header template; header list/group toggle; host-row `MenuFlyout`.
- `src/Lattice.App/Views/ShellWindow.axaml.cs` — viewport-height wiring; MenuFlyout/dialog handlers; auth-failed → Edit dialog; remove-confirm machinery (moved from SettingsView).
- `src/Lattice.App/ViewModels/ShellViewModel.cs` — rail orchestration (viewport, override, expand, reconcile via core); host-command events; drop `Settings.ExpandHost`.
- `src/Lattice.App/ViewModels/AddHostViewModel.cs` — mode-aware (Add/Edit), Save path, Test-connection command, password-error state.
- `src/Lattice.App/Views/AddHostDialog.axaml` / `.axaml.cs` — Edit title/buttons, password danger border + focus, secondary Test button.
- `src/Lattice.App/ViewModels/SettingsViewModel.cs` — drop Hosts group; add `SelectedTheme`.
- `src/Lattice.App/Views/SettingsView.axaml` / `.axaml.cs` — remove Hosts ItemsControl + remove-confirm handler; add Theme group + pointer caption.
- `src/Lattice.App/Infrastructure/UiStateStore.cs` — `UiState` gains `RailGrouping`, `RailHealthyExpanded`, `Theme`; `JsonStringEnumConverter`.
- `src/Lattice.App/App.axaml.cs` — construct + apply `ThemePreference` at startup; pass into `ShellViewModel`.
- `src/Lattice.App/Localization/Strings.resx` — add new keys; retire Settings host-group keys.

**Delete:**
- `src/Lattice.App/ViewModels/HostSettingsItemViewModel.cs` and `tests/Lattice.App.Tests/` references to it.

---

## Phase A — Rail placement (de-risks the FA slot first)

### Task 1: Move the hosts block from `PaneCustomContent` to `PaneFooter`

**Files:**
- Modify: `src/Lattice.App/Views/ShellWindow.axaml` (hosts block currently lines ~103–173; Settings footer ~175–181)
- Test: `tests/Lattice.App.Tests/Headless/ShellRailTests.cs` (add geometry pins)

Verified up front (decisions spec §1): FA 3.0.1 `FANavigationView` has `PaneFooter`, and its template renders `PaneFooter` **above** `FooterMenuItems`. The move is a pure slot change; the `MinWidth=0` compact fix and every `#Nav.IsPaneOpen` binding stay as-is.

- [ ] **Step 1: Write the failing geometry test**

Add to `ShellRailTests` (uses the existing `MakeShell` helper + `Layout`):

```csharp
[AvaloniaFact]
public void Hosts_block_renders_above_the_settings_footer_item()
{
    var (window, shell, registry) = MakeShell();
    window.Show();
    registry.AddHost(TestData.MakeHostConfig(name: "office-pc"));
    Layout(window);

    // Design 3a: hosts dock in PaneFooter directly above Settings (FooterMenuItems).
    var hostsTop = window.HostList.TranslatePoint(new Point(0, 0), window)!.Value.Y;
    var settingsTop = window.NavSettings.TranslatePoint(new Point(0, 0), window)!.Value.Y;
    Assert.True(hostsTop < settingsTop,
        $"hosts (y={hostsTop}) must render above Settings (y={settingsTop})");
    window.Close();
}
```

- [ ] **Step 2: Run it — expect FAIL**

Run: `dotnet test tests/Lattice.App.Tests --filter "FullyQualifiedName~Hosts_block_renders_above_the_settings_footer_item"`
Expected: FAIL (today the block is in `PaneCustomContent`, which sits above the menu items, not just above Settings — the assertion may pass or the element tree differs; either way this pins the target).

- [ ] **Step 3: Move the block in XAML**

In `ShellWindow.axaml`, cut the entire `<ui:FANavigationView.PaneCustomContent> … </ui:FANavigationView.PaneCustomContent>` element (the `StackPanel` with the Hosts header + `HostList`) and re-add it verbatim as `<ui:FANavigationView.PaneFooter> … </ui:FANavigationView.PaneFooter>`, placed immediately **before** `<ui:FANavigationView.FooterMenuItems>`. Change only the two wrapper tag names; keep the inner `StackPanel Margin="4,8,4,0"`, the `DockPanel` header, and the `ListBox x:Name="HostList"` (with `Classes="hostRail"`, `ItemsSource`, `SelectedItem`, `SelectionChanged`) exactly as they are.

- [ ] **Step 4: Run the test — expect PASS**

Run: `dotnet test tests/Lattice.App.Tests --filter "FullyQualifiedName~Hosts_block_renders_above_the_settings_footer_item"`
Expected: PASS.

- [ ] **Step 5: Run the existing compact + sentinel rail pins**

Run: `dotnet test tests/Lattice.App.Tests --filter "FullyQualifiedName~ShellRailTests"`
Expected: PASS — `Collapsed_pane_keeps_the_all_hosts_sentinel_icon_only`, `First_rail_row_renders_the_all_hosts_label`, etc. still green (proves compact 48 px icons-only survives the slot move).

- [ ] **Step 6: Commit**

```bash
git add src/Lattice.App/Views/ShellWindow.axaml tests/Lattice.App.Tests/Headless/ShellRailTests.cs
git commit -m "feat(shell): dock hosts rail in PaneFooter above Settings (design 3a)"
```

---

## Phase B — Height-adaptive grouping core (born pure) + wiring

### Task 2: Pure F# core — types + `Flat` / `SingleHost` modes

**Files:**
- Create: `src/Lattice.App.Aggregation/RailLayout.fs`
- Modify: `src/Lattice.App.Aggregation/Lattice.App.Aggregation.fsproj` (add `<Compile Include="RailLayout.fs" />` — after `ViewSlice.fs`, order-independent of the others)
- Create: `tests/Lattice.Aggregation.Tests/RailLayoutTests.fs`
- Modify: `tests/Lattice.Aggregation.Tests/Lattice.Aggregation.Tests.fsproj` (add `<Compile Include="RailLayoutTests.fs" />`)

The full type set + contract is the decisions spec §6. This task lands the types and everything except grouped-row emission (Task 3 adds `Grouped`).

- [ ] **Step 1: Write failing tests for the non-grouped modes**

Create `tests/Lattice.Aggregation.Tests/RailLayoutTests.fs`:

```fsharp
module Lattice.Aggregation.Tests.RailLayoutTests

open System
open Xunit
open Lattice.App.Aggregation

let private id () = Guid.NewGuid()
let private host tier = { Id = id (); Tier = tier }

let private input hosts height =
    { Hosts = hosts
      AvailableHeight = height
      RowHeight = 40.0
      Override = Auto
      HealthyExpanded = false }

[<Fact>]
let ``single host is degenerate: no All-hosts row, no toggle`` () =
    let h = host Healthy
    let layout = RailLayoutPolicy.compute (input [| h |] 1000.0)
    Assert.Equal(SingleHost, layout.Mode)
    Assert.False layout.ShowToggle
    Assert.Equal<RailRow list>([ HostRow h.Id ], layout.Rows)

[<Fact>]
let ``flat when the list fits: All-hosts leads, hosts in registry order`` () =
    let a, b = host Healthy, host Attention
    // (2 hosts + All-hosts) * 40 = 120 <= 200 => fits
    let layout = RailLayoutPolicy.compute (input [| a; b |] 200.0)
    Assert.Equal(Flat, layout.Mode)
    Assert.False layout.ShowToggle           // fits under Auto => no toggle
    Assert.Equal<RailRow list>([ AllHostsRow; HostRow a.Id; HostRow b.Id ], layout.Rows)

[<Fact>]
let ``auto + overflow flips to grouped and shows the toggle`` () =
    let a, b = host Healthy, host Attention
    // (2 + 1) * 40 = 120 > 100 => does not fit
    let layout = RailLayoutPolicy.compute (input [| a; b |] 100.0)
    Assert.Equal(Grouped, layout.Mode)
    Assert.True layout.ShowToggle

[<Fact>]
let ``force-flat keeps flat even when it overflows, and keeps the toggle`` () =
    let a, b = host Healthy, host Attention
    let layout = RailLayoutPolicy.compute { input [| a; b |] 100.0 with Override = ForceFlat }
    Assert.Equal(Flat, layout.Mode)
    Assert.True layout.ShowToggle            // manual override => toggle stays to undo

[<Fact>]
let ``force-grouped groups even when it fits, and keeps the toggle`` () =
    let a, b = host Healthy, host Attention
    let layout = RailLayoutPolicy.compute { input [| a; b |] 1000.0 with Override = ForceGrouped }
    Assert.Equal(Grouped, layout.Mode)
    Assert.True layout.ShowToggle
```

- [ ] **Step 2: Run — expect FAIL (module not defined)**

Run: `dotnet test tests/Lattice.Aggregation.Tests --filter "FullyQualifiedName~RailLayoutTests"`
Expected: FAIL to build — `RailLayout` not found.

- [ ] **Step 3: Implement `RailLayout.fs` (types + fit + mode resolve; grouped rows come in Task 3)**

Create `src/Lattice.App.Aggregation/RailLayout.fs`:

```fsharp
namespace Lattice.App.Aggregation

open System

/// Status-group tier for the many-hosts rail. Two tiers only (owner decision,
/// decisions spec §2); M3 reopens this when a terminal paused/disabled state exists.
type RailTier =
    | Attention
    | Healthy

/// User's persisted list/group override; Auto lets the height fit test decide.
type RailOverride =
    | Auto
    | ForceFlat
    | ForceGrouped

/// Effective mode after fit test + override resolve.
type RailMode =
    | SingleHost   // exactly one host: no "All hosts", scope pinned to it
    | Flat         // "All hosts" + individual host rows
    | Grouped      // status groups

/// One host projected by the shell from HostStore (registry order preserved).
type RailHost = { Id: Guid; Tier: RailTier }

/// An ordered rail row the shell reconciles into a view-model.
type RailRow =
    | AllHostsRow
    | HostRow of Guid
    | GroupHeaderRow of tier: RailTier * count: int * expanded: bool

/// What the shell measures / persists and hands to the pure core.
type RailLayoutInput =
    { Hosts: RailHost[]
      AvailableHeight: float
      RowHeight: float
      Override: RailOverride
      HealthyExpanded: bool }

/// The layout the shell renders.
type RailLayout =
    { Mode: RailMode
      ShowToggle: bool
      Rows: RailRow list }

module RailLayoutPolicy =

    /// Flat list fits iff (host count + the All-hosts row) rows clear the budget.
    let private fits (input: RailLayoutInput) =
        float (input.Hosts.Length + 1) * input.RowHeight <= input.AvailableHeight

    /// Registry-order host rows for the flat list.
    let private flatRows (hosts: RailHost[]) =
        AllHostsRow :: [ for h in hosts -> HostRow h.Id ]

    let compute (input: RailLayoutInput) : RailLayout =
        match input.Hosts.Length with
        | 0 ->
            { Mode = Flat; ShowToggle = false; Rows = [ AllHostsRow ] }
        | 1 ->
            { Mode = SingleHost; ShowToggle = false; Rows = [ HostRow input.Hosts.[0].Id ] }
        | _ ->
            let doesFit = fits input
            let mode =
                match input.Override with
                | ForceFlat -> Flat
                | ForceGrouped -> Grouped
                | Auto -> if doesFit then Flat else Grouped
            let showToggle = not doesFit || input.Override <> Auto
            let rows =
                match mode with
                | Flat | SingleHost -> flatRows input.Hosts
                | Grouped -> flatRows input.Hosts   // TODO(Task 3): replace with grouped rows
            { Mode = mode; ShowToggle = showToggle; Rows = rows }
```

> Note: the `Grouped -> flatRows` line is a **deliberate placeholder for Task 3 only** — Task 3's failing test replaces it. It is never committed as the final state: Task 3 is in the same phase and its red test (Step 1 below) fails against this stand-in.

- [ ] **Step 4: Wire the project files**

In `src/Lattice.App.Aggregation/Lattice.App.Aggregation.fsproj`, add inside the existing `<ItemGroup>` (after `ViewSlice.fs`):
```xml
    <Compile Include="RailLayout.fs" />
```
In `tests/Lattice.Aggregation.Tests/Lattice.Aggregation.Tests.fsproj`, add (after `ViewSliceTests.fs`):
```xml
    <Compile Include="RailLayoutTests.fs" />
```

- [ ] **Step 5: Run — expect PASS**

Run: `dotnet test tests/Lattice.Aggregation.Tests --filter "FullyQualifiedName~RailLayoutTests"`
Expected: PASS (all five).

- [ ] **Step 6: Commit**

```bash
git add src/Lattice.App.Aggregation/RailLayout.fs src/Lattice.App.Aggregation/Lattice.App.Aggregation.fsproj \
        tests/Lattice.Aggregation.Tests/RailLayoutTests.fs tests/Lattice.Aggregation.Tests/Lattice.Aggregation.Tests.fsproj
git commit -m "feat(rail): pure RailLayout core — flat/single-host fit + override (design 3a)"
```

---

### Task 3: Pure F# core — `Grouped` rows (tiers, expand, order) + property tests

**Files:**
- Modify: `src/Lattice.App.Aggregation/RailLayout.fs`
- Modify: `tests/Lattice.Aggregation.Tests/RailLayoutTests.fs`

Contract (decisions spec §6): `Grouped` ⇒ `AllHostsRow` then, in fixed order Attention → Healthy, for each **non-empty** tier a `GroupHeaderRow(tier, count, expanded)` followed by that tier's `HostRow`s **iff expanded**. Attention is always expanded; Healthy honors the persisted flag. Within a tier, registry order is preserved.

- [ ] **Step 1: Write failing grouped-emission tests + a property**

Append to `RailLayoutTests.fs`:

```fsharp
open FsCheck
open FsCheck.Xunit

let private grouped hosts healthyExp =
    RailLayoutPolicy.compute
        { input hosts 0.0 with   // height 0 => never fits => Grouped under Auto
            Override = Auto
            HealthyExpanded = healthyExp }

[<Fact>]
let ``grouped: attention always expands; healthy collapsed hides its hosts`` () =
    let att = host Attention
    let heal = host Healthy
    let layout = grouped [| att; heal |] false
    Assert.Equal<RailRow list>(
        [ AllHostsRow
          GroupHeaderRow(Attention, 1, true); HostRow att.Id
          GroupHeaderRow(Healthy, 1, false) ],
        layout.Rows)

[<Fact>]
let ``grouped: expanding healthy reveals its hosts in registry order`` () =
    let h1, h2 = host Healthy, host Healthy
    let layout = grouped [| h1; h2 |] true
    Assert.Equal<RailRow list>(
        [ AllHostsRow
          GroupHeaderRow(Healthy, 2, true); HostRow h1.Id; HostRow h2.Id ],
        layout.Rows)

[<Fact>]
let ``grouped: an empty tier is skipped`` () =
    let a, b = host Attention, host Attention
    let layout = grouped [| a; b |] false
    Assert.Equal<RailRow list>(
        [ AllHostsRow; GroupHeaderRow(Attention, 2, true); HostRow a.Id; HostRow b.Id ],
        layout.Rows)

// --- generators for the property pass ---
let private tierGen = Gen.elements [ Attention; Healthy ]
let private railInputGen =
    gen {
        let! n = Gen.choose (0, 8)
        let! tiers = Gen.listOfLength n tierGen
        let hosts = [| for t in tiers -> { Id = Guid.NewGuid(); Tier = t } |]
        let! height = Gen.choose (0, 600) |> Gen.map float
        let! ov = Gen.elements [ Auto; ForceFlat; ForceGrouped ]
        let! he = Arb.generate<bool>
        return { Hosts = hosts; AvailableHeight = height; RowHeight = 40.0
                 Override = ov; HealthyExpanded = he }
    }
type RailArbs =
    static member Input() = Arb.fromGen railInputGen

let private hostIdsIn rows =
    rows |> List.choose (function HostRow id -> Some id | _ -> None)

[<Property(Arbitrary = [| typeof<RailArbs> |])>]
let ``every emitted HostRow id is a real input host`` (inp: RailLayoutInput) =
    let known = inp.Hosts |> Array.map (fun h -> h.Id) |> Set.ofArray
    (RailLayoutPolicy.compute inp).Rows |> hostIdsIn |> List.forall known.Contains

[<Property(Arbitrary = [| typeof<RailArbs> |])>]
let ``flat and expanded-grouped conserve hosts exactly once`` (inp: RailLayoutInput) =
    let layout = RailLayoutPolicy.compute inp
    // Force Healthy expanded so grouped emits every host, then compare as a set.
    let expanded = RailLayoutPolicy.compute { inp with HealthyExpanded = true }
    match layout.Mode with
    | SingleHost -> true   // degenerate single-host handled by its own unit test
    | Flat ->
        (hostIdsIn layout.Rows |> Set.ofList) = (inp.Hosts |> Array.map (fun h -> h.Id) |> Set.ofArray)
    | Grouped ->
        (hostIdsIn expanded.Rows |> Set.ofList) = (inp.Hosts |> Array.map (fun h -> h.Id) |> Set.ofArray)

[<Property(Arbitrary = [| typeof<RailArbs> |])>]
let ``grouped attention rows always follow their header (always expanded)`` (inp: RailLayoutInput) =
    let layout = RailLayoutPolicy.compute { inp with Override = ForceGrouped }
    let attentionCount = inp.Hosts |> Array.filter (fun h -> h.Tier = Attention) |> Array.length
    if attentionCount = 0 then
        layout.Rows |> List.forall (function GroupHeaderRow(Attention, _, _) -> false | _ -> true)
    else
        layout.Rows |> List.exists (function GroupHeaderRow(Attention, c, ex) -> c = attentionCount && ex | _ -> false)
```

- [ ] **Step 2: Run — expect FAIL**

Run: `dotnet test tests/Lattice.Aggregation.Tests --filter "FullyQualifiedName~RailLayoutTests"`
Expected: FAIL — the placeholder `Grouped -> flatRows` emits flat rows, so the grouped-emission asserts fail.

- [ ] **Step 3: Implement grouped emission**

In `RailLayout.fs`, replace the placeholder line and add the helpers. Add above `compute`:

```fsharp
    /// Fixed render order of the status groups.
    let private tierOrder = [ Attention; Healthy ]

    /// Attention is always expanded; Healthy honors the persisted flag.
    let private tierExpanded (input: RailLayoutInput) tier =
        match tier with
        | Attention -> true
        | Healthy -> input.HealthyExpanded

    /// Rows for one non-empty tier: header, then its hosts iff expanded.
    let private groupRows (input: RailLayoutInput) tier (members: RailHost[]) =
        let expanded = tierExpanded input tier
        let header = GroupHeaderRow(tier, members.Length, expanded)
        if expanded then header :: [ for h in members -> HostRow h.Id ] else [ header ]

    let private groupedRows (input: RailLayoutInput) =
        AllHostsRow
        :: [ for tier in tierOrder do
                let members = input.Hosts |> Array.filter (fun h -> h.Tier = tier)
                if members.Length > 0 then yield! groupRows input tier members ]
```

Then change the `Grouped` arm in `compute` from `flatRows input.Hosts` to `groupedRows input`:

```fsharp
                | Grouped -> groupedRows input
```

- [ ] **Step 4: Run — expect PASS**

Run: `dotnet test tests/Lattice.Aggregation.Tests --filter "FullyQualifiedName~RailLayoutTests"`
Expected: PASS (unit + all three properties).

- [ ] **Step 5: Falsification pass (mutation)**

Temporarily change `tierExpanded`'s `Attention -> true` to `Attention -> false`; re-run — the always-expanded property must go red. Revert.

- [ ] **Step 6: Commit**

```bash
git add src/Lattice.App.Aggregation/RailLayout.fs tests/Lattice.Aggregation.Tests/RailLayoutTests.fs
git commit -m "feat(rail): grouped rows (Attention/Healthy) in RailLayout core (design 3a)"
```

---

### Task 4: `RailTierProjection` — `RailState → RailTier` (total, no wildcard)

**Files:**
- Create: `src/Lattice.App/ViewModels/RailTierProjection.cs`
- Create: `tests/Lattice.App.Tests/RailTierProjectionTests.cs`

Taxonomy (decisions spec §2, owner-simplified to two tiers): Attention ← Unreachable/AuthFailed/Retrying; Healthy ← Connected/Connecting. No Offline tier in M2.

- [ ] **Step 1: Write the failing test**

```csharp
using Lattice.App.Aggregation;
using Lattice.App.ViewModels;
using Xunit;

namespace Lattice.App.Tests;

public class RailTierProjectionTests
{
    [Theory]
    [InlineData(RailState.Unreachable)]
    [InlineData(RailState.AuthFailed)]
    [InlineData(RailState.Retrying)]
    public void Problem_states_are_attention(RailState state) =>
        Assert.Equal(RailTier.Attention, RailTierProjection.From(state));

    [Theory]
    [InlineData(RailState.Connected)]
    [InlineData(RailState.Connecting)]
    public void Live_states_are_healthy(RailState state) =>
        Assert.Equal(RailTier.Healthy, RailTierProjection.From(state));
}
```

- [ ] **Step 2: Run — expect FAIL** (`dotnet test tests/Lattice.App.Tests --filter "FullyQualifiedName~RailTierProjectionTests"`).

- [ ] **Step 3: Implement**

```csharp
using Lattice.App.Aggregation;

namespace Lattice.App.ViewModels;

/// <summary>
/// Buckets the five rail visuals into the two many-hosts status groups (design 3a;
/// decisions spec §2 — owner-simplified to Attention + Healthy). Total, no wildcard:
/// adding a RailState case must force a choice here.
/// </summary>
public static class RailTierProjection
{
    public static RailTier From(RailState state) => state switch
    {
        RailState.Unreachable or RailState.AuthFailed or RailState.Retrying => RailTier.Attention,
        RailState.Connected or RailState.Connecting => RailTier.Healthy,
    };
}
```

> `RailTier` is an F# DU; from C# its cases are `RailTier.Attention` etc. (static properties). The `switch` has no `_` arm, so a new `RailState` is a compile error (CS8509 is treated as a warning-as-error in this repo) — the intended guard.

- [ ] **Step 4: Run — expect PASS.** Then `dotnet build src/Lattice.App` to confirm the exhaustiveness guard compiles clean.

- [ ] **Step 5: Commit**

```bash
git add src/Lattice.App/ViewModels/RailTierProjection.cs tests/Lattice.App.Tests/RailTierProjectionTests.cs
git commit -m "feat(rail): RailState -> RailTier projection (design 3a taxonomy)"
```

---

### Task 5: Persist rail override + group expand + theme in `UiState`

**Files:**
- Modify: `src/Lattice.App/Infrastructure/UiStateStore.cs`
- Modify: `tests/Lattice.App.Tests/UiStateStoreTests.cs`

Add C# enums + four `UiState` fields (decisions spec §7). Enums persist as strings.

- [ ] **Step 1: Write the failing round-trip test**

Append to `UiStateStoreTests.cs`:

```csharp
[Fact]
public void Rail_and_theme_preferences_round_trip()
{
    var path = Path.Combine(Path.GetTempPath(), $"lattice-ui-{Guid.NewGuid():N}.json");
    var store = new UiStateStore(path);
    store.Save(UiState.Default with
    {
        RailGrouping = RailGroupingMode.Grouped,
        RailHealthyExpanded = true,
        Theme = AppTheme.Dark,
    });

    var loaded = new UiStateStore(path).Load();
    Assert.Equal(RailGroupingMode.Grouped, loaded.RailGrouping);
    Assert.True(loaded.RailHealthyExpanded);
    Assert.Equal(AppTheme.Dark, loaded.Theme);
    File.Delete(path);
}

[Fact]
public void Legacy_state_file_without_new_fields_loads_defaults()
{
    var path = Path.Combine(Path.GetTempPath(), $"lattice-ui-{Guid.NewGuid():N}.json");
    // A pre-rework file: only the original three members present.
    File.WriteAllText(path, """{"compactDensity":true,"columnVisibility":{},"columnWidths":{}}""");
    var loaded = new UiStateStore(path).Load();
    Assert.True(loaded.CompactDensity);
    Assert.Equal(RailGroupingMode.Auto, loaded.RailGrouping);
    Assert.Equal(AppTheme.System, loaded.Theme);
    File.Delete(path);
}
```

- [ ] **Step 2: Run — expect FAIL** (`dotnet test tests/Lattice.App.Tests --filter "FullyQualifiedName~UiStateStoreTests"`).

- [ ] **Step 3: Implement**

In `UiStateStore.cs`, add the string-enum converter to `JsonOptions`:
```csharp
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
```

Add the enums (top of file, under the namespace):
```csharp
/// <summary>Persisted rail list/group override (maps to F# RailOverride in the shell).</summary>
public enum RailGroupingMode { Auto, Flat, Grouped }

/// <summary>Persisted app theme (design 2d/1f). System follows the OS.</summary>
public enum AppTheme { Light, Dark, System }
```

Extend the record — **append new positional params with defaults** so legacy JSON deserializes (STJ uses the default for a missing member):
```csharp
public sealed record UiState(
    bool CompactDensity,
    Dictionary<string, bool> ColumnVisibility,
    Dictionary<string, double> ColumnWidths,
    RailGroupingMode RailGrouping = RailGroupingMode.Auto,
    bool RailHealthyExpanded = false,
    AppTheme Theme = AppTheme.System)
{
    public static UiState Default => new(false, [], []);
}
```

> `Default` still passes only the first three args; the new params fall to their defaults. The `Load` normalizer (`ColumnVisibility ?? new()`) is unchanged.

- [ ] **Step 4: Run — expect PASS** (both new tests + existing `UiStateStoreTests`).

- [ ] **Step 5: Commit**

```bash
git add src/Lattice.App/Infrastructure/UiStateStore.cs tests/Lattice.App.Tests/UiStateStoreTests.cs
git commit -m "feat(state): persist rail grouping/expand + theme in UiState"
```

---

### Task 6: `GroupHeaderRailItemViewModel` — a status-group header row

**Files:**
- Create: `src/Lattice.App/ViewModels/GroupHeaderRailItemViewModel.cs`
- Create: `tests/Lattice.App.Tests/GroupHeaderRailItemViewModelTests.cs`

A header row shows "Attention · 3" / "Healthy · 35" (localized), carries its `RailTier`, whether it is collapsible (Attention is not), and an expand toggle command that raises an event the shell handles (the shell owns persistence + recompute). Header rows are **not** selectable scope — the ListBox must skip selection on them (handled in Task 8's `OnHostSelectionChanged`).

- [ ] **Step 1: Write the failing test**

```csharp
using Lattice.App.Aggregation;
using Lattice.App.Localization;
using Lattice.App.ViewModels;
using Xunit;

namespace Lattice.App.Tests;

public class GroupHeaderRailItemViewModelTests
{
    [Fact]
    public void Attention_header_formats_count_and_is_not_collapsible()
    {
        var vm = new GroupHeaderRailItemViewModel(RailTier.Attention, count: 3, expanded: true);
        Assert.Equal(string.Format(Strings.RailGroupAttentionFmt, 3), vm.Text);
        Assert.False(vm.IsCollapsible);   // Attention is always expanded
    }

    [Fact]
    public void Healthy_header_is_collapsible_and_raises_toggle_with_its_tier()
    {
        var vm = new GroupHeaderRailItemViewModel(RailTier.Healthy, count: 35, expanded: false);
        Assert.Equal(string.Format(Strings.RailGroupHealthyFmt, 35), vm.Text);
        Assert.True(vm.IsCollapsible);
        Assert.False(vm.Expanded);

        RailTier? toggled = null;
        vm.ToggleRequested += (_, tier) => toggled = tier;
        vm.ToggleCommand.Execute(null);
        Assert.Equal(RailTier.Healthy, toggled);
    }
}
```

- [ ] **Step 2: Run — expect FAIL** (`dotnet test tests/Lattice.App.Tests --filter "FullyQualifiedName~GroupHeaderRailItemViewModelTests"`; also fails to compile until the `RailGroup*Fmt` strings exist — add them now in this task's Step 3, and the final ResX pass Task 15 consolidates).

- [ ] **Step 3: Add strings + implement**

Add to `src/Lattice.App/Localization/Strings.resx` (T1 convention — meaning-based names, `{0}` count):
```xml
  <data name="RailGroupAttentionFmt" xml:space="preserve"><value>Attention · {0}</value></data>
  <data name="RailGroupHealthyFmt" xml:space="preserve"><value>Healthy · {0}</value></data>
```

Create `GroupHeaderRailItemViewModel.cs`:
```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lattice.App.Aggregation;
using Lattice.App.Localization;

namespace Lattice.App.ViewModels;

/// <summary>A status-group header row in the hosts rail (design 3a). Attention is
/// always expanded (not collapsible); Healthy toggles, the shell persists.</summary>
public sealed partial class GroupHeaderRailItemViewModel : ObservableObject
{
    public GroupHeaderRailItemViewModel(RailTier tier, int count, bool expanded)
    {
        Tier = tier;
        _expanded = expanded;
        // Two tiers (decisions spec §2): Attention or Healthy.
        Text = tier.Equals(RailTier.Attention)
            ? string.Format(Strings.RailGroupAttentionFmt, count)
            : string.Format(Strings.RailGroupHealthyFmt, count);
    }

    public RailTier Tier { get; }
    public string Text { get; }

    /// <summary>Attention is pinned open (decisions spec §2); only the others show a chevron.</summary>
    public bool IsCollapsible => !Tier.Equals(RailTier.Attention);

    [ObservableProperty] private bool _expanded;

    /// <summary>Raised by <see cref="ToggleCommand"/>; the shell flips + persists + recomputes.</summary>
    public event EventHandler<RailTier>? ToggleRequested;

    [RelayCommand]
    private void Toggle()
    {
        if (IsCollapsible)
            ToggleRequested?.Invoke(this, Tier);
    }
}
```

> `RailTier` is an F# DU; C# equality uses `.Equals` / `==` against the static case properties. The `switch` uses `when` guards (not patterns) because F# DU cases are not C# constant patterns.

- [ ] **Step 4: Run — expect PASS.**

- [ ] **Step 5: Commit**

```bash
git add src/Lattice.App/ViewModels/GroupHeaderRailItemViewModel.cs \
        tests/Lattice.App.Tests/GroupHeaderRailItemViewModelTests.cs src/Lattice.App/Localization/Strings.resx
git commit -m "feat(rail): group-header rail row view-model (design 3a)"
```

---

### Task 7: `ShellViewModel` rail orchestration — measure, persist, reconcile via the core

**Files:**
- Modify: `src/Lattice.App/ViewModels/ShellViewModel.cs`
- Modify: `tests/Lattice.App.Tests/ShellViewModelTests.cs`

Replaces the layout-agnostic `ReconcileHosts` host-VM churn with: a persistent host-VM map keyed by id (owns clock subscriptions), plus a `RebuildRail()` that materializes `RailLayoutPolicy.compute(...)` output into `RailEntries`. New inputs: viewport height (fed by the view), override + expand state (loaded/persisted via `UiStateStore`). `Scope` is preserved across rebuilds by capturing the scoped host id and suppressing scope side effects while the collection is rebuilt.

Constants: `RailRowHeight = 40.0` (= `LatticeHostItemHeight`), `ReservedRailChrome = 150.0` (Hosts header + Settings item + paddings; the exact value is pinned by the headless geometry test in Task 8 — tune it there, keep it here).

- [ ] **Step 1: Write failing VM-level tests**

Append to `ShellViewModelTests.cs`:

```csharp
private void AddHosts(int n)
{
    for (var i = 0; i < n; i++)
        _registry.AddHost(TestData.MakeHostConfig(name: $"h{i}"));
}

[Fact]
public void Small_viewport_with_many_hosts_groups_the_rail()
{
    AddHosts(4);
    // Budget below (4 + 1) * 40 = 200 forces grouped under Auto.
    _shell.SetRailViewportHeight(300.0 - 150.0 + 30.0); // available ~180 < 200
    Assert.Contains(_shell.RailEntries, e => e is GroupHeaderRailItemViewModel);
    Assert.True(_shell.ShowRailToggle);
}

[Fact]
public void Tall_viewport_keeps_the_rail_flat_and_hides_the_toggle()
{
    AddHosts(4);
    _shell.SetRailViewportHeight(1000.0);
    Assert.DoesNotContain(_shell.RailEntries, e => e is GroupHeaderRailItemViewModel);
    Assert.False(_shell.ShowRailToggle);
}

[Fact]
public void Toggling_grouping_forces_the_opposite_layout_and_persists()
{
    AddHosts(4);
    _shell.SetRailViewportHeight(1000.0);              // fits => Flat
    _shell.ToggleRailGroupingCommand.Execute(null);    // force grouped
    Assert.Contains(_shell.RailEntries, e => e is GroupHeaderRailItemViewModel);
    // Persisted: a fresh shell on the same ui-state file restores grouped.
    var shell2 = new ShellViewModel(_registry, _store, _clock, new UiStateStore(_uiPath),
        () => new RoutingGuiRpcClient(_fakes));
    shell2.SetRailViewportHeight(1000.0);
    Assert.Contains(shell2.RailEntries, e => e is GroupHeaderRailItemViewModel);
    shell2.Dispose();
}

[Fact]
public void Selecting_a_host_survives_a_rail_rebuild()
{
    AddHosts(3);
    _shell.SetRailViewportHeight(1000.0);
    var hostVm = _shell.RailEntries.OfType<HostRailItemViewModel>().First();
    _shell.SelectedRailEntry = hostVm;
    Assert.Equal(hostVm.HostId, _shell.Scope.HostId);

    _shell.SetRailViewportHeight(1001.0);              // triggers a rebuild
    Assert.Equal(hostVm.HostId, _shell.Scope.HostId);  // scope preserved
}
```

- [ ] **Step 2: Run — expect FAIL** (`dotnet test tests/Lattice.App.Tests --filter "FullyQualifiedName~ShellViewModelTests"`).

- [ ] **Step 3: Rework `ShellViewModel`**

Add usings: `using Lattice.App.Aggregation;`. Add fields near the top of the class:
```csharp
    private const double RailRowHeight = 40.0;      // LatticeHostItemHeight
    private const double ReservedRailChrome = 150.0; // header + Settings + paddings (pinned by Task 8)
    private readonly UiStateStore _uiState;
    private readonly Dictionary<Guid, HostRailItemViewModel> _hostRowVms = [];
    private double _railViewportHeight;
    private RailGroupingMode _grouping;
    private bool _healthyExpanded;
    private bool _rebuilding;

    [ObservableProperty] private bool _showRailToggle;
```

In the constructor, capture `uiState` and load persisted rail state (before `ReconcileHosts`):
```csharp
        _uiState = uiState;
        var ui = uiState.Load();
        _grouping = ui.RailGrouping;
        _healthyExpanded = ui.RailHealthyExpanded;
```
Keep `RailEntries.Add(_allHosts); SelectedRailEntry = _allHosts;` then `store.Changed += OnStoreChanged; ReconcileHosts();` as today.

Replace the body of `ReconcileHosts` (host-VM management stays; row layout moves to `RebuildRail`):
```csharp
    private void ReconcileHosts()
    {
        // Keep the host-VM map in sync with the registry (append-only order).
        var seen = new HashSet<Guid>();
        for (var i = 0; i < _store.Hosts.Count; i++)
        {
            HostEntry entry = _store.Hosts[i];
            seen.Add(entry.Config.Id);
            if (!_hostRowVms.TryGetValue(entry.Config.Id, out HostRailItemViewModel? vm))
                _hostRowVms[entry.Config.Id] = new HostRailItemViewModel(entry, _clock);
            else
                vm.Refresh();
        }
        foreach (Guid gone in _hostRowVms.Keys.Where(k => !seen.Contains(k)).ToArray())
        {
            _hostRowVms[gone].Dispose();
            _hostRowVms.Remove(gone);
        }
        var connected = _store.Hosts.Count(h => RailStateProjection.From(h.Status) == RailState.Connected);
        _allHosts.Update(connected, _store.Hosts.Count);
        HasHosts = _store.Hosts.Count > 0;
        RebuildRail();
    }
```

Add the orchestration members:
```csharp
    /// <summary>The view feeds the measured footer height; the core re-evaluates the
    /// flat↔grouped fit boundary (design 3a: "window resize re-evaluates").</summary>
    public void SetRailViewportHeight(double availableHeight)
    {
        if (Math.Abs(availableHeight - _railViewportHeight) < 0.5) return;
        _railViewportHeight = availableHeight;
        RebuildRail();
    }

    [RelayCommand]
    private void ToggleRailGrouping()
    {
        // Toggle forces the opposite of the current effective layout, persisted.
        bool grouped = RailEntries.OfType<GroupHeaderRailItemViewModel>().Any();
        _grouping = grouped ? RailGroupingMode.Flat : RailGroupingMode.Grouped;
        _uiState.Update(s => s with { RailGrouping = _grouping });
        RebuildRail();
    }

    private void OnGroupToggleRequested(object? sender, RailTier tier)
    {
        // Healthy is the only collapsible tier (Attention is pinned open).
        if (tier.Equals(RailTier.Healthy))
        {
            _healthyExpanded = !_healthyExpanded;
            _uiState.Update(s => s with { RailHealthyExpanded = _healthyExpanded });
        }
        RebuildRail();
    }

    private static RailOverride MapOverride(RailGroupingMode mode) =>
        mode switch
        {
            RailGroupingMode.Flat => RailOverride.ForceFlat,
            RailGroupingMode.Grouped => RailOverride.ForceGrouped,
            _ => RailOverride.Auto,
        };

    private void RebuildRail()
    {
        Guid? scopedHostId = SelectedRailEntry is HostRailItemViewModel h ? h.HostId : null;

        var hosts = _store.Hosts
            .Select(e => new RailHost(e.Config.Id,
                RailTierProjection.From(RailStateProjection.From(e.Status))))
            .ToArray();
        var available = Math.Max(0.0, _railViewportHeight - ReservedRailChrome);
        var input = new RailLayoutInput(hosts, available, RailRowHeight,
            MapOverride(_grouping), _healthyExpanded);
        RailLayout layout = RailLayoutPolicy.compute(input);
        ShowRailToggle = layout.ShowToggle;

        _rebuilding = true;
        try
        {
            foreach (var g in RailEntries.OfType<GroupHeaderRailItemViewModel>())
                g.ToggleRequested -= OnGroupToggleRequested;
            RailEntries.Clear();
            foreach (RailRow row in layout.Rows)
                RailEntries.Add(MaterializeRow(row));

            SelectedRailEntry = scopedHostId is { } id && _hostRowVms.TryGetValue(id, out var vm)
                ? vm : _allHosts;
        }
        finally { _rebuilding = false; }

        // Apply scope once from the restored selection (side effects were suppressed).
        Scope = SelectedRailEntry is HostRailItemViewModel sh
            ? new ScopeSelection(sh.HostId) : ScopeSelection.AllHosts;
    }

    private object MaterializeRow(RailRow row)
    {
        if (row.IsAllHostsRow) return _allHosts;
        if (row is RailRow.HostRow hr) return _hostRowVms[hr.Item];
        var gh = (RailRow.GroupHeaderRow)row;
        var vm = new GroupHeaderRailItemViewModel(gh.tier, gh.count, gh.expanded);
        vm.ToggleRequested += OnGroupToggleRequested;
        return vm;
    }
```

Update `OnSelectedRailEntryChanged` to suppress side effects during a rebuild:
```csharp
    partial void OnSelectedRailEntryChanged(object? value)
    {
        if (_rebuilding) return;   // RebuildRail applies Scope itself, once
        Scope = value is HostRailItemViewModel h ? new ScopeSelection(h.HostId) : ScopeSelection.AllHosts;
    }
```

Update `Dispose` to dispose host VMs from the map (they no longer all live in `RailEntries`) and unsubscribe headers:
```csharp
        foreach (var g in RailEntries.OfType<GroupHeaderRailItemViewModel>())
            g.ToggleRequested -= OnGroupToggleRequested;
        foreach (HostRailItemViewModel item in _hostRowVms.Values)
            item.Dispose();
```
(Remove the old `RailEntries.OfType<HostRailItemViewModel>()` disposal loop.) Also **delete** the `Settings.Reconcile();` call at the end of `ReconcileHosts` — Settings no longer tracks hosts (Task 14). Leave a note; Task 14 removes `SettingsViewModel.Reconcile`.

> F#↔C# interop notes for reviewers: `RailHost`/`RailLayoutInput` are F# records → positional constructors in declared field order. `RailOverride`/`RailTier` DU cases are static properties (compare with `.Equals`). `RailRow` DU cases expose `IsAllHostsRow`, nested types `RailRow.HostRow` (`.Item`) and `RailRow.GroupHeaderRow` (`.tier`/`.count`/`.expanded`).

- [ ] **Step 4: Run — expect PASS** (new tests + the existing `ShellViewModelTests` — `First_run_flag_follows_host_count`, scope pins, etc.). If `Settings.Reconcile` removal breaks compile, land Task 14's `SettingsViewModel` change first or stub `Reconcile()` as a no-op until Task 14.

- [ ] **Step 5: Commit**

```bash
git add src/Lattice.App/ViewModels/ShellViewModel.cs tests/Lattice.App.Tests/ShellViewModelTests.cs
git commit -m "feat(rail): height-adaptive rail orchestration via RailLayoutPolicy (design 3a)"
```

---

### Task 8: `ShellWindow` view — group-header template, toggle, viewport wiring

**Files:**
- Modify: `src/Lattice.App/Views/ShellWindow.axaml`
- Modify: `src/Lattice.App/Views/ShellWindow.axaml.cs`
- Create: `tests/Lattice.App.Tests/Headless/HostRailGroupingTests.cs`

Adds the third rail `DataTemplate` (group header), the list/group toggle in the Hosts header (visible only while `ShowRailToggle`), the `Nav.Bounds.Height → SetRailViewportHeight` feed, and makes header rows non-selectable (clicking a header toggles its group, never changes scope).

- [ ] **Step 1: Write failing headless tests**

Create `HostRailGroupingTests.cs` (mirror `ShellRailTests.MakeShell`; add 6 hosts into a short window so the flat list overflows):

```csharp
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Lattice.App.Infrastructure;
using Lattice.App.Tests.Fakes;
using Lattice.App.ViewModels;
using Lattice.App.Views;
using Lattice.Core;
using Lattice.Tests;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using static Lattice.Tests.HeadlessLayout;

namespace Lattice.App.Tests.Headless;

public class HostRailGroupingTests
{
    private static (ShellWindow Window, ShellViewModel Shell, HostRegistry Registry) MakeShell(double height)
    {
        var path = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}.json");
        var registry = new HostRegistry(new LatticeConfig(5, []), path);
        var manager = new HostMonitorManager(registry, () => new FakeGuiRpcClient(), new FakeTimeProvider());
        var store = new HostStore(registry, manager, new ImmediateUiDispatcher());
        var uiState = new UiStateStore(Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}-ui.json"));
        var shell = new ShellViewModel(registry, store, new ManualUiClock(), uiState, () => new FakeGuiRpcClient());
        var window = new ShellWindow { DataContext = shell, Height = height, Width = 1280 };
        return (window, shell, registry);
    }

    [AvaloniaFact]
    public void Overflowing_rail_shows_group_headers_and_the_toggle()
    {
        var (window, shell, registry) = MakeShell(height: 700);
        window.Show();
        for (var i = 0; i < 12; i++) registry.AddHost(TestData.MakeHostConfig(name: $"h{i}"));
        Layout(window);

        // 12 hosts + All-hosts * 40 = 520 > footer budget on a 700-high window => grouped.
        Assert.Contains(shell.RailEntries, e => e is GroupHeaderRailItemViewModel);
        var toggle = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "RailGroupToggle");
        Assert.True(toggle.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void Selecting_a_group_header_does_not_change_scope()
    {
        var (window, shell, registry) = MakeShell(height: 700);
        window.Show();
        for (var i = 0; i < 12; i++) registry.AddHost(TestData.MakeHostConfig(name: $"h{i}"));
        Layout(window);
        Assert.True(shell.Scope.IsAllHosts);

        var headerIndex = shell.RailEntries.ToList().FindIndex(e => e is GroupHeaderRailItemViewModel);
        window.HostList.SelectedIndex = headerIndex;
        Layout(window);

        // Header click toggles its group / is rejected — scope stays All hosts and the
        // header is not left selected.
        Assert.True(shell.Scope.IsAllHosts);
        Assert.IsNotType<GroupHeaderRailItemViewModel>(window.HostList.SelectedItem);
        window.Close();
    }
}
```

> `ScopeSelection.IsAllHosts` is the existing predicate (verify its exact name against `ScopeSelection.cs`; if it is `HostId is null`, assert `shell.Scope.HostId is null` instead).

- [ ] **Step 2: Run — expect FAIL** (`dotnet test tests/Lattice.App.Tests --filter "FullyQualifiedName~HostRailGroupingTests"` — no `RailGroupToggle`, no header template, headers still selectable).

- [ ] **Step 3: Add the group-header template + toggle in `ShellWindow.axaml`**

Add a third `DataTemplate` inside `HostList`'s `ListBox.DataTemplates` (after the `HostRailItemViewModel` one):
```xml
              <DataTemplate x:DataType="vm:GroupHeaderRailItemViewModel">
                <DockPanel Height="{StaticResource LatticeHostItemHeight}" Background="Transparent">
                  <PathIcon DockPanel.Dock="Left" Width="12" Height="12" Margin="6,0,0,0"
                            VerticalAlignment="Center"
                            IsVisible="{Binding IsCollapsible}"
                            Data="{StaticResource IconChevronRightRegular}"
                            Foreground="{DynamicResource LatticeTextSecondaryBrush}">
                    <PathIcon.RenderTransform>
                      <RotateTransform Angle="{Binding Expanded, Converter={x:Static v:TaskGridConverters.ChevronAngle}}" />
                    </PathIcon.RenderTransform>
                  </PathIcon>
                  <TextBlock Text="{Binding Text}" FontSize="11" FontWeight="SemiBold"
                             VerticalAlignment="Center" Margin="6,0,0,0"
                             IsVisible="{Binding #Nav.IsPaneOpen}"
                             Foreground="{DynamicResource LatticeTextSecondaryBrush}" />
                </DockPanel>
              </DataTemplate>
```
In the Hosts header `DockPanel` (currently the `Button` + `HostsHeader` TextBlock), add the toggle button docked right, left of the "+" button:
```xml
            <Button DockPanel.Dock="Right" x:Name="RailGroupToggle" Padding="4" Background="Transparent"
                    Command="{Binding ToggleRailGroupingCommand}"
                    IsVisible="{Binding ShowRailToggle}"
                    ToolTip.Tip="{x:Static loc:Strings.RailGroupToggleTooltip}">
              <PathIcon Data="{StaticResource IconGroupListRegular}" Width="12" Height="12" />
            </Button>
```

> Icons `IconChevronRightRegular` and `IconGroupListRegular` must exist in `Theming/Icons.axaml`. If absent, add them from Fluent System Icons in this step (regular 16 glyph paths) and cover them in `ThemeResourceTests`'s icon-resolution theory. `TaskGridConverters.ChevronAngle` (bool→0/90) may already exist for Projects child rows — reuse it; if not, add a one-line `IValueConverter` there returning `90.0` when true else `0.0`.

Add the string:
```xml
  <data name="RailGroupToggleTooltip" xml:space="preserve"><value>Toggle list / grouped view</value></data>
```

- [ ] **Step 4: Wire viewport height + non-selectable headers in `ShellWindow.axaml.cs`**

In `AttachShell` (after `_shell` is set), subscribe to Nav bounds and push the height:
```csharp
        Nav.GetObservable(Avalonia.Visual.BoundsProperty).Subscribe(b => _shell?.SetRailViewportHeight(b.Height));
```
(Store the `IDisposable` and dispose it when detaching, mirroring the existing detach pattern. A one-shot subscription for the window's life is acceptable since the window owns `Nav`.)

Replace `OnHostSelectionChanged` so header rows toggle their group and never hold selection or change scope:
```csharp
    private bool _revertingRailSelection;

    private void OnHostSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_shell is null || _revertingRailSelection)
            return;

        if (HostList.SelectedItem is GroupHeaderRailItemViewModel header)
        {
            header.ToggleCommand.Execute(null);   // expand/collapse; RebuildRail refreshes rows
            // A header is not a scope; restore the prior selection without recursing.
            _revertingRailSelection = true;
            try { HostList.SelectedItem = e.RemovedItems.Count > 0 ? e.RemovedItems[0] : null; }
            finally { _revertingRailSelection = false; }
            return;
        }

        // Auth-failed host click opens the Edit dialog (Task 12), not Settings.
        if (HostList.SelectedItem is HostRailItemViewModel { State: RailState.AuthFailed } item)
            OpenEditHostDialog(item.HostId, focusPassword: true, authError: true);   // added in Task 12
    }
```

> Until Task 12 lands `OpenEditHostDialog`, keep the existing `_shell.NavigateToSettings(item.HostId)` line here and swap it in Task 12 (note it inline so the swap is not forgotten).

- [ ] **Step 5: Run — expect PASS** (`HostRailGroupingTests` + existing `ShellRailTests`). Tune `ReservedRailChrome` in `ShellViewModel` if the 12-host/700px case doesn't cross the boundary: add a temporary assert on `shell.RailEntries.Count` and adjust the constant so the geometry matches; keep the final value.

- [ ] **Step 6: Commit**

```bash
git add src/Lattice.App/Views/ShellWindow.axaml src/Lattice.App/Views/ShellWindow.axaml.cs \
        src/Lattice.App/Theming/Icons.axaml src/Lattice.App/Localization/Strings.resx \
        tests/Lattice.App.Tests/Headless/HostRailGroupingTests.cs
git commit -m "feat(shell): group-header rail rows + list/group toggle + viewport wiring (design 3a)"
```

---

## Phase C — Host management moves to the rail (design 3b)

### Task 9: Make `AddHostViewModel` mode-aware (Add / Edit) with Test + password-error

**Files:**
- Modify: `src/Lattice.App/ViewModels/AddHostViewModel.cs`
- Modify: `tests/Lattice.App.Tests/AddHostViewModelTests.cs`

Edit mode (design 3b / card `2d`): prefilled fields, title "Edit host", primary "Save" → `UpdateHost`, a Test-connection button, and an openable password-error state (auth-failed deep link).

- [ ] **Step 1: Write failing tests**

Append to `AddHostViewModelTests.cs`:

```csharp
[Fact]
public void Edit_mode_prefills_from_the_host_and_titles_for_editing()
{
    var registry = new HostRegistry(new LatticeConfig(5, []), NewPath());
    var cfg = TestData.MakeHostConfig(name: "mini-01", address: "192.168.1.40");
    registry.AddHost(cfg);
    var vm = AddHostViewModel.ForEdit(registry, () => new FakeGuiRpcClient(), cfg, authError: false);

    Assert.Equal(HostDialogMode.Edit, vm.Mode);
    Assert.Equal("mini-01", vm.Name);
    Assert.Equal("192.168.1.40", vm.Address);
    Assert.Equal(Strings.EditHostDialogTitle, vm.DialogTitle);
    Assert.Equal(Strings.EditHostPrimaryButton, vm.PrimaryButtonText);
    Assert.True(vm.ShowTestButton);
}

[Fact]
public async Task Edit_mode_save_updates_the_host_in_the_registry()
{
    var registry = new HostRegistry(new LatticeConfig(5, []), NewPath());
    var cfg = TestData.MakeHostConfig(name: "mini-01");
    registry.AddHost(cfg);
    // Connect succeeds by default with FakeGuiRpcClient.
    var vm = AddHostViewModel.ForEdit(registry, () => new FakeGuiRpcClient(), cfg, authError: false)
    { Name = "mini-01-renamed" };

    await vm.AddCommand.ExecuteAsync(null);

    Assert.True(vm.Succeeded);
    Assert.Equal("mini-01-renamed", registry.Hosts.Single(h => h.Id == cfg.Id).Name);
}

[Fact]
public void Auth_failed_edit_opens_with_the_password_error_shown()
{
    var registry = new HostRegistry(new LatticeConfig(5, []), NewPath());
    var cfg = TestData.MakeHostConfig(name: "mini-01");
    registry.AddHost(cfg);
    var vm = AddHostViewModel.ForEdit(registry, () => new FakeGuiRpcClient(), cfg, authError: true);

    Assert.True(vm.HasPasswordError);
    Assert.Equal(string.Format(Strings.EditHostPasswordError, "mini-01"), vm.PasswordErrorText);
}
```

Add a `NewPath()` helper in the test class if not present: `private static string NewPath() => Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}.json");`

- [ ] **Step 2: Run — expect FAIL.**

- [ ] **Step 3: Implement mode-awareness**

Add the enum (top of `AddHostViewModel.cs`, under the namespace):
```csharp
public enum HostDialogMode { Add, Edit }
```
Add fields + a private full constructor + the two factories, and mode-dependent surface. Key additions:
```csharp
    private readonly Guid _editId;       // Guid.Empty in Add mode

    public HostDialogMode Mode { get; private init; } = HostDialogMode.Add;

    public string DialogTitle => Mode == HostDialogMode.Edit ? Strings.EditHostDialogTitle : Strings.AddHostDialogTitle;
    public string PrimaryButtonText => Mode == HostDialogMode.Edit ? Strings.EditHostPrimaryButton : Strings.AddHostPrimaryButton;
    public bool ShowTestButton => Mode == HostDialogMode.Edit;

    [ObservableProperty] private bool _hasPasswordError;
    [ObservableProperty] private string? _passwordErrorText;
    [ObservableProperty] private string? _testResultText;

    /// <summary>Edit an existing host: prefilled, retitled, Save→UpdateHost.</summary>
    public static AddHostViewModel ForEdit(HostRegistry registry, Func<IGuiRpcClient> clientFactory,
        HostConfig host, bool authError) =>
        new(registry, clientFactory)
        {
            Mode = HostDialogMode.Edit,
            _editId = host.Id,      // set via private field init below (see note)
            Name = host.Name,
            Address = host.Address,
            PortText = host.Port.ToString(),
            Password = host.Password,
            HasPasswordError = authError,
            PasswordErrorText = authError ? string.Format(Strings.EditHostPasswordError, host.DisplayName) : null,
        };
```

> `_editId` is `readonly`; set it through the constructor rather than an object initializer. Cleanest: add a private ctor `AddHostViewModel(HostRegistry registry, Func<IGuiRpcClient> clientFactory, Guid editId, HostDialogMode mode)` that assigns `_editId`/`Mode`, and have `ForEdit` call it then set the bindable props. Keep the existing public `(registry, clientFactory)` ctor for Add (it sets `_editId = Guid.Empty`, `Mode = Add`).

Rework `AddAsync` to branch on mode after a successful test:
```csharp
            if (result.Success)
            {
                var op = Mode == HostDialogMode.Edit
                    ? (Func<string?>)(() => Register(candidate with { Id = _editId }, edit: true))
                    : () => Register(candidate, edit: false);
                if (op() is { } error) ErrorText = error;
                else { Succeeded = true; ErrorText = null; }
            }
```
where `candidate` is built as today (`new HostConfig(Guid.NewGuid(), …)` for Add) and:
```csharp
    private string? Register(HostConfig cfg, bool edit) =>
        RegistryGuard.TryMutate(() => { if (edit) _registry.UpdateHost(cfg); else _registry.AddHost(cfg); }) is { } err
            ? string.Format(edit ? Strings.EditHostSaveFailedFmt : Strings.AddHostSaveFailedFmt, err)
            : null;
```
Clear `HasPasswordError`/`PasswordErrorText` on any field edit (`OnPasswordChanged`) so the danger border lifts once the user types.

Add a `TestConnectionCommand` (same body as `HostSettingsItemViewModel.TestConnectionAsync`, writing `TestResultText`):
```csharp
    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        var candidate = new HostConfig(_editId == Guid.Empty ? Guid.NewGuid() : _editId,
            Name.Trim(), Address.Trim(), int.TryParse(PortText, out var p) ? p : 0, Password);
        TestResultText = Strings.SettingsTestConnectionBusy;
        try
        {
            using var cts = new CancellationTokenSource(TestTimeout);
            var r = await HostMonitorManager.TestConnectionAsync(candidate, _clientFactory, cts.Token);
            TestResultText = r.Success
                ? string.Format(Strings.SettingsTestConnectionSuccess, r.Version!.Major, r.Version.Minor, r.Version.Release)
                : r.Error;
        }
        catch (OperationCanceledException) { TestResultText = Strings.SettingsTestConnectionTimeout; }
    }
```

Add the new strings (final consolidation in Task 15): `EditHostDialogTitle` ("Edit host"), `EditHostPrimaryButton` ("Save"), `EditHostPasswordError` ("The host refused this password. Check gui_rpc_auth.cfg on {0}."), `EditHostSaveFailedFmt` ("Couldn’t save changes: {0}").

- [ ] **Step 4: Run — expect PASS** (new + existing `AddHostViewModelTests`).

- [ ] **Step 5: Commit**

```bash
git add src/Lattice.App/ViewModels/AddHostViewModel.cs tests/Lattice.App.Tests/AddHostViewModelTests.cs \
        src/Lattice.App/Localization/Strings.resx
git commit -m "feat(hosts): mode-aware Add/Edit host view-model (design 3b)"
```

---

### Task 10: Edit-dialog UI — title/buttons, Test button, password danger + focus

**Files:**
- Modify: `src/Lattice.App/Views/AddHostDialog.axaml`
- Modify: `src/Lattice.App/Views/AddHostDialog.axaml.cs`
- Modify: `src/Lattice.App/ViewModels/AddHostViewModel.cs` (add `TestButtonText`)
- Create: `tests/Lattice.App.Tests/Headless/EditHostDialogTests.cs`

- [ ] **Step 1: Write failing headless test**

```csharp
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using Lattice.App.Localization;
using Lattice.App.Tests.Fakes;
using Lattice.App.ViewModels;
using Lattice.App.Views;
using Lattice.Core;
using Lattice.Tests;
using Xunit;
using static Lattice.Tests.HeadlessLayout;

namespace Lattice.App.Tests.Headless;

public class EditHostDialogTests
{
    [AvaloniaFact]
    public async Task Auth_failed_edit_dialog_shows_error_and_focuses_password()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}.json");
        var registry = new HostRegistry(new LatticeConfig(5, []), path);
        var cfg = TestData.MakeHostConfig(name: "mini-01");
        registry.AddHost(cfg);
        var vm = AddHostViewModel.ForEdit(registry, () => new FakeGuiRpcClient(), cfg, authError: true);
        var dialog = new AddHostDialog { DataContext = vm };
        var window = new Window { Width = 600, Height = 500 };
        window.Show();
        Layout(window);
        _ = dialog.ShowAsync(window);
        Layout(window);

        Assert.Equal(Strings.EditHostDialogTitle, dialog.Title);
        var pwd = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "PasswordBox");
        Assert.Contains("danger", pwd.Classes);
        Assert.True(pwd.IsFocused);
        // Secondary "Test connection" button is present in edit mode.
        Assert.Equal(Strings.SettingsTestConnectionButton, dialog.SecondaryButtonText);
        dialog.Hide();
        window.Close();
    }
}
```

- [ ] **Step 2: Run — expect FAIL.**

- [ ] **Step 3: Implement**

Add to `AddHostViewModel`: `public string? TestButtonText => ShowTestButton ? Strings.SettingsTestConnectionButton : null;`

In `AddHostDialog.axaml`, bind the mode-aware surface and mark up the password field:
```xml
                    Title="{Binding DialogTitle}"
                    PrimaryButtonText="{Binding PrimaryButtonText}"
                    SecondaryButtonText="{Binding TestButtonText}"
                    CloseButtonText="{x:Static loc:Strings.AddHostCloseButton}"
```
Replace the password `TextBox` with a named, danger-classable one plus the error line:
```xml
    <TextBox x:Name="PasswordBox" Text="{Binding Password, Mode=TwoWay}" PasswordChar="●"
             Classes.danger="{Binding HasPasswordError}"
             PlaceholderText="{x:Static loc:Strings.AddHostFieldPasswordPlaceholder}" />
    <TextBlock Text="{Binding PasswordErrorText}" FontSize="12"
               IsVisible="{Binding HasPasswordError}"
               Foreground="{DynamicResource LatticeDangerFgBrush}" />
    <TextBlock Text="{Binding TestResultText}" FontSize="12"
               IsVisible="{Binding TestResultText, Converter={x:Static ObjectConverters.IsNotNull}}"
               Foreground="{DynamicResource LatticeTextSecondaryBrush}" />
```
Add a danger style in `Window.Resources`/`Styles` of the dialog (or `ControlStyles.axaml`):
```xml
    <Style Selector="TextBox.danger">
      <Setter Property="BorderBrush" Value="{DynamicResource LatticeDangerFgBrush}" />
    </Style>
```

In `AddHostDialog.axaml.cs`, add the secondary (Test) handler and password focus:
```csharp
    public AddHostDialog()
    {
        InitializeComponent();
        PrimaryButtonClick += OnPrimaryClick;
        SecondaryButtonClick += OnSecondaryClick;
        Opened += OnOpened;
    }

    private void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        if (DataContext is AddHostViewModel { HasPasswordError: true } && this.FindControl<TextBox>("PasswordBox") is { } pwd)
            pwd.Focus();
    }

    // Test connection: never closes the dialog; runs the test and shows the result inline.
    private async void OnSecondaryClick(FAContentDialog sender, FAContentDialogButtonClickEventArgs args)
    {
        if (DataContext is not AddHostViewModel vm) return;
        FADeferral deferral = args.GetDeferral();
        try { await vm.TestConnectionCommand.ExecuteAsync(null); args.Cancel = true; }
        finally { deferral.Complete(); }
    }
```

> Verify the exact FA 3.0.1 `Opened` event signature (`ContentDialog`/`FAContentDialog` + `ContentDialogOpenedEventArgs`) via the FA source; adjust the delegate types if they differ. `SecondaryButtonClick` mirrors the proven `PrimaryButtonClick` deferral pattern already in this file.

- [ ] **Step 4: Run — expect PASS.**

- [ ] **Step 5: Commit**

```bash
git add src/Lattice.App/Views/AddHostDialog.axaml src/Lattice.App/Views/AddHostDialog.axaml.cs \
        src/Lattice.App/ViewModels/AddHostViewModel.cs src/Lattice.App/Theming/ControlStyles.axaml \
        tests/Lattice.App.Tests/Headless/EditHostDialogTests.cs
git commit -m "feat(hosts): edit-dialog UI — Test button, password error + focus (design 3b)"
```

---

### Task 11: Host-row `MenuFlyout` — Edit / Test / Remove (design 3b)

**Files:**
- Modify: `src/Lattice.App/Views/ShellWindow.axaml` (host-row template only)
- Modify: `src/Lattice.App/ViewModels/ShellViewModel.cs` (host command events + config/row lookups)
- Modify: `src/Lattice.App/ViewModels/HostRailItemViewModel.cs` (inline `TestResultText`)
- Modify: `src/Lattice.App/Views/ShellWindow.axaml.cs` (Edit/Test/Remove handlers + Remove-confirm machinery)
- Create: `tests/Lattice.App.Tests/Headless/HostRailMenuTests.cs`

The `MenuFlyout` lives on the `HostRailItemViewModel` template only, so the All-hosts sentinel and group headers never get a menu (design 3b). Commands live on `ShellViewModel`; the window does the dialog/test work. Remove reuses the exact single-flight confirm machinery currently in `SettingsView.axaml.cs`, moved here.

- [ ] **Step 1: Write failing headless tests**

```csharp
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using Lattice.App.Infrastructure;
using Lattice.App.Tests.Fakes;
using Lattice.App.ViewModels;
using Lattice.App.Views;
using Lattice.Core;
using Lattice.Tests;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using static Lattice.Tests.HeadlessLayout;

namespace Lattice.App.Tests.Headless;

public class HostRailMenuTests
{
    private static (ShellWindow Window, ShellViewModel Shell, HostRegistry Registry) MakeShell()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}.json");
        var registry = new HostRegistry(new LatticeConfig(5, []), path);
        var manager = new HostMonitorManager(registry, () => new FakeGuiRpcClient(), new FakeTimeProvider());
        var store = new HostStore(registry, manager, new ImmediateUiDispatcher());
        var uiState = new UiStateStore(Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}-ui.json"));
        var shell = new ShellViewModel(registry, store, new ManualUiClock(), uiState, () => new FakeGuiRpcClient());
        var window = new ShellWindow { DataContext = shell, Width = 1280, Height = 800 };
        return (window, shell, registry);
    }

    [AvaloniaFact]
    public void Edit_host_command_opens_a_prefilled_edit_dialog()
    {
        var (window, shell, registry) = MakeShell();
        window.Show();
        var cfg = TestData.MakeHostConfig(name: "mini-01");
        registry.AddHost(cfg);
        Layout(window);

        shell.EditHostCommand.Execute(cfg.Id);
        Dispatcher.UIThread.RunJobs();

        var dialog = Assert.Single(window.GetVisualDescendants().OfType<AddHostDialog>());
        var vm = Assert.IsType<AddHostViewModel>(dialog.DataContext);
        Assert.Equal(HostDialogMode.Edit, vm.Mode);
        Assert.Equal("mini-01", vm.Name);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Remove_host_command_confirms_then_removes()
    {
        var (window, shell, registry) = MakeShell();
        window.Show();
        var cfg = TestData.MakeHostConfig(name: "mini-01");
        registry.AddHost(cfg);
        Layout(window);

        shell.RemoveHostCommand.Execute(cfg.Id);
        Dispatcher.UIThread.RunJobs();
        var dialog = Assert.Single(window.GetVisualDescendants().OfType<FAContentDialog>());
        dialog.Hide(FAContentDialogResult.Primary);
        await HeadlessSync.WaitUntilAsync(() => registry.Hosts.Count == 0);

        Assert.Empty(registry.Hosts);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Test_host_command_writes_the_result_into_the_row_subtext()
    {
        var (window, shell, registry) = MakeShell();
        window.Show();
        var cfg = TestData.MakeHostConfig(name: "mini-01");
        registry.AddHost(cfg);
        Layout(window);
        var row = shell.RailEntries.OfType<HostRailItemViewModel>().Single();

        shell.TestHostCommand.Execute(cfg.Id);
        // FakeGuiRpcClient connects + exchanges versions successfully by default.
        await HeadlessSync.WaitUntilAsync(() => row.TestResultText is not null
            && !row.TestResultText.Equals(Strings.SettingsTestConnectionBusy));

        Assert.Contains("8", row.TestResultText!); // "Connected · BOINC 8.x.x"
        window.Close();
    }
}
```

- [ ] **Step 2: Run — expect FAIL** (no `EditHostCommand`/`RemoveHostCommand`/`TestHostCommand`, no `TestResultText`).

- [ ] **Step 3: `HostRailItemViewModel` — inline test result**

Add `[ObservableProperty] private string? _testResultText;`. Change the subtext to prefer it: the template currently binds `StateText`; add the property `public string SubtextDisplay => TestResultText ?? StateText;` and raise it in `OnTestResultTextChanged` + at the end of `Refresh()` (and clear `TestResultText = null;` at the top of `Refresh()` so a fresh poll reverts to live state). Bind the template's second `TextBlock` to `SubtextDisplay`. Fold `TestResultText` into `Tooltip` when set.

- [ ] **Step 4: `ShellViewModel` — command events + lookups**

```csharp
    public event EventHandler<Guid>? EditHostRequested;
    public event EventHandler<Guid>? TestHostRequested;
    public event EventHandler<Guid>? RemoveHostRequested;

    [RelayCommand] private void EditHost(Guid id) => EditHostRequested?.Invoke(this, id);
    [RelayCommand] private void TestHost(Guid id) => TestHostRequested?.Invoke(this, id);
    [RelayCommand] private void RemoveHost(Guid id) => RemoveHostRequested?.Invoke(this, id);

    public HostConfig? FindHostConfig(Guid id) =>
        _store.Hosts.FirstOrDefault(h => h.Config.Id == id)?.Config;
    public HostRailItemViewModel? FindHostRow(Guid id) =>
        _hostRowVms.TryGetValue(id, out var vm) ? vm : null;
    public HostRegistry Registry => Settings.Registry;
    public Func<IGuiRpcClient> ClientFactory => Settings.ClientFactory;
```
(Detach the three events in `Dispose`.)

- [ ] **Step 5: `ShellWindow.axaml` — MenuFlyout on the host row**

Add `ContextFlyout` to the host-row template's root `DockPanel` (the `HostRailItemViewModel` `DataTemplate`):
```xml
                <DockPanel.ContextFlyout>
                  <MenuFlyout>
                    <MenuItem Header="{x:Static loc:Strings.HostMenuEdit}"
                              Command="{Binding $parent[ListBox].((vm:ShellViewModel)DataContext).EditHostCommand}"
                              CommandParameter="{Binding HostId}" />
                    <MenuItem Header="{x:Static loc:Strings.HostMenuTest}"
                              Command="{Binding $parent[ListBox].((vm:ShellViewModel)DataContext).TestHostCommand}"
                              CommandParameter="{Binding HostId}" />
                    <MenuItem Header="{x:Static loc:Strings.HostMenuRemove}"
                              Command="{Binding $parent[ListBox].((vm:ShellViewModel)DataContext).RemoveHostCommand}"
                              CommandParameter="{Binding HostId}"
                              Foreground="{DynamicResource LatticeDangerFgBrush}" />
                  </MenuFlyout>
                </DockPanel.ContextFlyout>
```
Add a `MinHeight="32"` item style if the theme's default differs from the 32 px spec (design 3b): a `Style Selector="MenuFlyout MenuItem"` in `Window.Styles` with `MinHeight=32`.

- [ ] **Step 6: `ShellWindow.axaml.cs` — handlers + Remove-confirm (moved from SettingsView)**

In `AttachShell`, subscribe/unsubscribe the three events alongside `AddHostRequested`. Add:
```csharp
    private bool _editHostInFlight;
    private bool _removeConfirmInFlight;

    private async void OnEditHostRequested(object? sender, Guid id) =>
        await OpenEditHostDialog(id, focusPassword: false, authError: false);

    private async Task OpenEditHostDialog(Guid id, bool focusPassword, bool authError)
    {
        if (_editHostInFlight || _shell is not { } shell || shell.FindHostConfig(id) is not { } cfg) return;
        _editHostInFlight = true;
        try
        {
            var vm = AddHostViewModel.ForEdit(shell.Registry, shell.ClientFactory, cfg, authError);
            var dialog = new AddHostDialog { DataContext = vm };
            if (TopLevel.GetTopLevel(this) is { } top) await dialog.ShowAsync(top);
        }
        finally { _editHostInFlight = false; }
    }

    private async void OnTestHostRequested(object? sender, Guid id)
    {
        if (_shell is not { } shell || shell.FindHostConfig(id) is not { } cfg
            || shell.FindHostRow(id) is not { } row) return;
        row.TestResultText = Strings.SettingsTestConnectionBusy;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var r = await HostMonitorManager.TestConnectionAsync(cfg, shell.ClientFactory, cts.Token);
            row.TestResultText = r.Success
                ? string.Format(Strings.SettingsTestConnectionSuccess, r.Version!.Major, r.Version.Minor, r.Version.Release)
                : r.Error;
        }
        catch (OperationCanceledException) { row.TestResultText = Strings.SettingsTestConnectionTimeout; }
    }

    private async void OnRemoveHostRequested(object? sender, Guid id)
    {
        if (_removeConfirmInFlight || _shell is not { } shell
            || shell.FindHostConfig(id) is not { } cfg
            || TopLevel.GetTopLevel(this) is not { } top) return;
        _removeConfirmInFlight = true;
        try
        {
            var dialog = new FAContentDialog
            {
                Title = string.Format(Strings.HostRemoveConfirmTitleFmt, cfg.DisplayName),
                Content = Strings.HostRemoveConfirmBody,
                PrimaryButtonText = Strings.HostRemoveConfirmPrimary,
                CloseButtonText = Strings.HostRemoveConfirmCancel,
                DefaultButton = FAContentDialogButton.Close,
            };
            if (await dialog.ShowAsync(top) == FAContentDialogResult.Primary
                && shell.FindHostConfig(id) is not null)
                RegistryGuard.TryMutate(() => shell.Registry.RemoveHost(id));
        }
        finally { _removeConfirmInFlight = false; }
    }
```
(Add `using Lattice.Core;` / `using Lattice.App.Infrastructure;` / `using Lattice.App.Localization;` as needed.)

Add strings (final pass Task 15): `HostMenuEdit` ("Edit host…"), `HostMenuTest` ("Test connection"), `HostMenuRemove` ("Remove host…"), `HostRemoveConfirmTitleFmt` ("Remove {0}?"), `HostRemoveConfirmBody` ("Lattice stops monitoring this host. The BOINC client on the host is not affected."), `HostRemoveConfirmPrimary` ("Remove"), `HostRemoveConfirmCancel` ("Cancel").

- [ ] **Step 7: Run — expect PASS** (`HostRailMenuTests` + prior suites).

- [ ] **Step 8: Commit**

```bash
git add src/Lattice.App/Views/ShellWindow.axaml src/Lattice.App/Views/ShellWindow.axaml.cs \
        src/Lattice.App/ViewModels/ShellViewModel.cs src/Lattice.App/ViewModels/HostRailItemViewModel.cs \
        src/Lattice.App/Localization/Strings.resx tests/Lattice.App.Tests/Headless/HostRailMenuTests.cs
git commit -m "feat(hosts): rail row MenuFlyout — edit/test/remove (design 3b)"
```

---

### Task 12: Auth-failed rail click opens the Edit dialog (design 3b / 3a §4)

**Files:**
- Modify: `src/Lattice.App/Views/ShellWindow.axaml.cs`
- Modify: `tests/Lattice.App.Tests/Headless/AuthFailedLinkageTests.cs`

Clicking an auth-failed host now opens Edit with the password field in error + focused (Task 10/11 built the pieces), replacing the old "navigate to Settings expander".

- [ ] **Step 1: Rewrite the linkage test to the new target**

Replace the body of `Selecting_an_auth_failed_host_lands_in_settings_with_that_host_expanded` (rename it) in `AuthFailedLinkageTests.cs`:

```csharp
    [AvaloniaFact]
    public async Task Selecting_an_auth_failed_host_opens_the_edit_dialog_with_the_password_error()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}.json");
        var registry = new HostRegistry(new LatticeConfig(5, []), path);
        var manager = new HostMonitorManager(registry, () => new FakeGuiRpcClient(), new FakeTimeProvider());
        var store = new HostStore(registry, manager, new ImmediateUiDispatcher());
        var uiState = new UiStateStore(Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}-ui.json"));
        var shell = new ShellViewModel(registry, store, new ManualUiClock(), uiState, () => new FakeGuiRpcClient());
        var window = new ShellWindow { DataContext = shell };
        window.Show();
        Layout(window);

        var host = TestData.MakeHostConfig(name: "office-pc");
        registry.AddHost(host);
        store.Hosts[0].Status = new ConnectionStatus(
            host.Id, HostConnectionState.AuthFailed, 1, null, "unauthorized", null);
        shell.RailEntries.OfType<HostRailItemViewModel>().Single().Refresh();
        Layout(window);

        window.HostList.SelectedIndex = 1;   // index 0 is the All-hosts sentinel
        await HeadlessSync.WaitUntilAsync(() => window.GetVisualDescendants().OfType<AddHostDialog>().Any());

        var dialog = Assert.Single(window.GetVisualDescendants().OfType<AddHostDialog>());
        var vm = Assert.IsType<AddHostViewModel>(dialog.DataContext);
        Assert.Equal(HostDialogMode.Edit, vm.Mode);
        Assert.True(vm.HasPasswordError);
        Assert.Same(shell.Settings, shell.CurrentPage is SettingsViewModel ? shell.CurrentPage : shell.Settings); // NOT navigated to Settings
        Assert.IsNotType<SettingsViewModel>(shell.CurrentPage);
        dialog.Hide();
        window.Close();
    }
```

Add `using Avalonia.VisualTree;`, `using FluentAvalonia.UI.Controls;`, `using Lattice.App.Views;` as needed. Drop the old assertions on `shell.Settings.Hosts` / `IsExpanded` (that surface is gone after Task 13).

- [ ] **Step 2: Run — expect FAIL** (Task 8 left `NavigateToSettings` in place as the interim).

- [ ] **Step 3: Swap the handler line**

In `ShellWindow.axaml.cs` `OnHostSelectionChanged`, replace the interim auth-failed branch with the Edit-dialog open:
```csharp
        if (HostList.SelectedItem is HostRailItemViewModel { State: RailState.AuthFailed } item)
            _ = OpenEditHostDialog(item.HostId, focusPassword: true, authError: true);
```

- [ ] **Step 4: Run — expect PASS.**

- [ ] **Step 5: Commit**

```bash
git add src/Lattice.App/Views/ShellWindow.axaml.cs tests/Lattice.App.Tests/Headless/AuthFailedLinkageTests.cs
git commit -m "feat(hosts): auth-failed rail click opens the edit dialog (design 3b)"
```

---

## Phase D — Settings: drop host group, add Theme (design 2d / 1f)

### Task 13: Remove the Settings Hosts group → pointer caption

**Files:**
- Delete: `src/Lattice.App/ViewModels/HostSettingsItemViewModel.cs`
- Modify: `src/Lattice.App/ViewModels/SettingsViewModel.cs`
- Modify: `src/Lattice.App/Views/SettingsView.axaml` / `.axaml.cs`
- Modify: `src/Lattice.App/ViewModels/ShellViewModel.cs` (`NavigateToSettings` loses the host focus; drop the `Settings.Reconcile()` call)
- Modify: `tests/Lattice.App.Tests/SettingsViewModelTests.cs`, `tests/Lattice.App.Tests/Headless/SettingsViewTests.cs`

- [ ] **Step 1: Update the tests to the new shape (red)**

In `SettingsViewTests.cs`: delete the host-expander/remove tests (`Renders_one_expander_per_host_plus_polling_selector`, `Confirming_remove_*`, `Cancelling_remove_*`, `Double_clicking_remove_*`, `Navigating_away_unsubscribes_*`). Add:
```csharp
    [AvaloniaFact]
    public void Renders_pointer_caption_and_no_host_expanders()
    {
        var (window, _, _) = MakeView();
        window.Show();
        Layout(window);

        Assert.Empty(window.GetVisualDescendants().OfType<FASettingsExpander>()
            .Where(e => e.Name != "PollingExpander"));
        var caption = window.GetVisualDescendants().OfType<TextBlock>()
            .SingleOrDefault(t => t.Text == Strings.SettingsHostsPointer);
        Assert.NotNull(caption);
        window.Close();
    }
```
Simplify `MakeView` (no `store.Changed += Reconcile`; keep adding two hosts to prove they do NOT appear). In `SettingsViewModelTests.cs`, delete any assertions touching `Hosts`, `Reconcile`, `ExpandHost`, or `Remove`.

- [ ] **Step 2: Run — expect FAIL / non-compiling** (references to removed members).

- [ ] **Step 3: Prune `SettingsViewModel`**

Delete: `Hosts`, `Reconcile`, `ExpandHost`, `Remove`, `RemoveRequested`, `HasRemoveSubscribersForTests`. Keep: `Registry`, `ClientFactory`, `PollingIntervalSeconds`, `PollingError`, `AllowedPollingIntervals`. The ctor drops its `Reconcile()` call. (Theme members arrive in Task 14.)

- [ ] **Step 4: Delete `HostSettingsItemViewModel.cs`**

```bash
git rm src/Lattice.App/ViewModels/HostSettingsItemViewModel.cs
```

- [ ] **Step 5: Rewrite `SettingsView.axaml` host region**

Remove the `SettingsHostsSection` `TextBlock` and the entire `<ItemsControl ItemsSource="{Binding Hosts}"> … </ItemsControl>`. Replace with the pointer caption above the Polling group:
```xml
      <TextBlock Text="{x:Static loc:Strings.SettingsHostsPointer}" FontSize="12" Margin="0,4,0,8"
                 TextWrapping="Wrap"
                 Foreground="{DynamicResource LatticeTextSecondaryBrush}" />
```
Empty `SettingsView.axaml.cs` back to just `InitializeComponent()` — delete `_subscribed`, `OnDataContextChanged`, `OnRemoveRequested`, `SubscribedVmForTests`, `_removeConfirmInFlight` (the confirm machinery now lives in `ShellWindow.axaml.cs`, Task 11).

- [ ] **Step 6: Fix `ShellViewModel`**

Change `NavigateToSettings` to drop the host-focus param and body:
```csharp
    public void NavigateToSettings()
    {
        SelectedView = null;
        CurrentPage = Settings;
    }
```
Update the caller in `ShellWindow.axaml.cs` `OnNavSelectionChanged` (`_shell.NavigateToSettings()` — already parameterless there). Remove the now-removed `Settings.Reconcile();` line from `ReconcileHosts` (Task 7 left it/stubbed).

- [ ] **Step 7: Run — expect PASS**

Run: `dotnet test tests/Lattice.App.Tests`
Expected: PASS. Add string `SettingsHostsPointer` ("Hosts are managed from the sidebar — use “+” to add, right-click a host to edit or remove.").

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat(settings): remove host group; hosts managed from the sidebar (design 3b)"
```

---

### Task 14: Theme setting — Light / Dark / System (design 2d / 1f)

**Files:**
- Create: `src/Lattice.App/Infrastructure/ThemePreference.cs`
- Create: `tests/Lattice.App.Tests/ThemePreferenceTests.cs`
- Modify: `src/Lattice.App/ViewModels/SettingsViewModel.cs`
- Modify: `src/Lattice.App/Views/SettingsView.axaml` (+ a `ThemeLabelConverter`)
- Modify: `src/Lattice.App/ViewModels/ShellViewModel.cs` (create + own the `ThemePreference` from its `uiState`)
- Create: `tests/Lattice.App.Tests/Headless/ThemeSettingTests.cs`

`ThemePreference` mirrors `DensityPreference`: holds the live `AppTheme`, persists via `UiStateStore.Update`, and applies to `Application.Current.RequestedThemeVariant` (`Light`/`Dark`/`Default`; FluentAvaloniaTheme follows the OS on `Default`). `ShellViewModel` constructs it from the `uiState` it already receives (no ctor-signature change; startup theme is applied when the shell is built) and passes it to `SettingsViewModel`.

- [ ] **Step 1: Write failing preference tests**

```csharp
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Lattice.App.Infrastructure;
using Xunit;

namespace Lattice.App.Tests;

public class ThemePreferenceTests
{
    private static string NewPath() => Path.Combine(Path.GetTempPath(), $"lattice-ui-{Guid.NewGuid():N}.json");

    [AvaloniaFact]
    public void Setting_dark_applies_and_persists()
    {
        var path = NewPath();
        var pref = new ThemePreference(new UiStateStore(path));
        pref.Set(AppTheme.Dark);

        Assert.Equal(ThemeVariant.Dark, Application.Current!.RequestedThemeVariant);
        Assert.Equal(AppTheme.Dark, new ThemePreference(new UiStateStore(path)).Value);
        File.Delete(path);
    }

    [AvaloniaFact]
    public void System_maps_to_the_default_variant()
    {
        var pref = new ThemePreference(new UiStateStore(NewPath()));
        pref.Set(AppTheme.System);
        Assert.Equal(ThemeVariant.Default, Application.Current!.RequestedThemeVariant);
    }
}
```

- [ ] **Step 2: Run — expect FAIL.**

- [ ] **Step 3: Implement `ThemePreference`**

```csharp
using Avalonia;
using Avalonia.Styling;

namespace Lattice.App.Infrastructure;

/// <summary>Single owner of the app theme (UiState.Theme): live value + persistence,
/// and applies it to the running Application. System => ThemeVariant.Default, which
/// FluentAvaloniaTheme resolves by following the OS (design 2d/1f).</summary>
public sealed class ThemePreference
{
    private readonly UiStateStore _store;

    public ThemePreference(UiStateStore store)
    {
        _store = store;
        Value = store.Load().Theme;
        Apply();
    }

    public AppTheme Value { get; private set; }

    public void Set(AppTheme value)
    {
        if (Value == value) return;
        Value = value;
        _store.Update(s => s with { Theme = value });
        Apply();
    }

    private void Apply()
    {
        if (Application.Current is not { } app) return;
        app.RequestedThemeVariant = Value switch
        {
            AppTheme.Light => ThemeVariant.Light,
            AppTheme.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }
}
```

- [ ] **Step 4: Wire `SettingsViewModel` + `ShellViewModel`**

`ShellViewModel` ctor: after loading `uiState`, add `Settings = new SettingsViewModel(registry, store, clientFactory, new ThemePreference(uiState));`.

`SettingsViewModel`: add `ThemePreference` param + theme surface:
```csharp
    private readonly ThemePreference _theme;
    // ctor: _theme = theme;
    public static IReadOnlyList<AppTheme> AllThemes { get; } = [AppTheme.Light, AppTheme.Dark, AppTheme.System];
    public AppTheme SelectedTheme
    {
        get => _theme.Value;
        set { _theme.Set(value); OnPropertyChanged(); }
    }
```
Update the direct `SettingsViewModel` construction in `SettingsViewTests.MakeView` and any `SettingsViewModelTests` to pass `new ThemePreference(new UiStateStore(<uiPath>))`.

- [ ] **Step 5: Settings UI — Theme group**

Add a `ThemeLabelConverter` (in `TaskGridConverters.cs` or a new `ThemeLabelConverter.cs`) mapping `AppTheme`→`Strings.ThemeLight/ThemeDark/ThemeSystem`. In `SettingsView.axaml`, after the Polling group:
```xml
      <TextBlock Text="{x:Static loc:Strings.SettingsThemeSection}" FontSize="11" FontWeight="SemiBold" Margin="0,8,0,0"
                 Foreground="{DynamicResource LatticeTextSecondaryBrush}" />
      <ui:FASettingsExpander Header="{x:Static loc:Strings.SettingsThemeHeader}"
                             Description="{x:Static loc:Strings.SettingsThemeDescription}">
        <ui:FASettingsExpander.Footer>
          <ComboBox ItemsSource="{x:Static vm:SettingsViewModel.AllThemes}"
                    SelectedItem="{Binding SelectedTheme, Mode=TwoWay}">
            <ComboBox.ItemTemplate>
              <DataTemplate x:DataType="infra:AppTheme">
                <TextBlock Text="{Binding Converter={x:Static v:ThemeLabelConverter.Instance}}" />
              </DataTemplate>
            </ComboBox.ItemTemplate>
          </ComboBox>
        </ui:FASettingsExpander.Footer>
      </ui:FASettingsExpander>
```
Add xmlns `infra` (`using:Lattice.App.Infrastructure`) and `v` (already present) to `SettingsView.axaml`.

- [ ] **Step 6: Add the headless setting test**

`ThemeSettingTests`: build the Settings view over a `SettingsViewModel`, set `SelectedTheme = AppTheme.Dark`, assert `Application.Current!.RequestedThemeVariant == ThemeVariant.Dark` and the ComboBox `SelectedItem` reflects it. Add strings `ThemeLight`/`ThemeDark`/`ThemeSystem`, `SettingsThemeSection`/`SettingsThemeHeader` ("Theme"), `SettingsThemeDescription` ("How Lattice follows light or dark mode").

- [ ] **Step 7: Run — expect PASS** (`dotnet test tests/Lattice.App.Tests`).

- [ ] **Step 8: Commit**

```bash
git add src/Lattice.App/Infrastructure/ThemePreference.cs src/Lattice.App/ViewModels/SettingsViewModel.cs \
        src/Lattice.App/ViewModels/ShellViewModel.cs src/Lattice.App/Views/SettingsView.axaml \
        src/Lattice.App/Views/TaskGridConverters.cs src/Lattice.App/Localization/Strings.resx \
        tests/Lattice.App.Tests/ThemePreferenceTests.cs tests/Lattice.App.Tests/Headless/ThemeSettingTests.cs
git commit -m "feat(settings): Light/Dark/System theme setting (design 2d/1f)"
```

---

## Phase E — Consolidation & verification

### Task 15: ResX consolidation — add new keys, retire host-group keys

**Files:**
- Modify: `src/Lattice.App/Localization/Strings.resx`
- Modify: `tests/Lattice.App.Tests/LocalizationTests.cs` (if it enumerates keys)

Earlier tasks added strings incrementally; this task audits the set: every new key is present and unique, and keys orphaned by the Settings-host removal are retired (T1 convention: meaning-based names, no orphans).

- [ ] **Step 1: Confirm the new keys exist (added across Tasks 6–14)**

`RailGroupAttentionFmt`, `RailGroupHealthyFmt`, `RailGroupToggleTooltip`, `EditHostDialogTitle`, `EditHostPrimaryButton`, `EditHostPasswordError`, `EditHostSaveFailedFmt`, `HostMenuEdit`, `HostMenuTest`, `HostMenuRemove`, `HostRemoveConfirmTitleFmt`, `HostRemoveConfirmBody`, `HostRemoveConfirmPrimary`, `HostRemoveConfirmCancel`, `SettingsHostsPointer`, `SettingsThemeSection`, `SettingsThemeHeader`, `SettingsThemeDescription`, `ThemeLight`, `ThemeDark`, `ThemeSystem`.

- [ ] **Step 2: Retire keys orphaned by the Settings-host removal**

For each candidate, confirm zero references remain before deleting:
```bash
for k in SettingsHostsSection SettingsFieldName SettingsFieldNamePlaceholder SettingsFieldAddress \
         SettingsFieldPort SettingsFieldPassword SettingsFieldPasswordPlaceholder SettingsFieldAddressRequired \
         SettingsFieldPortInvalid SettingsSaveButton SettingsRemoveButton SettingsSaveFailedFmt \
         SettingsRemoveFailedFmt SettingsAuthErrorGuidance RailConnectedWord RailRetryingWord; do
  echo -n "$k: "; grep -rIl "Strings.$k" src tests --include=*.cs --include=*.axaml | wc -l
done
```
Delete from `Strings.resx` only those printing `0`. (`SettingsTestConnectionButton`/`Busy`/`Success`/`Timeout` stay — reused by the Edit dialog + rail Test. `RailAuthFailed`/`RailConnecting`/`RailUnreachable`/`RailRetrying`/`RailConnectedFmt` stay — used by the rail rows.)

- [ ] **Step 3: Run localization + build**

Run: `dotnet build src/Lattice.App && dotnet test tests/Lattice.App.Tests --filter "FullyQualifiedName~LocalizationTests"`
Expected: PASS — no missing/orphan keys; the generated `Strings` designer compiles.

- [ ] **Step 4: Commit**

```bash
git add src/Lattice.App/Localization/Strings.resx tests/Lattice.App.Tests/LocalizationTests.cs
git commit -m "chore(i18n): consolidate rail/host/theme strings; retire settings host keys"
```

---

### Task 16: Full-suite verification + self-review

**Files:** none (verification only)

- [ ] **Step 1: Full build + test**

Run: `dotnet build && dotnet test`
Expected: PASS across `Lattice.Tests`, `Lattice.App.Tests`, `Lattice.Aggregation.Tests`, `Lattice.Verification` (untouched), `Lattice.Core.Machine` (untouched — verification-sync rule respected).

- [ ] **Step 2: Rail geometry probe (headless visual)**

Confirm the design-3a layout holds end-to-end: a `ShellWindow` at 1280×768 with 12 hosts renders group headers; resizing to 1280×1600 flattens them; the compact 48 px rail still shows icons-only for both host rows and (text-hidden) group headers. This is covered by `HostRailGroupingTests` + `ShellRailTests`; if any assertion is height-brittle, re-pin `ReservedRailChrome` and record the final constant.

- [ ] **Step 3: Manual smoke (optional, real daemon)**

Per the dev-environment note (BOINC 8.2.11 live), launch the app, add a host, right-click → Edit/Test/Remove, toggle Theme, and shrink the window to force grouping. Confirms nothing the headless tests can't see (Mica excluded — #11).

- [ ] **Step 4: Verification-sync check**

This change touches no `HostMonitor.cs` / `HostMachine` semantics, so no Promela/F#-spec update is owed. State this explicitly in the PR body (per CLAUDE.md's verification-sync contract).

- [ ] **Step 5: Final commit if any tuning changed**

```bash
git add -A && git commit -m "test(shell): pin rail geometry + final ReservedRailChrome"
```

---

## Deferred (noted, not built here)

- **Compact grouped rendering** (design 3a): stacked single-icon per collapsed Healthy group + 8 px badge dot. M2 renders individual host icons in the compact rail; the stacked-icon visual needs bespoke rendering → **#32 polish wave** (decisions spec §5).
- **`Offline · N` group NOT implemented** (owner decision, decisions spec §2): the card `3a` mock's third group folds into `Attention` in M2 — persistently-unreachable hosts are attention-worthy. Re-add when an M3 terminal paused/disabled/snoozed `RailState` exists (F#/C# exhaustiveness will flag the `RailTier` matches to update). **Record this deliberate deviation on the #57 design-fidelity tracker.**
- **Filled/selected rail icons + InfoBadge refinements** beyond current behavior → #32.
- **#11 Mica**: opaque `LatticeCanvasBrush` paints over Mica — separate on-hardware pass, out of scope.

## Self-review checklist (run before opening the code PR)

1. **Spec coverage** — Area 1 (PaneFooter) = Task 1; Area 2 (grouping core) = Tasks 2–8; Area 3 (host mgmt) = Tasks 9–13; Area 4 (auth-failed) = Task 12; Area 5 (Theme) = Task 14; Area 6 (ResX) = Tasks 6–15. ✔
2. **DU totality** — `RailTierProjection` and every F# `match` are wildcard-free over domain DUs; adding a `RailState`/`RailTier` case is a compile error.
3. **Determinism** — every UI test settles on expected text / fake calls / registry state via `HeadlessSync.WaitUntilAsync` or synchronous `Layout`; no wall-clock waits; `ManualUiClock`/`FakeTimeProvider` throughout. No `WaitUntilAsync` on a transient that can flip true early — asserts target end-state text/collection contents.
4. **Type consistency** — F# module `RailLayoutPolicy` vs result record `RailLayout`; C# consumes `RailLayoutPolicy.compute`, `RailRow.HostRow.Item`, `RailRow.GroupHeaderRow.{tier,count,expanded}`, `RailHost(Guid, RailTier)`, `RailLayoutInput(Hosts, AvailableHeight, RowHeight, Override, HealthyExpanded)`. Persisted enums `RailGroupingMode`/`AppTheme` map to F# `RailOverride`/tiers.
5. **No `HostMonitor`/`HostMachine` edits** — App-layer + pure `Lattice.App.Aggregation` only. ✔

