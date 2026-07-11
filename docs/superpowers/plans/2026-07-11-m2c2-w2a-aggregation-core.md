# M2c-2 Wave 2a — Shared Aggregation Core + CollectionReconciler + Tasks Retrofit

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** One pure F# aggregation core (host classification + row merge + keyed reconcile diff) consumed by all four data-view VMs, with `TasksViewModel` retrofitted onto it and the `SequenceEqual` full-replace guard removed (closes issue #24's Tasks leg).

**Architecture:** A dependency-free F# project (`Lattice.App.Aggregation`) holds two pure modules: `ViewSlice` (per-host facts → merged rows + coverage/staleness facts) and `Reconcile` (keyed diff producing imperative ops). A thin C# applier translates ops onto `ObservableCollection<RowHolder<,>>`, updating row holders in place so DataGrid selection identity survives value-change polls. Views bind to closed holder subclasses (XAML cannot name generic types).

**Tech Stack:** F# (net10.0, `--warnaserror`), FsCheck property tests (xunit v2 world), C# glue in Lattice.App, Avalonia 12 DataGrid.

**Authoritative sources:** issue #31 (scope), issue #24 (acceptance criteria), spec `docs/superpowers/specs/2026-07-10-m2c2-data-views-design.md` §Wave 2, design package `docs/design/m2/README.md`.

**Standing rules that bind every task:**
- Red-first: run the failing test and see it fail before implementing. Reviewer repeats falsification.
- `-warnaserror` clean, Debug + Release, on every commit.
- Nothing here touches `src/Lattice.Core/HostMonitor.cs` or `HostMachine.fs`. If a task somehow does, the verification sync rule (CLAUDE.md) applies in full — stop and escalate instead.
- Avalonia API questions go to the avalonia-docs MCP first (`https://docs-mcp.avaloniaui.net/mcp`), never guessed.
- Repo language: commits/comments in English.

---

## File structure

```
src/Lattice.App.Aggregation/            NEW — pure F#, zero project/package deps
├── Lattice.App.Aggregation.fsproj
├── Reconcile.fs                        ReconcileOp union + diff
└── ViewSlice.fs                        HostFacts / Slice records + compute

tests/Lattice.Aggregation.Tests/        NEW — F#, xunit 2.9.3 + FsCheck.Xunit
├── Lattice.Aggregation.Tests.fsproj
├── ReconcileTests.fs                   examples + apply-simulation + properties
└── ViewSliceTests.fs                   examples + properties

src/Lattice.App/
├── Infrastructure/RowHolder.cs         NEW — generic observable holder
├── Infrastructure/CollectionReconciler.cs  NEW — ops → ObservableCollection
├── ViewModels/TaskRow.cs               NEW — TaskRowKey + closed holder subclass
├── ViewModels/TaskRowViewModel.cs      MODIFY — gains HostId; From() takes hostId
├── ViewModels/TasksViewModel.cs        MODIFY — ViewSlice + Reconcile adoption
├── Views/TasksView.axaml               MODIFY — Data.* paths, x:DataType vm:TaskRow
└── Views/TasksView.axaml.cs            MODIFY — OnLoadingRow holder rebind

tests/Lattice.App.Tests/
├── TaskRowViewModelTests.cs            MODIFY — From() signature
├── TasksViewModelTests.cs              MODIFY — Rows[i].Data assertions + new regression tests
├── CollectionReconcilerTests.cs        NEW — event-log assertions (no Reset, identity reuse)
└── Headless/TasksViewTests.cs          MODIFY — selection-survival regression test
```

Order sensitivity in the fsproj: `Reconcile.fs` before `ViewSlice.fs` is arbitrary (no cross-references); keep that order for determinism.

---

### Task 1: Project scaffolding

**Files:**
- Create: `src/Lattice.App.Aggregation/Lattice.App.Aggregation.fsproj`
- Create: `tests/Lattice.Aggregation.Tests/Lattice.Aggregation.Tests.fsproj`
- Create: `src/Lattice.App.Aggregation/Reconcile.fs` (empty module shell)
- Create: `src/Lattice.App.Aggregation/ViewSlice.fs` (empty module shell)
- Create: `tests/Lattice.Aggregation.Tests/ReconcileTests.fs` (placeholder test)
- Create: `tests/Lattice.Aggregation.Tests/ViewSliceTests.fs` (empty)
- Modify: `Lattice.sln`, `src/Lattice.App/Lattice.App.csproj`

- [ ] **Step 1: Create the aggregation fsproj**

`src/Lattice.App.Aggregation/Lattice.App.Aggregation.fsproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <RootNamespace>Lattice.App.Aggregation</RootNamespace>
    <IsPackable>false</IsPackable>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <WarningLevel>5</WarningLevel>
    <OtherFlags>--warnaserror</OtherFlags>
  </PropertyGroup>

  <ItemGroup>
    <Compile Include="Reconcile.fs" />
    <Compile Include="ViewSlice.fs" />
  </ItemGroup>

</Project>
```

No `ProjectReference` items — this project is pure by charter. Adding one later is a design smell; the reviewer should reject it.

Module shells so the project compiles:

`Reconcile.fs`:
```fsharp
namespace Lattice.App.Aggregation

module Reconcile =
    /// Placeholder; replaced in Task 2.
    let internal placeholder = ()
```

`ViewSlice.fs`:
```fsharp
namespace Lattice.App.Aggregation

module ViewSlice =
    /// Placeholder; replaced in Task 3.
    let internal placeholder = ()
```

- [ ] **Step 2: Create the test fsproj**

`tests/Lattice.Aggregation.Tests/Lattice.Aggregation.Tests.fsproj` — mirror `tests/Lattice.Verification/Lattice.Verification.fsproj` (xunit 2.9.3, `xunit.runner.visualstudio` 3.1.5, `Microsoft.NET.Test.Sdk` 17.14.1) plus FsCheck:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>false</IsPackable>
    <WarningLevel>5</WarningLevel>
    <OtherFlags>--warnaserror</OtherFlags>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="ReconcileTests.fs" />
    <Compile Include="ViewSliceTests.fs" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5" />
    <PackageReference Include="FsCheck.Xunit" Version="2.16.6" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Lattice.App.Aggregation\Lattice.App.Aggregation.fsproj" />
  </ItemGroup>
</Project>
```

**Version check (do, don't assume):** `FsCheck.Xunit` 2.16.6 is the last v2-xunit-compatible stable known at plan time. Before committing, verify against nuget.org (`dotnet package search FsCheck.Xunit --exact-match`) that no newer 2.x exists and that the chosen version restores against xunit 2.9.3. Do NOT take FsCheck 3.x here: its xunit adapter targets xunit v3 and this project deliberately sits in the v2 world with Lattice.Tests/Lattice.Verification (issue #16 tracks unification).

`ReconcileTests.fs` placeholder so the runner finds one test:
```fsharp
module Lattice.Aggregation.Tests.ReconcileTests

open Xunit

[<Fact>]
let ``scaffolding compiles and runs`` () = Assert.True true
```

`ViewSliceTests.fs`:
```fsharp
module Lattice.Aggregation.Tests.ViewSliceTests
```

- [ ] **Step 3: Wire solution and App reference**

```bash
dotnet sln Lattice.sln add src/Lattice.App.Aggregation/Lattice.App.Aggregation.fsproj tests/Lattice.Aggregation.Tests/Lattice.Aggregation.Tests.fsproj
dotnet add src/Lattice.App/Lattice.App.csproj reference src/Lattice.App.Aggregation/Lattice.App.Aggregation.fsproj
```

Check `.github/workflows/` — if the CI test step enumerates test projects explicitly, add the new test project; if it runs `dotnet test` on the solution, nothing to do.

- [ ] **Step 4: Build + run the placeholder test**

Run: `dotnet build Lattice.sln -warnaserror && dotnet test tests/Lattice.Aggregation.Tests -v minimal`
Expected: build clean; 1 test passes.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "chore(aggregation): scaffold pure F# aggregation project + FsCheck test project"
```

---

### Task 2: `Reconcile.diff` — pure keyed diff

**Files:**
- Modify: `src/Lattice.App.Aggregation/Reconcile.fs`
- Modify: `tests/Lattice.Aggregation.Tests/ReconcileTests.fs`

**Contract.** `diff existing target` returns ops that, applied in order to a collection whose (key, row) content equals `existing`, transform it into exactly `target` — while never removing-and-reinserting a surviving key (that is the identity-preservation property #24 hinges on). Indices in each op refer to the collection state at the moment that op applies. Precondition: keys are unique within each array (guaranteed by construction upstream: task key = hostId + result name, unique per BOINC host; the applier debug-asserts it).

- [ ] **Step 1: Write example-based failing tests**

Replace `ReconcileTests.fs` content:

```fsharp
module Lattice.Aggregation.Tests.ReconcileTests

open Xunit
open FsCheck
open FsCheck.Xunit
open Lattice.App.Aggregation

// F#-side simulation of the C# applier: the executable meaning of the ops.
// CollectionReconcilerTests (C#) pins the real applier to these semantics.
// The union lives at namespace level (ReconcileOp), NOT inside module
// Reconcile — cases are used unqualified once the namespace is open.
let apply (existing: struct (int * string)[]) (ops: ReconcileOp<int, string> list) =
    let list = ResizeArray(existing |> Seq.map (fun struct (k, r) -> (k, r)))
    for op in ops do
        match op with
        | Update (i, _, row) -> list[i] <- (fst list[i], row)
        | Insert (i, key, row) -> list.Insert(i, (key, row))
        | RemoveAt (i, _) -> list.RemoveAt i
        | Move (fromIndex, toIndex, _) ->
            let item = list[fromIndex]
            list.RemoveAt fromIndex
            list.Insert(toIndex, item)
    list |> Seq.map (fun (k, r) -> struct (k, r)) |> Array.ofSeq

[<Fact>]
let ``identical input yields no ops`` () =
    let rows = [| struct (1, "a"); struct (2, "b") |]
    Assert.Empty(Reconcile.diff rows rows)

[<Fact>]
let ``value change on a surviving key is a single Update`` () =
    let ops = Reconcile.diff [| struct (1, "a"); struct (2, "b") |] [| struct (1, "a2"); struct (2, "b") |]
    Assert.Equal<ReconcileOp<int, string> list>([ Update(0, 1, "a2") ], ops)

[<Fact>]
let ``departed key is removed, new key inserted at position`` () =
    let ops = Reconcile.diff [| struct (1, "a") |] [| struct (2, "b") |]
    Assert.Equal<ReconcileOp<int, string> list>(
        [ RemoveAt(0, 1); Insert(0, 2, "b") ], ops)

[<Fact>]
let ``reorder uses Move, not remove plus insert`` () =
    let ops = Reconcile.diff [| struct (1, "a"); struct (2, "b") |] [| struct (2, "b"); struct (1, "a") |]
    Assert.Equal<ReconcileOp<int, string> list>([ Move(1, 0, 2) ], ops)
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Lattice.Aggregation.Tests -v minimal`
Expected: FAIL — `ReconcileOp` / `diff` not defined (compile error is the red here).

- [ ] **Step 3: Implement**

Replace `Reconcile.fs` content:

```fsharp
namespace Lattice.App.Aggregation

open System.Collections.Generic

/// One imperative edit for the applier to perform on the bound collection.
/// Indices refer to the collection state at the moment the op applies.
type ReconcileOp<'Key, 'Row> =
    /// Holder at Index keeps its identity; only its Data changes.
    | Update of Index: int * Key: 'Key * Row: 'Row
    | Insert of Index: int * Key: 'Key * Row: 'Row
    | RemoveAt of Index: int * Key: 'Key
    | Move of FromIndex: int * ToIndex: int * Key: 'Key

module Reconcile =
    /// Pure keyed diff. Precondition: keys unique within each array.
    /// Surviving keys are never removed+reinserted — identity preservation
    /// is the point (issue #24). Removals are emitted back-to-front so every
    /// emitted index is live when its op applies.
    let diff (existing: struct ('Key * 'Row)[]) (target: struct ('Key * 'Row)[]) : ReconcileOp<'Key, 'Row> list =
        let targetKeys = HashSet(target |> Seq.map (fun struct (k, _) -> k))
        let working = ResizeArray(existing |> Seq.map (fun struct (k, r) -> (k, r)))
        let ops = ResizeArray()

        for i in working.Count - 1 .. -1 .. 0 do
            let key = fst working[i]
            if not (targetKeys.Contains key) then
                ops.Add(RemoveAt(i, key))
                working.RemoveAt i

        target
        |> Array.iteri (fun i struct (key, row) ->
            if i < working.Count && fst working[i] = key then
                if snd working[i] <> row then
                    ops.Add(Update(i, key, row))
                    working[i] <- (key, row)
            else
                let mutable j = -1
                for candidate in i + 1 .. working.Count - 1 do
                    if j < 0 && fst working[candidate] = key then j <- candidate
                if j >= 0 then
                    ops.Add(Move(j, i, key))
                    let item = working[j]
                    working.RemoveAt j
                    working.Insert(i, item)
                    if snd item <> row then
                        ops.Add(Update(i, key, row))
                        working[i] <- (key, row)
                else
                    ops.Add(Insert(i, key, row))
                    working.Insert(i, (key, row)))

        List.ofSeq ops
```

Note `'Row` picks up F# structural equality via the `<>` uses; the fsproj's `--warnaserror` will surface the implied `equality` constraint in the signature — that constraint is intended (row records are value-equal by design).

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/Lattice.Aggregation.Tests -v minimal`
Expected: PASS (4 tests + scaffold).

- [ ] **Step 5: Add FsCheck properties (red-first: sabotage check)**

Append to `ReconcileTests.fs`:

```fsharp
// Generator: unique keys per array, small alphabet so key overlap is common.
let keyedRows =
    gen {
        let! keys = Gen.subListOf [ 0 .. 9 ]
        let! shuffled = Gen.shuffle keys
        let! rows = Gen.listOfLength shuffled.Length (Gen.elements [ "a"; "b"; "c" ])
        return Array.map2 (fun k r -> struct (k, r)) shuffled (Array.ofList rows)
    }

type ReconcileArbs =
    static member Pairs() =
        gen {
            let! before = keyedRows
            let! after = keyedRows
            return (before, after)
        }
        |> Arb.fromGen

[<Property(Arbitrary = [| typeof<ReconcileArbs> |])>]
let ``applying the diff reproduces the target exactly`` ((before, after)) =
    apply before (Reconcile.diff before after) = after

[<Property(Arbitrary = [| typeof<ReconcileArbs> |])>]
let ``a surviving key is never removed or inserted`` ((before, after)) =
    let survivors =
        HashSet(Set.intersect
            (before |> Seq.map (fun struct (k, _) -> k) |> Set.ofSeq)
            (after |> Seq.map (fun struct (k, _) -> k) |> Set.ofSeq))
    Reconcile.diff before after
    |> List.forall (function
        | Reconcile.RemoveAt (_, k) | Reconcile.Insert (_, k, _) -> not (survivors.Contains k)
        | Reconcile.Update _ | Reconcile.Move _ -> true)

[<Property(Arbitrary = [| typeof<ReconcileArbs> |])>]
let ``no-op diff for equal inputs`` ((before, _)) =
    Reconcile.diff before before |> List.isEmpty
```

Red-first for properties = mutation falsification: temporarily change `ops.Add(Move(j, i, key))` to emit `RemoveAt`+`Insert` instead, run, and confirm the survivor property FAILS; revert. Record the observed failure in the commit message body.

- [ ] **Step 6: Run tests**

Run: `dotnet test tests/Lattice.Aggregation.Tests -v minimal`
Expected: PASS (7 + scaffold).

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(aggregation): Reconcile.diff pure keyed diff with FsCheck identity properties"
```

---

### Task 3: `ViewSlice.compute` — host classification + merge

**Files:**
- Modify: `src/Lattice.App.Aggregation/ViewSlice.fs`
- Modify: `tests/Lattice.Aggregation.Tests/ViewSliceTests.fs`

**Contract.** Faithful extraction of the classification block of `TasksViewModel.Rebuild` (`src/Lattice.App/ViewModels/TasksViewModel.cs:134-215` pre-retrofit), preserving three Codex-adjudicated subtleties: (1) covered = in-scope ∧ Connected ∧ snapshotted — the exact set rows are built from; (2) unreachable tier spans ALL hosts regardless of scope (the partial-bar fingerprint episode advances even while a single-host scope hides the bar); (3) freshness = OLDEST in-scope row-source timestamp (pessimistic). Flags, not `RailState`: this project must not know UI types. The App layer projects `RailState` into the flags.

- [ ] **Step 1: Write failing tests**

Replace `ViewSliceTests.fs` content:

```fsharp
module Lattice.Aggregation.Tests.ViewSliceTests

open System
open Xunit
open FsCheck
open FsCheck.Xunit
open Lattice.App.Aggregation

let host id inScope rowSource unreachable stale ts rows =
    { Id = id
      InScope = inScope
      IsRowSource = rowSource
      IsUnreachableTier = unreachable
      IsStaleSignal = stale
      Timestamp = ts
      Rows = rows }

let idA = Guid.NewGuid()
let idB = Guid.NewGuid()
let t0 = DateTimeOffset(2026, 7, 11, 0, 0, 0, TimeSpan.Zero)

[<Fact>]
let ``rows merge from in-scope row sources only, in host order`` () =
    let slice =
        ViewSlice.compute
            [| host idA true true false false (Nullable t0) [| "a1"; "a2" |]
               host idB false true false false (Nullable t0) [| "b1" |] |]
    Assert.Equal<string[]>([| "a1"; "a2" |], slice.AllRows)
    Assert.Equal<Guid seq>([ idA ], Seq.sort slice.CoveredIds)

[<Fact>]
let ``unreachable tier spans out-of-scope hosts`` () =
    let slice =
        ViewSlice.compute
            [| host idA true true false false (Nullable t0) [||]
               host idB false false true false (Nullable()) [||] |]
    Assert.Contains(idB, slice.UnreachableIds)

[<Fact>]
let ``freshness is the oldest in-scope row-source timestamp`` () =
    let older = t0.AddSeconds -30.0
    let slice =
        ViewSlice.compute
            [| host idA true true false false (Nullable t0) [||]
               host idB true true false false (Nullable older) [||] |]
    Assert.Equal(Nullable older, slice.OldestTimestamp)

[<Fact>]
let ``stale iff any in-scope host signals stale`` () =
    let notStale =
        ViewSlice.compute [| host idA true true false false (Nullable t0) [||] |]
    Assert.False notStale.IsUpdateStale
    let stale =
        ViewSlice.compute
            [| host idA true true false false (Nullable t0) [||]
               host idB true false false true (Nullable()) [||] |]
    Assert.True stale.IsUpdateStale

[<Fact>]
let ``out-of-scope stale signal does not mark the view stale`` () =
    let slice =
        ViewSlice.compute
            [| host idA true true false false (Nullable t0) [||]
               host idB false false false true (Nullable()) [||] |]
    Assert.False slice.IsUpdateStale
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Lattice.Aggregation.Tests -v minimal`
Expected: FAIL — `HostFacts` / `compute` not defined.

- [ ] **Step 3: Implement**

Replace `ViewSlice.fs` content:

```fsharp
namespace Lattice.App.Aggregation

open System
open System.Collections.Generic

/// Per-host facts the App layer projects from HostStore + RailStateProjection.
/// Flags, not RailState: this project stays free of UI-layer types.
type HostFacts<'Row> =
    { Id: Guid
      /// Host is inside the current view scope (single host or all).
      InScope: bool
      /// Connected AND snapshotted — this host's rows are in the grid.
      IsRowSource: bool
      /// Unreachable or AuthFailed — the partial-bar "missing" tier.
      /// Evaluated over ALL hosts, scope-independent (episode semantics).
      IsUnreachableTier: bool
      /// Retrying or Unreachable — drives the stale-update indicator.
      IsStaleSignal: bool
      /// Snapshot timestamp when a snapshot exists.
      Timestamp: DateTimeOffset Nullable
      Rows: 'Row[] }

/// Everything a data-view VM derives from the host set before its
/// view-specific filter/sort.
type Slice<'Row> =
    { /// In-scope row-source hosts' rows, concatenated in host order.
      AllRows: 'Row[]
      /// Exactly the hosts AllRows came from (feeds the partial-bar fingerprint).
      CoveredIds: HashSet<Guid>
      /// Unreachable-tier hosts across ALL hosts, scope-independent.
      UnreachableIds: HashSet<Guid>
      /// Oldest in-scope row-source snapshot: the pessimistic freshness reading.
      OldestTimestamp: DateTimeOffset Nullable
      IsUpdateStale: bool }

module ViewSlice =
    /// Total and pure over the facts array.
    let compute (hosts: HostFacts<'Row>[]) : Slice<'Row> =
        let rowSources = hosts |> Array.filter (fun h -> h.InScope && h.IsRowSource)
        let timestamps = rowSources |> Array.choose (fun h -> Option.ofNullable h.Timestamp)
        { AllRows = rowSources |> Array.collect (fun h -> h.Rows)
          CoveredIds = HashSet(rowSources |> Seq.map (fun h -> h.Id))
          UnreachableIds =
            HashSet(hosts |> Seq.filter (fun h -> h.IsUnreachableTier) |> Seq.map (fun h -> h.Id))
          OldestTimestamp =
            if timestamps.Length = 0 then Nullable() else Nullable(Array.min timestamps)
          IsUpdateStale = hosts |> Array.exists (fun h -> h.InScope && h.IsStaleSignal) }
```

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/Lattice.Aggregation.Tests -v minimal`
Expected: PASS.

- [ ] **Step 5: Add properties**

Append to `ViewSliceTests.fs`:

```fsharp
let factsGen =
    gen {
        let! n = Gen.choose (0, 6)
        let! hosts =
            Gen.listOfLength n (gen {
                let! inScope = Arb.generate<bool>
                let! rowSource = Arb.generate<bool>
                let! unreachable = Arb.generate<bool>
                let! stale = Arb.generate<bool>
                let! seconds = Gen.choose (0, 1000)
                let! rowCount = Gen.choose (0, 3)
                let ts = if rowSource then Nullable(t0.AddSeconds(float seconds)) else Nullable()
                return host (Guid.NewGuid()) inScope rowSource unreachable stale ts
                           (Array.init rowCount (fun i -> $"r{i}"))
            })
        return Array.ofList hosts
    }

type SliceArbs =
    static member Facts() = Arb.fromGen factsGen

[<Property(Arbitrary = [| typeof<SliceArbs> |])>]
let ``row conservation: AllRows is exactly the in-scope row sources' rows`` (hosts: HostFacts<string>[]) =
    (ViewSlice.compute hosts).AllRows
    = (hosts |> Array.filter (fun h -> h.InScope && h.IsRowSource) |> Array.collect (fun h -> h.Rows))

[<Property(Arbitrary = [| typeof<SliceArbs> |])>]
let ``covered equals row-source ids; unreachable ignores scope`` (hosts: HostFacts<string>[]) =
    let slice = ViewSlice.compute hosts
    let covered = hosts |> Seq.filter (fun h -> h.InScope && h.IsRowSource) |> Seq.map (fun h -> h.Id) |> Set.ofSeq
    let unreachable = hosts |> Seq.filter (fun h -> h.IsUnreachableTier) |> Seq.map (fun h -> h.Id) |> Set.ofSeq
    Set.ofSeq slice.CoveredIds = covered && Set.ofSeq slice.UnreachableIds = unreachable
```

- [ ] **Step 6: Run tests, then commit**

Run: `dotnet test tests/Lattice.Aggregation.Tests -v minimal` — Expected: PASS.

```bash
git add -A
git commit -m "feat(aggregation): ViewSlice.compute host classification + merge with properties"
```

---

### Task 4: `RowHolder` + `CollectionReconciler` applier (C#)

**Files:**
- Create: `src/Lattice.App/Infrastructure/RowHolder.cs`
- Create: `src/Lattice.App/Infrastructure/CollectionReconciler.cs`
- Create: `tests/Lattice.App.Tests/CollectionReconcilerTests.cs`

- [ ] **Step 1: Write failing tests**

`tests/Lattice.App.Tests/CollectionReconcilerTests.cs`:

```csharp
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Lattice.App.Aggregation;
using Lattice.App.Infrastructure;

namespace Lattice.Tests;

public class CollectionReconcilerTests
{
    private static ObservableCollection<RowHolder<int, string>> Collection(params (int Key, string Row)[] items) =>
        new(items.Select(i => new RowHolder<int, string>(i.Key, i.Row)));

    private static void Reconcile(ObservableCollection<RowHolder<int, string>> rows, params (int Key, string Row)[] target)
    {
        var existing = rows.Select(h => ((int, string))(h.Key, h.Data)).ToArray();
        CollectionReconciler.Apply(rows, Reconcile.diff(existing, target.Select(t => ((int, string))t).ToArray()));
    }

    [Fact]
    public void Value_change_keeps_holder_identity_and_raises_no_collection_event()
    {
        var rows = Collection((1, "a"), (2, "b"));
        var holder = rows[0];
        var events = new List<NotifyCollectionChangedAction>();
        rows.CollectionChanged += (_, e) => events.Add(e.Action);

        Reconcile(rows, (1, "a2"), (2, "b"));

        Assert.Empty(events);
        Assert.Same(holder, rows[0]);
        Assert.Equal("a2", rows[0].Data);
    }

    [Fact]
    public void Reorder_moves_holders_without_reset()
    {
        var rows = Collection((1, "a"), (2, "b"));
        var first = rows[0];
        var second = rows[1];
        var events = new List<NotifyCollectionChangedAction>();
        rows.CollectionChanged += (_, e) => events.Add(e.Action);

        Reconcile(rows, (2, "b"), (1, "a"));

        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, events);
        Assert.Same(second, rows[0]);
        Assert.Same(first, rows[1]);
    }

    [Fact]
    public void Add_and_remove_touch_only_the_changed_identities()
    {
        var rows = Collection((1, "a"), (2, "b"));
        var survivor = rows[1];

        Reconcile(rows, (2, "b"), (3, "c"));

        Assert.Equal(2, rows.Count);
        Assert.Same(survivor, rows[0]);
        Assert.Equal(3, rows[1].Key);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Lattice.App.Tests --filter CollectionReconciler -v minimal`
Expected: FAIL — types not defined (compile error).

- [ ] **Step 3: Implement**

`src/Lattice.App/Infrastructure/RowHolder.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;

namespace Lattice.App.Infrastructure;

/// <summary>
/// Mutable identity wrapper around an immutable row record: the DataGrid binds
/// to holders, the reconciler swaps <see cref="Data"/> in place, so selection
/// (item identity) survives value-change polls. XAML cannot name generic types,
/// so each view binds a closed subclass (e.g. TaskRow).
/// </summary>
public partial class RowHolder<TKey, TRow> : ObservableObject
    where TKey : notnull
{
    public RowHolder(TKey key, TRow data)
    {
        Key = key;
        _data = data;
    }

    public TKey Key { get; }

    [ObservableProperty] private TRow _data;
}
```

`src/Lattice.App/Infrastructure/CollectionReconciler.cs`:

```csharp
using System.Collections.ObjectModel;
using System.Diagnostics;
using Lattice.App.Aggregation;
using Microsoft.FSharp.Collections;

namespace Lattice.App.Infrastructure;

/// <summary>
/// Applies Reconcile.diff ops to the bound collection. All decision logic is
/// in the F# diff; this class only translates ops into collection mutations.
/// Never raises Reset (Clear is never called).
/// </summary>
public static class CollectionReconciler
{
    public static void Apply<TKey, TRow>(
        ObservableCollection<RowHolder<TKey, TRow>> rows,
        FSharpList<ReconcileOp<TKey, TRow>> ops)
        where TKey : notnull
        => Apply(rows, ops, (key, row) => new RowHolder<TKey, TRow>(key, row));

    /// <summary>Overload for closed holder subclasses (XAML-bindable rows).</summary>
    public static void Apply<TKey, TRow>(
        ObservableCollection<RowHolder<TKey, TRow>> rows,
        FSharpList<ReconcileOp<TKey, TRow>> ops,
        Func<TKey, TRow, RowHolder<TKey, TRow>> createHolder)
        where TKey : notnull
    {
        foreach (var op in ops)
        {
            switch (op)
            {
                case ReconcileOp<TKey, TRow>.Update u:
                    Debug.Assert(EqualityComparer<TKey>.Default.Equals(rows[u.Index].Key, u.Key));
                    rows[u.Index].Data = u.Row;
                    break;
                case ReconcileOp<TKey, TRow>.Insert ins:
                    rows.Insert(ins.Index, createHolder(ins.Key, ins.Row));
                    break;
                case ReconcileOp<TKey, TRow>.RemoveAt rem:
                    Debug.Assert(EqualityComparer<TKey>.Default.Equals(rows[rem.Index].Key, rem.Key));
                    rows.RemoveAt(rem.Index);
                    break;
                case ReconcileOp<TKey, TRow>.Move mv:
                    rows.Move(mv.FromIndex, mv.ToIndex);
                    break;
            }
        }
    }
}
```

**Interop note for the implementer:** F# union cases compile to nested classes (`ReconcileOp<,>.Update` etc.) with the declared field names as properties (`Index`, `Key`, `Row`, `FromIndex`, `ToIndex`) — the same pattern the C# shell already uses to consume `HostMachine` commands (see `src/Lattice.Core/HostMonitor.cs` for precedent). If the C# pattern matching bites (e.g. singleton-vs-class case shapes), consult that precedent first.

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/Lattice.App.Tests --filter CollectionReconciler -v minimal`
Expected: PASS (3).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(app): RowHolder + CollectionReconciler applier over F# reconcile ops"
```

---

### Task 5: `TaskRowKey` + `HostId` on the row record

**Files:**
- Create: `src/Lattice.App/ViewModels/TaskRow.cs`
- Modify: `src/Lattice.App/ViewModels/TaskRowViewModel.cs`
- Modify: `src/Lattice.App/ViewModels/TasksViewModel.cs:146-147` (the `From` call gains `h.Config.Id`)
- Modify: `tests/Lattice.App.Tests/TaskRowViewModelTests.cs` (every `From(...)` call site)

- [ ] **Step 1: Write the failing test**

Append to `tests/Lattice.App.Tests/TaskRowViewModelTests.cs`:

```csharp
[Fact]
public void Key_is_host_id_plus_result_name()
{
    var hostId = Guid.NewGuid();
    var row = TaskRowViewModel.From(TestData.MakeTaskSnapshot(name: "wu_1"), hostId, "host-a");
    Assert.Equal(new TaskRowKey(hostId, "wu_1"), row.Key);
}
```

(Adapt the `TestData` factory name to what the file already uses for building `TaskSnapshot`s — the existing tests in this file show the pattern.)

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Lattice.App.Tests --filter TaskRowViewModel -v minimal`
Expected: FAIL — `TaskRowKey` undefined / `From` arity.

- [ ] **Step 3: Implement**

`src/Lattice.App/ViewModels/TaskRow.cs`:

```csharp
using Lattice.App.Infrastructure;

namespace Lattice.App.ViewModels;

/// <summary>Row identity in the Tasks grid: result names are unique per host.</summary>
public readonly record struct TaskRowKey(Guid HostId, string Name);

/// <summary>Closed holder type so XAML can use x:DataType (generics can't be named in XAML).</summary>
public sealed class TaskRow(TaskRowKey key, TaskRowViewModel data)
    : RowHolder<TaskRowKey, TaskRowViewModel>(key, data);
```

`TaskRowViewModel.cs` changes:
- add `Guid HostId` as the first record parameter;
- `From(TaskSnapshot snap, Guid hostId, string host)` — pass `HostId: hostId` in the record construction;
- add a computed key property: `public TaskRowKey Key => new(HostId, Name);`.

- [ ] **Step 4: Fix all `From` call sites, run the full App test project**

Run: `dotnet test tests/Lattice.App.Tests -v minimal`
Expected: PASS (compilation drives you to every call site; `TasksViewModel.cs:146-147` becomes `TaskRowViewModel.From(t, h.Config.Id, h.Config.DisplayName)`).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(app): TaskRowKey + HostId on task rows (reconciler identity)"
```

---

### Task 6: `TasksViewModel` retrofit

**Files:**
- Modify: `src/Lattice.App/ViewModels/TasksViewModel.cs`
- Modify: `tests/Lattice.App.Tests/TasksViewModelTests.cs`

- [ ] **Step 1: Write the two acceptance regression tests (red)**

Append to `TasksViewModelTests.cs` (mirror the existing steady-state test at ~line 570 for fixture setup):

```csharp
[Fact]
public async Task Progress_change_updates_row_in_place_without_collection_events()
{
    double fraction = 0.25;
    var fake = new FakeGuiRpcClient
    {
        OnGetResults = _ => Task.FromResult<IReadOnlyList<Result>>(
            [TestData.MakeResult(name: "wu_1", fractionDone: fraction)]),
    };
    AddHost("host-a", fake);
    var vm = MakeVm();
    _manager.Start();
    await Wait.UntilAsync(() => vm.Rows.Count == 1);

    var holder = vm.Rows[0];
    var events = 0;
    vm.Rows.CollectionChanged += (_, _) => events++;

    fraction = 0.50; // next poll reports progress
    _store.RequestRefresh(null);
    await Wait.UntilAsync(() => vm.Rows[0].Data.PercentText == "50%");

    Assert.Equal(0, events);              // no Reset, no Remove+Add — issue #24
    Assert.Same(holder, vm.Rows[0]);      // selection identity survives
}

[Fact]
public async Task Departed_task_is_removed_without_reset()
{
    IReadOnlyList<Result> results = [TestData.MakeResult(name: "wu_1"), TestData.MakeResult(name: "wu_2")];
    var fake = new FakeGuiRpcClient { OnGetResults = _ => Task.FromResult(results) };
    AddHost("host-a", fake);
    var vm = MakeVm();
    _manager.Start();
    await Wait.UntilAsync(() => vm.Rows.Count == 2);

    var actions = new List<NotifyCollectionChangedAction>();
    vm.Rows.CollectionChanged += (_, e) => actions.Add(e.Action);

    results = [TestData.MakeResult(name: "wu_2")];
    _store.RequestRefresh(null);
    await Wait.UntilAsync(() => vm.Rows.Count == 1);

    Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, actions);
    Assert.Equal("wu_2", vm.Rows[0].Data.Name);
}
```

Adapt fake/`TestData` parameter names to the existing fixture idiom in this file (settle on expected text / observed calls per the determinism canon — `Wait.UntilAsync` on the expected end state, never on transient booleans). The first test fails against current code because `Rows` is rebuilt wholesale (`Assert.Same` fails and/or events fire); the second may partially pass — run and record which assertions are red.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Lattice.App.Tests --filter TasksViewModel -v minimal`
Expected: the two new tests FAIL (plus compile errors from `Rows[0].Data` until the type changes — that's fine, red includes not-compiling).

- [ ] **Step 3: Retrofit the VM**

In `TasksViewModel.cs`:

1. `Rows` becomes `public ObservableCollection<TaskRow> Rows { get; } = [];`
2. Replace the body of `Rebuild()`'s classification + replace-guard sections (keep filter/sort/counts/overlay logic):

```csharp
private void Rebuild()
{
    IsAllHostsScope = Scope.IsAllHosts;

    var facts = _store.Hosts.Select(h =>
    {
        var rail = RailStateProjection.From(h.Status);
        var inScope = Scope.IsAllHosts || h.Config.Id == Scope.HostId;
        var isRowSource = rail == RailState.Connected && h.Snapshot is not null;
        return new HostFacts<TaskRowViewModel>(
            id: h.Config.Id,
            inScope: inScope,
            isRowSource: isRowSource,
            isUnreachableTier: rail is RailState.Unreachable or RailState.AuthFailed,
            isStaleSignal: rail is RailState.Retrying or RailState.Unreachable,
            timestamp: h.Snapshot?.Timestamp ?? default(DateTimeOffset?),
            rows: isRowSource
                ? h.Snapshot!.Tasks.Select(t => TaskRowViewModel.From(t, h.Config.Id, h.Config.DisplayName)).ToArray()
                : []);
    }).ToArray();

    var slice = ViewSlice.compute(facts);
    var allRows = slice.AllRows;

    var target = allRows
        .Where(MatchesFilters)
        .OrderBy(r => r.Deadline is null)
        .ThenBy(r => r.Deadline)
        .Select(r => (r.Key, r))
        .ToArray();

    // Keyed reconcile instead of replace: in-place Data updates keep holder
    // identity (DataGrid selection) and steady-state polls raise no
    // CollectionChanged at all (issue #24).
    var existing = Rows.Select(h => (h.Key, h.Data)).ToArray();
    CollectionReconciler.Apply(Rows, Reconcile.diff(existing, target),
        (key, row) => new TaskRow(key, row));

    // ... counts/at-risk/polling blocks unchanged, but read from the slice:
    //   timestamps.Min()            → slice.OldestTimestamp
    //   IsUpdateStale computation   → slice.IsUpdateStale
    //   unreachableIds              → slice.UnreachableIds
    //   coveredIds                  → slice.CoveredIds
    // PartialBarPolicy / TasksOverlayPolicy calls stay exactly as they are —
    // the fingerprint takes (slice.UnreachableIds, slice.CoveredIds).
    ...
}
```

The `...` above is not a placeholder to invent — it is the existing lines `TasksViewModel.cs:169-233` kept verbatim, with only the four reads redirected to the slice as annotated (the scoped/overlay facts for `TasksOverlayPolicy` still come from the in-scope host entries: keep a `var scoped = ...` list for that and `LoadingText`). Delete the old `reachable`/`allRows`/`rows`/`SequenceEqual` block (`:136-165`) and the `unreachableIds`/`coveredIds`/`timestamps` LINQ (`:179-206`) that the slice now answers.

C#→F# record construction: F# records expose a full-arity constructor; named arguments as shown compile if the F# field order matches — otherwise fall back to positional. Struct-tuple arrays (`(TaskRowKey, TaskRowViewModel)[]`) satisfy F#'s `struct ('K * 'R)[]` — if the compiler disagrees, construct `ValueTuple.Create(...)` explicitly.

3. Delete nothing else: filter properties, commands, persistence, dispose stay untouched.

- [ ] **Step 4: Run the full App suite; fix the fallout mechanically**

Run: `dotnet test tests/Lattice.App.Tests -v minimal`
Existing tests referencing `vm.Rows[i].<recordProp>` become `vm.Rows[i].Data.<recordProp>`; the ~line-584 identity test's `Assert.Same(firstRow, vm.Rows[0])` still holds (holder identity). Expected: PASS including the two new regression tests.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(app): TasksViewModel rides ViewSlice + reconciler; SequenceEqual replace-guard removed

Closes the Tasks leg of #24: in-place Data updates, no CollectionChanged on
steady-state polls, holder identity preserved across value-change polls."
```

---

### Task 7: `TasksView` binding retarget + row-class liveness

**Files:**
- Modify: `src/Lattice.App/Views/TasksView.axaml`
- Modify: `src/Lattice.App/Views/TasksView.axaml.cs`
- Modify: `tests/Lattice.App.Tests/Headless/TasksViewTests.cs`

**The row-class staleness trap (this task's real content):** `OnLoadingRow` (`TasksView.axaml.cs:197-202`) sets `atRisk`/`suspended` row classes from the row's values. Pre-retrofit, any value change produced a NEW row object → the DataGrid re-ran LoadingRow. Post-retrofit the holder mutates in place and LoadingRow does NOT re-fire — the classes would go stale. Fix: track the holder's `PropertyChanged` while its row is loaded.

- [ ] **Step 1: Write the failing headless test**

Append to `Headless/TasksViewTests.cs` (follow the file's existing fixture idiom for constructing the view + VM and pumping layout):

```csharp
[AvaloniaFact]
public async Task Row_going_at_risk_in_place_updates_row_class()
{
    // Arrange a grid with one non-at-risk row, find its DataGridRow,
    // then mutate Data to an at-risk record and assert the class flips.
    // Uses the same view+fake plumbing as the existing tests in this file.
    var (view, vm) = await ShowTasksViewWithSingleRow(atRisk: false);
    var row = VisualTree.FindRow(view.Grid, index: 0);
    Assert.False(row.Classes.Contains("atRisk"));

    vm.Rows[0].Data = vm.Rows[0].Data with { IsDeadlineAtRisk = true };
    await HeadlessSync.WaitUntilAsync(() => row.Classes.Contains("atRisk"));
}
```

`ShowTasksViewWithSingleRow` / `VisualTree.FindRow`: reuse or extract from this file's existing helpers — the file already renders grids and locates rows for its column-visibility and geometry assertions; do not invent a parallel fixture. If no row-locating helper exists yet, add one to `Headless/VisualTree.cs`.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Lattice.App.Tests --filter Row_going_at_risk -v minimal`
Expected: FAIL (compile error on `Data` first; after the XAML/code-behind edits below compile, the class never flips → `WaitUntilAsync` times out). Two-stage red is acceptable here; record both.

- [ ] **Step 3: Retarget XAML bindings**

In `TasksView.axaml`:
- Every cell `DataTemplate x:DataType="vm:TaskRowViewModel"` → `x:DataType="vm:TaskRow"`, and inner bindings gain the `Data.` prefix (`{Binding Name}` → `{Binding Data.Name}`, `{Binding Fraction, Converter=...}` → `{Binding Data.Fraction, Converter=...}`, `Classes.suspended="{Binding IsSuspended}"` → `="{Binding Data.IsSuspended}"`, `Classes.atRiskText="{Binding IsDeadlineAtRisk}"` → `="{Binding Data.IsDeadlineAtRisk}"`, tooltip on Name likewise).
- Every `DataGridTextColumn Binding="{Binding X}"` → `Binding="{Binding Data.X}"` (Project, Application, Elapsed, Remaining, Deadline-text, State-text, Host columns — see the full column list at `TasksView.axaml:134-190`).
- **Sorting check (do first, not after):** `CanUserSortColumns="True"` + nested binding paths — confirm via avalonia-docs MCP that Avalonia's DataGrid derives a working sort comparer from a `Data.X` binding path; if nested paths don't sort, set an explicit `SortMemberPath="Data.X"` per column, and if THAT doesn't support nesting either, set `CustomSortComparer` per the docs. Do not ship with sorting silently broken — `Headless/DataGridInfraTests.cs` has the sorting coverage to extend: add one test that sorts by a `Data.`-bound column and asserts row order.

- [ ] **Step 4: Rework `OnLoadingRow` + unloading**

In `TasksView.axaml.cs` replace `OnLoadingRow` and wire `UnloadingRow`:

```csharp
private readonly Dictionary<DataGridRow, (TaskRow Holder, PropertyChangedEventHandler Handler)> _rowSubscriptions = new();

private void OnLoadingRow(object? sender, DataGridRowEventArgs e)
{
    if (e.Row.DataContext is not TaskRow holder) return;
    ApplyRowClasses(e.Row, holder.Data);
    PropertyChangedEventHandler handler = (_, args) =>
    {
        if (args.PropertyName == nameof(TaskRow.Data))
            ApplyRowClasses(e.Row, holder.Data);
    };
    holder.PropertyChanged += handler;
    _rowSubscriptions[e.Row] = (holder, handler);
}

private void OnUnloadingRow(object? sender, DataGridRowEventArgs e)
{
    if (_rowSubscriptions.Remove(e.Row, out var sub))
        sub.Holder.PropertyChanged -= sub.Handler;
}

private static void ApplyRowClasses(DataGridRow row, TaskRowViewModel data)
{
    row.Classes.Set("atRisk", data.IsDeadlineAtRisk);
    row.Classes.Set("suspended", data.IsSuspended);
}
```

Wire `UnloadingRow="OnUnloadingRow"` next to the existing `LoadingRow` hookup (XAML attribute or code-behind `+=`, matching how `LoadingRow` is attached today). Row recycling means a recycled `DataGridRow` gets a fresh `LoadingRow` for its new item — the dictionary overwrite in `OnLoadingRow` must unsubscribe any previous entry first:

```csharp
if (_rowSubscriptions.Remove(e.Row, out var stale))
    stale.Holder.PropertyChanged -= stale.Handler;
```

(put this at the top of `OnLoadingRow`).

- [ ] **Step 5: Run headless + full App suite**

Run: `dotnet test tests/Lattice.App.Tests -v minimal`
Expected: PASS, including the new row-class test and the extended sorting test.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(app): TasksView binds row holders; row classes track in-place Data updates"
```

---

### Task 8: Full-suite verification + wrap-up

**Files:** none new.

- [ ] **Step 1: Full suite, both configurations**

Run: `dotnet build Lattice.sln -c Release -warnaserror && dotnet test Lattice.sln -c Release -v minimal && dotnet test Lattice.sln -v minimal`
Expected: all projects green (was 494 + new aggregation tests), zero warnings.

- [ ] **Step 2: Journey sanity**

Run: `dotnet test tests/Lattice.App.Tests --filter Journeys -v minimal`
Expected: PASS — journeys settle on expected text, so the reconciler swap should be invisible to them; a journey failure here means observable behavior changed and is a real finding, not a test to patch.

- [ ] **Step 3: Grep for leftovers**

Run: `grep -rn "SequenceEqual" src/Lattice.App/`
Expected: no hits in TasksViewModel (the replace-guard is gone).

- [ ] **Step 4: Commit any stragglers, hand back to controller**

Controller (not the executor) then: push branch `m2c2-w2a-aggregation-core`, open the PR (body cites issue #31 + "#24 Tasks leg" acceptance evidence), trigger `@codex review`, pr-monitor, merge on clean per standing cadence.

---

## Explicitly out of scope (Wave 2b–d PRs, planned separately)

- Projects/Transfers/Event-log views (each stamps from Tasks; each gets its own plan citing this core).
- `HostStore` consumption of `MessagesAdded` (Event-log PR).
- Closing issue #24 outright — it closes when the LAST view consumes the reconciler; the PR for this plan comments progress on it instead.
- Any `Lattice.Core`/`HostMonitor` change (none expected; verification sync rule applies if violated).

## Self-review notes (already applied)

- Spec coverage: issue #24 acceptance ↔ Task 2 (pure diff, unit-tested), Task 6 (in-place updates, no Reset, guard removed, regression tests), Task 7 (selection-adjacent row-class liveness + sorting check). ViewSlice semantics ↔ the three Codex-adjudicated subtleties, restated in Task 3's contract.
- Known judgment calls an executor must not "fix" silently: unreachable-tier spans all hosts while other facts are scope-gated (deliberate, preserves shipped fingerprint episodes); F# stays dependency-free; xunit v2 for the new test project (issue #16 owns unification).
- Sort-with-nested-paths is the one open API risk; Task 7 front-loads it with a mandated docs lookup + test instead of leaving it to QA.
