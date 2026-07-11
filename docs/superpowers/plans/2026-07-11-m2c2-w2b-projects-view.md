# M2c-2 Wave 2b — Projects View (hierarchical DataGrid)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The Projects data view: two-level hierarchy (aggregate parent per project MasterUrl, per-host child rows), Varies/Mixed aggregation tiers, stamped from the Tasks pattern onto the shared aggregation core.

**Architecture:** A pure F# `ProjectRows` module (in the existing `Lattice.App.Aggregation`) groups in-scope attachments by MasterUrl and computes the parent-row aggregates (share summary, three-tier status summary, credit sums). The C# `ProjectsViewModel` follows `TasksViewModel` exactly — `HostFacts` → `ViewSlice.compute` → view-specific shaping → `Reconcile.diff` + `CollectionReconciler` — with the hierarchy realized as a **flattened DataGrid**: parent and child rows live in one flat `Rows` collection, expansion state is VM-owned, and the chevron inserts/removes child rows through the reconciler. (Decision settled 2026-07-11 with the owner: flattened DataGrid over Expander list / TreeDataGrid — reuses the entire Wave-1/2a grid machinery. `DataGridColumn.CanUserSort` / `SortMemberPath` / `CustomSortComparer` confirmed present in Avalonia.Controls.DataGrid 12.1.0; column-header sorting is disabled grid-wide here because a flat sort would destroy the hierarchy — default sort is aggregate RAC descending per the design.)

**Tech Stack:** F# (net10.0, `--warnaserror`) + FsCheck 2.16.6 (xunit v2), C# / Avalonia 12 DataGrid 12.1.0, CommunityToolkit.Mvvm.

**Authoritative sources:** issue #31 (scope), spec `docs/superpowers/specs/2026-07-10-m2c2-data-views-design.md` §Wave 2, design package `docs/design/m2/README.md` §"Projects view (2a)" + §"Data model" (credit-field mapping). The design package wins over this plan when they conflict — cite the design line when deviating.


**Sequencing:** Blocked by the w2a2 scaffold-extraction PR (`2026-07-11-m2c2-w2a2-view-scaffold-extraction.md`) — `RowClassBinder` / `ViewSliceProjection` / `PartialBarState` must be on main before this branch starts; this plan consumes them instead of transcribing the Tasks pattern (PR #42 round-1 root cause).

**Standing rules that bind every task:**
- Red-first: run the failing test and see it fail before implementing. Reviewer repeats falsification.
- `-warnaserror` clean, Debug + Release, on every commit.
- Nothing here touches `src/Lattice.Core/HostMonitor.cs` or `HostMachine.fs`; if a task somehow does, the verification sync rule (CLAUDE.md) applies — stop and escalate.
- F# style canon (CLAUDE.md) applies; idiom review is a blocking review step. The F# below was typechecked and smoke-run via `dotnet fsi` at plan time — transcribe it verbatim.
- Avalonia API questions go to the avalonia-docs MCP, never guessed.
- Conflict isolation (parallel Wave-2 worktrees): this PR owns only its View/ViewModel/test files; shared touch points (`ShellViewModel.Views[1]`, one DataTemplate block in `ShellWindow.axaml`, resx keys prefixed `Projects*`) are additive one-liners; rebase on main before opening the PR.
- Repo language: commits/comments in English.

---

## File structure

```
src/Lattice.App.Aggregation/
└── ProjectRows.fs                      NEW — attachment records + grouping/summaries (+ fsproj Compile entry, after ViewSlice.fs)

tests/Lattice.Aggregation.Tests/
└── ProjectRowsTests.fs                 NEW — examples + FsCheck properties (+ fsproj Compile entry)

src/Lattice.App/
├── ViewModels/ProjectRow.cs            NEW — ProjectRowKey-keyed closed holder + row record
├── ViewModels/ProjectsViewModel.cs     NEW — stamped from TasksViewModel.cs
├── Views/ProjectsView.axaml            NEW — stamped from TasksView.axaml
├── Views/ProjectsView.axaml.cs         NEW — row-class liveness (stamped from TasksView.axaml.cs)
├── ViewModels/ShellViewModel.cs        MODIFY — Views[1] swap + scope push (2 lines)
├── Views/ShellWindow.axaml             MODIFY — one DataTemplate block
└── Localization/Strings.resx           MODIFY — Projects* keys

tests/Lattice.App.Tests/
├── ProjectRowViewModelTests.cs         NEW
├── ProjectsViewModelTests.cs           NEW
├── Headless/ProjectsViewTests.cs       NEW — incl. 40/32px row-height geometry assert
└── Headless/Journeys/ProjectsScopeJourney.cs  NEW
```

---

### Task 1: F# `ProjectRows` — grouping + aggregation tiers

**Files:**
- Create: `src/Lattice.App.Aggregation/ProjectRows.fs`
- Modify: `src/Lattice.App.Aggregation/Lattice.App.Aggregation.fsproj` (add `<Compile Include="ProjectRows.fs" />` AFTER ViewSlice.fs)
- Create: `tests/Lattice.Aggregation.Tests/ProjectRowsTests.fs`
- Modify: `tests/Lattice.Aggregation.Tests/Lattice.Aggregation.Tests.fsproj` (add Compile entry)

- [ ] **Step 1: Write example-based failing tests**

`tests/Lattice.Aggregation.Tests/ProjectRowsTests.fs`:

```fsharp
module Lattice.Aggregation.Tests.ProjectRowsTests

open System
open Xunit
open FsCheck
open FsCheck.Xunit
open Lattice.App.Aggregation

let hostA = Guid.NewGuid()
let hostB = Guid.NewGuid()

let att url name host hostId susp noNew share rac total tasks =
    { MasterUrl = url; ProjectName = name; HostId = hostId; HostName = host
      TaskCount = tasks; ResourceShare = share; AvgCredit = rac; TotalCredit = total
      IsSuspended = susp; NoNewTasks = noNew }

[<Fact>]
let ``groups by MasterUrl and sorts by aggregate RAC descending`` () =
    let groups =
        ProjectRows.compute
            [| att "u1" "P1" "a" hostA false false 100.0 5.0 10.0 1
               att "u1" "P1" "b" hostB false false 100.0 9.0 10.0 2
               att "u2" "P2" "a" hostA false false 100.0 1.0 10.0 3 |]
    Assert.Equal(2, groups.Length)
    Assert.Equal("u1", groups[0].MasterUrl)     // 14 RAC before 1
    Assert.Equal(14.0, groups[0].AvgCredit)
    Assert.Equal(20.0, groups[0].TotalCredit)   // Host* sums, never User* (design §Data model)
    Assert.Equal(3, groups[0].TaskCount)

[<Fact>]
let ``uniform share collapses, differing share varies with min-max`` () =
    let uniform = ProjectRows.compute [| att "u" "P" "a" hostA false false 100.0 1.0 1.0 0
                                         att "u" "P" "b" hostB false false 100.0 1.0 1.0 0 |]
    Assert.Equal(UniformShare 100.0, uniform[0].Share)
    let varies = ProjectRows.compute [| att "u" "P" "a" hostA false false 50.0 1.0 1.0 0
                                        att "u" "P" "b" hostB false false 100.0 1.0 1.0 0 |]
    Assert.Equal(VariesShare (50.0, 100.0), varies[0].Share)

[<Fact>]
let ``status tiers: all-same, one deviation, mixed`` () =
    let same = ProjectRows.compute [| att "u" "P" "a" hostA false false 1.0 1.0 1.0 0 |]
    Assert.Equal(AllSame Active, same[0].Status)
    let dev = ProjectRows.compute [| att "u" "P" "a" hostA true false 1.0 1.0 1.0 0
                                     att "u" "P" "b" hostB false false 1.0 1.0 1.0 0 |]
    Assert.Equal(OneDeviation (Suspended, 1, 2), dev[0].Status)
    let mixed = ProjectRows.compute [| att "u" "P" "a" hostA true false 1.0 1.0 1.0 0
                                       att "u" "P" "b" hostB false true 1.0 1.0 1.0 0 |]
    Assert.Equal(MixedStatus (1, 1), mixed[0].Status)

[<Fact>]
let ``suspended wins when both flags set; display name falls back to url`` () =
    let g = ProjectRows.compute [| att "u" "" "a" hostA true true 1.0 1.0 1.0 0 |]
    Assert.Equal(AllSame Suspended, g[0].Status)
    Assert.Equal("u", g[0].DisplayName)
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Lattice.Aggregation.Tests -v minimal`
Expected: FAIL — `ProjectAttachment` / `ProjectRows` not defined (compile error is the red).

- [ ] **Step 3: Implement**

`src/Lattice.App.Aggregation/ProjectRows.fs` (typechecked + smoke-run via dotnet fsi at plan time — transcribe verbatim):

```fsharp
namespace Lattice.App.Aggregation

open System

/// One host's attachment of one project. The App layer projects
/// (ProjectSnapshot, host identity) into this — no GuiRpc types here.
type ProjectAttachment =
    { MasterUrl: string
      ProjectName: string
      HostId: Guid
      HostName: string
      TaskCount: int
      ResourceShare: float
      /// HostExpavgCredit (RAC) — per-host, never account-level User* (design §Data model).
      AvgCredit: float
      /// HostTotalCredit — per-host, never account-level User*.
      TotalCredit: float
      IsSuspended: bool
      NoNewTasks: bool }

/// Per-host status, derived (suspended wins when both flags are set).
type AttachmentStatus =
    | Active
    | Suspended
    | NoNewTasks

/// Parent-row status aggregate, the three design tiers (design 2a):
/// all same / one deviating kind / mixed deviations.
type StatusSummary =
    | AllSame of AttachmentStatus
    | OneDeviation of status: AttachmentStatus * deviants: int * total: int
    | MixedStatus of suspended: int * noNewTasks: int

type ShareSummary =
    | UniformShare of float
    | VariesShare of min: float * max: float

/// One project aggregated across its in-scope host attachments.
type ProjectGroup =
    { MasterUrl: string
      DisplayName: string
      /// Sorted by host name (stable child order).
      Attachments: ProjectAttachment[]
      Share: ShareSummary
      Status: StatusSummary
      AvgCredit: float
      TotalCredit: float
      TaskCount: int }

module ProjectRows =
    let status (a: ProjectAttachment) : AttachmentStatus =
        if a.IsSuspended then Suspended
        elif a.NoNewTasks then NoNewTasks
        else Active

    /// Summarize per-host statuses into the three design tiers. Operates on the
    /// (suspended, noNew) counts — active is the remainder. NOTE: DU-case totality
    /// is enforced upstream in `status`, not here (counting is by value-equality,
    /// which the exhaustiveness checker cannot see). The tuple-match below is
    /// total over int × int with no DU wildcard.
    let internal summarize (statuses: AttachmentStatus[]) : StatusSummary =
        let count s = statuses |> Array.filter (fun x -> x = s) |> Array.length
        let suspended = count Suspended
        let noNew = count NoNewTasks
        let total = statuses.Length
        match suspended, noNew with
        | 0, 0 -> AllSame Active
        | s, 0 when s = total -> AllSame Suspended
        | 0, n when n = total -> AllSame NoNewTasks
        | s, 0 -> OneDeviation(Suspended, s, total)
        | 0, n -> OneDeviation(NoNewTasks, n, total)
        | s, n -> MixedStatus(s, n)

    let internal shareSummary (shares: float[]) : ShareSummary =
        let lo = Array.min shares
        let hi = Array.max shares
        if lo = hi then UniformShare lo else VariesShare(lo, hi)

    /// Groups in-scope attachments by MasterUrl (design 2a). Attachments sort
    /// by host name; groups sort by aggregate RAC descending (default sort).
    /// Precondition: caller passes in-scope attachments only (ViewSlice's
    /// AllRows), so single-host scope degrades naturally (no Varies, no
    /// children to show).
    let compute (attachments: ProjectAttachment[]) : ProjectGroup[] =
        attachments
        |> Array.groupBy (fun a -> a.MasterUrl)
        |> Array.map (fun (url, atts) ->
            let sorted = atts |> Array.sortBy (fun a -> a.HostName, a.HostId)
            { MasterUrl = url
              DisplayName =
                sorted
                |> Array.tryPick (fun a -> if a.ProjectName = "" then None else Some a.ProjectName)
                |> Option.defaultValue url
              Attachments = sorted
              Share = shareSummary (sorted |> Array.map (fun a -> a.ResourceShare))
              Status = summarize (sorted |> Array.map status)
              AvgCredit = sorted |> Array.sumBy (fun a -> a.AvgCredit)
              TotalCredit = sorted |> Array.sumBy (fun a -> a.TotalCredit)
              TaskCount = sorted |> Array.sumBy (fun a -> a.TaskCount) })
        |> Array.sortByDescending (fun g -> g.AvgCredit)

/// Row identity in the Projects grid: parent per MasterUrl, child per
/// (MasterUrl, host). DU structural equality is the reconciler key equality.
type ProjectRowKey =
    | ParentKey of masterUrl: string
    | ChildKey of masterUrl: string * hostId: Guid
```

fsproj: `<Compile Include="ProjectRows.fs" />` goes after `ViewSlice.fs` (no cross-references; keep deterministic order).

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/Lattice.Aggregation.Tests -v minimal`
Expected: PASS (4 new + existing).

- [ ] **Step 5: Add FsCheck properties (red-first via mutation falsification)**

Append to `ProjectRowsTests.fs`:

```fsharp
let attachmentsGen =
    gen {
        let! n = Gen.choose (0, 8)
        let hostIds = [| hostA; hostB; Guid.NewGuid() |]
        let! atts =
            Gen.listOfLength n (gen {
                let! url = Gen.elements [ "u1"; "u2"; "u3" ]
                let! hostIdx = Gen.choose (0, 2)
                let! susp = Arb.generate<bool>
                let! noNew = Arb.generate<bool>
                let! share = Gen.elements [ 50.0; 100.0; 200.0 ]
                let! rac = Gen.choose (0, 100)
                return att url "P" (string hostIdx) hostIds[hostIdx] susp noNew share (float rac) 1.0 1
            })
        // Key uniqueness precondition: one attachment per (url, host).
        return atts |> List.distinctBy (fun a -> a.MasterUrl, a.HostId) |> Array.ofList
    }

type ProjectArbs =
    static member Attachments() = Arb.fromGen attachmentsGen

[<Property(Arbitrary = [| typeof<ProjectArbs> |])>]
let ``attachment conservation: groups partition the input`` (atts: ProjectAttachment[]) =
    let groups = ProjectRows.compute atts
    let regrouped = groups |> Array.collect (fun g -> g.Attachments) |> Array.sortBy (fun a -> a.MasterUrl, a.HostId)
    regrouped = (atts |> Array.sortBy (fun a -> a.MasterUrl, a.HostId))

[<Property(Arbitrary = [| typeof<ProjectArbs> |])>]
let ``credit sums are per-group host sums`` (atts: ProjectAttachment[]) =
    ProjectRows.compute atts
    |> Array.forall (fun g ->
        g.AvgCredit = (g.Attachments |> Array.sumBy (fun a -> a.AvgCredit))
        && g.TotalCredit = (g.Attachments |> Array.sumBy (fun a -> a.TotalCredit)))

[<Property(Arbitrary = [| typeof<ProjectArbs> |])>]
let ``status summary counts are consistent with attachments`` (atts: ProjectAttachment[]) =
    ProjectRows.compute atts
    |> Array.forall (fun g ->
        let statuses = g.Attachments |> Array.map ProjectRows.status
        let suspended = statuses |> Array.filter (fun s -> s = Suspended) |> Array.length
        let noNew = statuses |> Array.filter (fun s -> s = NoNewTasks) |> Array.length
        match g.Status with
        | AllSame s -> statuses |> Array.forall (fun x -> x = s)
        | OneDeviation (Suspended, d, t) -> d = suspended && t = statuses.Length && noNew = 0
        | OneDeviation (NoNewTasks, d, t) -> d = noNew && t = statuses.Length && suspended = 0
        | OneDeviation (Active, _, _) -> false // Active is never the deviation
        | MixedStatus (s, n) -> s = suspended && n = noNew && suspended > 0 && noNew > 0)
```

Mutation falsification: temporarily change `summarize`'s `| s, 0 -> OneDeviation(Suspended, s, total)` arm to return `MixedStatus(s, 0)`, run, confirm the third property FAILS, revert. Record the observed failure in the commit body.

- [ ] **Step 6: Run tests, commit**

Run: `dotnet test tests/Lattice.Aggregation.Tests -v minimal` — Expected: PASS.

```bash
git add -A
git commit -m "feat(aggregation): ProjectRows grouping + share/status aggregation tiers"
```

---

### Task 2: `ProjectRow` holder + row record

**Files:**
- Create: `src/Lattice.App/ViewModels/ProjectRow.cs`
- Create: `tests/Lattice.App.Tests/ProjectRowViewModelTests.cs`

- [ ] **Step 1: Write failing tests**

`tests/Lattice.App.Tests/ProjectRowViewModelTests.cs`:

```csharp
using Lattice.App.Aggregation;
using Lattice.App.ViewModels;

namespace Lattice.Tests;

public class ProjectRowViewModelTests
{
    private static ProjectAttachment Att(
        string url = "http://p1/", string name = "P1", string host = "host-a",
        Guid? hostId = null, bool susp = false, bool noNew = false,
        double share = 100, double rac = 1234.567, double total = 99999.9, int tasks = 3) =>
        new(url, name, hostId ?? Guid.NewGuid(), host, tasks, share, rac, total, susp, noNew);

    [Fact]
    public void Parent_row_renders_aggregates()
    {
        var g = ProjectRows.compute([Att(rac: 100.4), Att(host: "host-b", rac: 100.4)])[0];
        var row = ProjectRowViewModel.Parent(g, isAllHostsScope: true);

        Assert.True(row.IsParent);
        Assert.Equal("P1", row.Name);
        Assert.Equal("http://p1/", row.MasterUrl);
        Assert.Equal("2", row.HostsText);
        Assert.Equal("201", row.AvgCreditText);        // sum, rounded, invariant
        Assert.Equal(ProjectRowKey.NewParentKey("http://p1/"), row.Key); // DU structural equality
    }

    [Fact]
    public void Varies_share_renders_range_on_parent_and_bars_on_children_only()
    {
        var g = ProjectRows.compute([Att(share: 50), Att(host: "host-b", share: 100)])[0];
        var parent = ProjectRowViewModel.Parent(g, isAllHostsScope: true);
        Assert.Equal("Varies · 50–100", parent.ShareText);
        Assert.False(parent.ShowShareBar);

        var child = ProjectRowViewModel.Child(g, g.Attachments[0]);
        Assert.True(child.ShowShareBar);
        Assert.False(child.IsParent);
        Assert.Equal("host-a", child.Name);
    }

    [Fact]
    public void Status_tiers_render_per_design()
    {
        var same = ProjectRows.compute([Att()])[0];
        Assert.Equal("Active on all hosts",
            ProjectRowViewModel.Parent(same, true).StatusText);

        var dev = ProjectRows.compute([Att(susp: true), Att(host: "host-b")])[0];
        Assert.Equal("Suspended · 1/2 hosts",
            ProjectRowViewModel.Parent(dev, true).StatusText);

        var mixed = ProjectRows.compute([Att(susp: true), Att(host: "host-b", noNew: true)])[0];
        Assert.Equal("Mixed · 1 suspended · 1 no new tasks",
            ProjectRowViewModel.Parent(mixed, true).StatusText);
    }
}
```

(Exact literals come from the resx values added in Task 5 — the formats are `ProjectsStatusActiveAll` = "Active on all hosts", `ProjectsStatusDeviationFmt` = "{0} · {1}/{2} hosts", `ProjectsStatusMixedFmt` = "Mixed · {0} suspended · {1} no new tasks", `ProjectsShareVariesFmt` = "Varies · {0}–{1}". Add the resx keys in THIS task if you want these tests green before Task 5; the LocalizationTests suite pins key existence either way.)

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Lattice.App.Tests --filter ProjectRowViewModel -v minimal`
Expected: FAIL — types not defined.

- [ ] **Step 3: Implement**

`src/Lattice.App/ViewModels/ProjectRow.cs`:

```csharp
using System.Globalization;
using Lattice.App.Aggregation;
using Lattice.App.Infrastructure;
using Lattice.App.Localization;

namespace Lattice.App.ViewModels;

/// <summary>Closed holder so XAML can use x:DataType (generics can't be named in XAML).</summary>
public sealed class ProjectRow(ProjectRowKey key, ProjectRowViewModel data)
    : RowHolder<ProjectRowKey, ProjectRowViewModel>(key, data);

/// <summary>
/// Immutable row projection for the Projects grid — one record type for both
/// hierarchy levels (parent aggregate / per-host child), discriminated by
/// <see cref="IsParent"/>; cell templates gate on it with IsVisible.
/// </summary>
public sealed record ProjectRowViewModel(
    ProjectRowKey Key,
    string MasterUrl,          // group URL on both levels (chevron CommandParameter)
    bool IsParent,
    bool IsExpanded,
    bool ShowChevron,
    string Name,               // parent: project display name; child: host name
    string HostsText,          // parent only ("2"); "" on children
    string ShareText,
    bool ShowShareBar,
    double ShareFraction,      // 0..1 of the group's max share, for the mini bar
    string AvgCreditText,
    string TotalCreditText,
    string TasksText,          // child rows: "3 tasks"; parent: ""
    ProjectStatusKind StatusKind,
    string StatusText)
{
    public static ProjectRowViewModel Parent(ProjectGroup g, bool isAllHostsScope)
    {
        var (shareText, showBar, shareFraction) = g.Share switch
        {
            ShareSummary.UniformShare u => (Num(u.Item), true, 1.0),
            ShareSummary.VariesShare v => (
                string.Format(Strings.ProjectsShareVariesFmt, Num(v.min), Num(v.max)), false, 0.0),
            _ => throw new InvalidOperationException("unreachable: closed DU"),
        };
        return new(
            Key: ProjectRowKey.NewParentKey(g.MasterUrl),
            MasterUrl: g.MasterUrl,
            IsParent: true,
            IsExpanded: false, // set by the VM after expansion lookup
            ShowChevron: isAllHostsScope && g.Attachments.Length > 0,
            Name: g.DisplayName,
            HostsText: g.Attachments.Length.ToString(CultureInfo.InvariantCulture),
            ShareText: shareText,
            ShowShareBar: showBar,
            ShareFraction: shareFraction,
            AvgCreditText: Num(g.AvgCredit),
            TotalCreditText: Num(g.TotalCredit),
            TasksText: "",
            StatusKind: StatusKindOf(g.Status),
            StatusText: StatusTextOf(g.Status));
    }

    public static ProjectRowViewModel Child(ProjectGroup g, ProjectAttachment a)
    {
        var maxShare = g.Attachments.Max(x => x.ResourceShare);
        var status = ProjectRows.status(a);
        return new(
            Key: ProjectRowKey.NewChildKey(g.MasterUrl, a.HostId),
            MasterUrl: g.MasterUrl,
            IsParent: false,
            IsExpanded: false,
            ShowChevron: false,
            Name: a.HostName,
            HostsText: "",
            ShareText: Num(a.ResourceShare),
            ShowShareBar: true,
            ShareFraction: maxShare > 0 ? a.ResourceShare / maxShare : 0.0,
            AvgCreditText: Num(a.AvgCredit),
            TotalCreditText: Num(a.TotalCredit),
            TasksText: string.Format(Strings.ProjectsTaskCountFmt, a.TaskCount),
            StatusKind: KindOf(status),
            StatusText: TextOf(status));
    }

    // Credits and shares render as whole numbers (design 2a mock);
    // invariant culture per the repo culture rule.
    private static string Num(double v) => Math.Round(v).ToString(CultureInfo.InvariantCulture);

    private static ProjectStatusKind KindOf(AttachmentStatus s) =>
        s.Tag switch
        {
            AttachmentStatus.Tags.Active => ProjectStatusKind.Active,
            AttachmentStatus.Tags.Suspended => ProjectStatusKind.Suspended,
            AttachmentStatus.Tags.NoNewTasks => ProjectStatusKind.NoNewTasks,
            _ => throw new InvalidOperationException("unreachable: closed DU"),
        };

    private static string TextOf(AttachmentStatus s) =>
        KindOf(s) switch
        {
            ProjectStatusKind.Active => Strings.ProjectsStatusActive,
            ProjectStatusKind.Suspended => Strings.ProjectsStatusSuspended,
            ProjectStatusKind.NoNewTasks => Strings.ProjectsStatusNoNewTasks,
            ProjectStatusKind.Mixed => throw new InvalidOperationException("per-host status is never Mixed"),
            _ => throw new InvalidOperationException("unreachable"),
        };

    private static ProjectStatusKind StatusKindOf(StatusSummary s) => s switch
    {
        StatusSummary.AllSame a => KindOf(a.Item),
        StatusSummary.OneDeviation d => KindOf(d.status),
        StatusSummary.MixedStatus => ProjectStatusKind.Mixed,
        _ => throw new InvalidOperationException("unreachable: closed DU"),
    };

    private static string StatusTextOf(StatusSummary s) => s switch
    {
        StatusSummary.AllSame a when KindOf(a.Item) == ProjectStatusKind.Active =>
            Strings.ProjectsStatusActiveAll,
        StatusSummary.AllSame a =>
            string.Format(Strings.ProjectsStatusAllFmt, TextOf(a.Item)),
        StatusSummary.OneDeviation d =>
            string.Format(Strings.ProjectsStatusDeviationFmt, TextOf(d.status), d.deviants, d.total),
        StatusSummary.MixedStatus m =>
            string.Format(Strings.ProjectsStatusMixedFmt, m.suspended, m.noNewTasks),
        _ => throw new InvalidOperationException("unreachable: closed DU"),
    };
}

/// <summary>Drives the status icon choice in XAML (icon+text, no pills — design 2a).</summary>
public enum ProjectStatusKind
{
    Active,
    Suspended,
    NoNewTasks,
    Mixed,
}
```

**F#-interop notes (w2a lessons, verify against `CollectionReconcilerTests.cs` precedent):**
- DU case construction from C#: `ProjectRowKey.NewParentKey(url)` / `NewParentKey` static factories; case tests via C# pattern matching on the nested case classes (`s is StatusSummary.MixedStatus`).
- If `switch` on DU case classes trips over exhaustiveness, the `.Tag` + `Tags` constants pattern shown for `AttachmentStatus` is the fallback (both appear above deliberately so the implementer sees both forms).
- The `_ =>` arms are C# switch requirements, not F# wildcards; the `throw` keeps them honest.

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/Lattice.App.Tests --filter ProjectRowViewModel -v minimal`
Expected: PASS (3). If resx keys were deferred to Task 5, expected compile failure — in that case add the resx keys now (see Task 5 Step 1 for the exact list) and rerun.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(app): ProjectRow holder + row record with hierarchy rendering"
```

---

### Task 3: `ProjectsViewModel`

**Files:**
- Create: `src/Lattice.App/ViewModels/ProjectsViewModel.cs`
- Create: `tests/Lattice.App.Tests/ProjectsViewModelTests.cs`

**Stamp source:** `src/Lattice.App/ViewModels/TasksViewModel.cs` — same ctor triple `(HostStore, IUiClock, UiStateStore)`, same `Scope` push contract, same facts-projection (including the `inScope && isRowSource` row-materialization gate — Codex P2, PR #37), same PartialBarPolicy fingerprint + TasksOverlayPolicy blocks. Differences: rows come from `ProjectRows.compute` + hierarchy flattening; expansion state; no text/state filter (design 2a has none); no column-visibility persistence (fixed column set); density toggle kept.

- [ ] **Step 1: Write failing tests** — mirror `TasksViewModelTests.cs` fixture idiom (FakeGuiRpcClient + HostStore + QueueUiDispatcher; settle with `Wait.UntilAsync` on expected end states, never transient booleans). The behaviors to pin, each its own test:

```csharp
// 1. Two hosts, same MasterUrl → one parent row; expanding inserts two child
//    rows under it (order: parent, child-a, child-b) via reconciler (no Reset).
// 2. Single-host scope → no chevron, no children even after Toggle, Hosts column
//    fact: rows have ShowChevron == false.
// 3. Collapse removes exactly the children (parent holder identity preserved —
//    Assert.Same on the parent holder across toggle).
// 4. Steady-state poll with unchanged data raises zero CollectionChanged events.
// 5. Default order: parents by AvgCredit descending.
// 6. IsEmpty when connected with zero projects; IsLoading per TasksOverlayPolicy
//    (stamp the corresponding TasksViewModelTests cases).
```

Write all six as real tests now (adapt the arrange/act helpers from `TasksViewModelTests.cs`; the fake's `OnGetState`/snapshot path already carries `ProjectSnapshot`s — see `HostSnapshot.Projects`). Example shape for (1):

```csharp
[Fact]
public async Task Same_master_url_aggregates_and_expands_to_children()
{
    AddHost("host-a", FakeWithProject("http://p/", "P", rac: 10));
    AddHost("host-b", FakeWithProject("http://p/", "P", rac: 5));
    var vm = MakeVm();
    _manager.Start();
    await Wait.UntilAsync(() => vm.Rows.Count == 1);

    var parent = vm.Rows[0];
    vm.ToggleExpandCommand.Execute(((ProjectRowKey.ParentKey)parent.Key).masterUrl);
    await Wait.UntilAsync(() => vm.Rows.Count == 3);

    Assert.Same(parent, vm.Rows[0]);
    Assert.False(vm.Rows[1].Data.IsParent);
    Assert.Equal("host-a", vm.Rows[1].Data.Name);  // children sorted by host name
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Lattice.App.Tests --filter ProjectsViewModel -v minimal`
Expected: FAIL (compile error — VM type missing).

- [ ] **Step 3: Implement**

`src/Lattice.App/ViewModels/ProjectsViewModel.cs` — stamp `TasksViewModel.cs` with these substitutions (everything not mentioned stays structurally identical to the stamp source, including the comments that explain the Codex-adjudicated subtleties):

```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lattice.App.Aggregation;
using Lattice.App.Infrastructure;
using Lattice.App.Localization;

namespace Lattice.App.ViewModels;

/// <summary>
/// Scopes, groups and aggregates projects across one or all hosts for the
/// Projects view's hierarchical (flattened) DataGrid. Same shape as
/// TasksViewModel: (HostStore, IUiClock, UiStateStore), Scope pushed by
/// ShellViewModel, ViewSlice + Reconcile pipeline.
/// </summary>
public sealed partial class ProjectsViewModel : ObservableObject, IDisposable
{
    private readonly HostStore _store;
    private readonly IUiClock _clock;
    private readonly UiStateStore _uiStateStore;
    private UiState _uiState;
    private ScopeSelection _scope = ScopeSelection.AllHosts;

    // Expansion is per project URL, session-local (not persisted — a monitoring
    // dashboard reopens collapsed; revisit only if users ask).
    private readonly HashSet<string> _expanded = [];

    private readonly PartialBarState _partialBar = new();

    public ProjectsViewModel(HostStore store, IUiClock clock, UiStateStore uiStateStore)
    {
        _store = store;
        _clock = clock;
        _uiStateStore = uiStateStore;
        _uiState = uiStateStore.Load();
        _isCompact = _uiState.CompactDensity;
        store.Changed += OnStoreChanged;
        clock.Tick += OnTick;
        Rebuild();
    }

    public ObservableCollection<RowHolder<ProjectRowKey, ProjectRowViewModel>> Rows { get; } = [];

    public ScopeSelection Scope
    {
        get => _scope;
        set
        {
            if (_scope.Equals(value)) return;
            _scope = value;
            Rebuild();
        }
    }

    [ObservableProperty] private bool _isAllHostsScope;
    [ObservableProperty] private string _countsText = "";
    [ObservableProperty] private string _pollingText = "";
    [ObservableProperty] private string _updatedText = "";
    [ObservableProperty] private bool _isUpdateStale;
    [ObservableProperty] private bool _showPartialBar;
    [ObservableProperty] private string _partialBarText = "";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private string _loadingText = "";
    [ObservableProperty] private bool _isCompact;

    partial void OnIsCompactChanged(bool value)
    {
        _uiState = _uiState with { CompactDensity = value };
        _uiStateStore.Save(_uiState); // best-effort, as in TasksViewModel
    }

    [RelayCommand]
    private void Refresh() => _store.RequestRefresh(Scope.HostId);

    [RelayCommand]
    private void DismissPartial()
    {
        _partialBar.Dismiss();
        ShowPartialBar = false;
    }

    /// <summary>Chevron toggle; parameter is the group's MasterUrl.</summary>
    [RelayCommand]
    private void ToggleExpand(string masterUrl)
    {
        if (!_expanded.Remove(masterUrl))
            _expanded.Add(masterUrl);
        Rebuild();
    }

    private void OnStoreChanged(object? sender, EventArgs e) => Rebuild();
    private void OnTick(object? sender, EventArgs e) => Rebuild();

    private void Rebuild()
    {
        var scoped = Scope.IsAllHosts
            ? _store.Hosts
            : _store.Hosts.Where(h => h.Config.Id == Scope.HostId).ToList();
        IsAllHostsScope = Scope.IsAllHosts;

        // Facts projection (incl. the inScope && isRowSource gate) lives once
        // in ViewSliceProjection (w2a2) — this callback only shapes rows.
        var slice = ViewSliceProjection.Compute(_store.Hosts, Scope,
            h => h.Snapshot!.Projects.Select(p => new ProjectAttachment(
                p.Project.MasterUrl, p.Project.ProjectName,
                h.Config.Id, h.Config.DisplayName, p.TaskCount,
                p.Project.ResourceShare,
                p.Project.HostExpavgCredit, p.Project.HostTotalCredit,
                p.Project.SuspendedViaGui, p.Project.DontRequestMoreWork)).ToArray());

        var groups = ProjectRows.compute(slice.AllRows);

        // Hierarchy flattening is a trivial projection (grouping/aggregation —
        // the decision logic — lives in F#); children render only in the
        // All-hosts scope (design 2a: single-host hides child rows).
        var target = groups.SelectMany(g =>
        {
            var expanded = IsAllHostsScope && _expanded.Contains(g.MasterUrl);
            var parent = ProjectRowViewModel.Parent(g, IsAllHostsScope) with { IsExpanded = expanded };
            IEnumerable<ProjectRowViewModel> rows = expanded
                ? [parent, .. g.Attachments.Select(a => ProjectRowViewModel.Child(g, a))]
                : [parent];
            return rows;
        }).Select(r => (r.Key, r)).ToArray();

        var existing = Rows.Select(h => (h.Key, h.Data)).ToArray();
        CollectionReconciler.Apply(Rows, Reconcile.diff(existing, target),
            (key, row) => new ProjectRow(key, row));

        CountsText = string.Format(Strings.ProjectsCountsFmt, groups.Length, slice.CoveredIds.Count);
        PollingText = string.Format(Strings.PollingFmt, _store.PollingIntervalSeconds);
        UpdatedText = slice.OldestTimestamp is { } oldest ? TimeText.UpdatedAgo(oldest, _clock.Now) : "";
        IsUpdateStale = slice.IsUpdateStale;

        ShowPartialBar = _partialBar.Advance(slice.UnreachableIds, slice.CoveredIds, Scope.IsAllHosts);
        if (ShowPartialBar)
        {
            PartialBarText = string.Format(
                Strings.PartialFmt, slice.UnreachableIds.Count, _store.Hosts.Count, slice.CoveredIds.Count);
        }

        (IsLoading, IsEmpty) = TasksOverlayPolicy.Decide(
            [.. scoped.Select(h => new TasksOverlayPolicy.HostFacts(
                RailStateProjection.From(h.Status), h.Snapshot is not null))],
            groups.Length > 0);
        LoadingText = IsLoading
            ? string.Format(Strings.LoadingFromFmt, string.Join(", ", scoped.Select(h => h.Config.DisplayName)))
            : "";
    }

    public void Dispose()
    {
        _store.Changed -= OnStoreChanged;
        _clock.Tick -= OnTick;
    }
}
```

Interop reminders: F# record positional ctor uses camelCase parameter names — positional construction as shown is the reliable path (w2a lesson). `ProjectRowKey` DU values are structurally equal — safe reconciler keys. Tuple casts `((ProjectRowKey, ProjectRowViewModel))` feed `Reconcile.diff`'s `struct ('K * 'R)[]` — if the compiler objects, use `ValueTuple.Create`.

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/Lattice.App.Tests --filter ProjectsViewModel -v minimal`
Expected: PASS (all six).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(app): ProjectsViewModel — hierarchy over shared aggregation core"
```

---

### Task 4: `ProjectsView` (XAML + row-class liveness)

**Files:**
- Create: `src/Lattice.App/Views/ProjectsView.axaml`
- Create: `src/Lattice.App/Views/ProjectsView.axaml.cs`
- Create: `tests/Lattice.App.Tests/Headless/ProjectsViewTests.cs`

**Stamp source:** `src/Lattice.App/Views/TasksView.axaml` (+ `.axaml.cs`). Keep: command-bar Border layout, F5 KeyBinding, FAInfoBar partial bar, StatusBarControl, loading/empty overlay Panels, `LoadingRow`/`OnUnloadingRow` subscription-liveness pattern (including the recycled-row unsubscribe at the top of OnLoadingRow). Drop: filter TextBox, state ComboBox, column-visibility overflow menu (fixed columns here). Grid: `CanUserSortColumns="False"` (hierarchy — see Architecture).

- [ ] **Step 1: Write the failing headless tests**

`tests/Lattice.App.Tests/Headless/ProjectsViewTests.cs`, following `Headless/TasksViewTests.cs` fixture idiom (`VisualTree` helpers, `HeadlessSync.WaitUntilAsync`, settle on expected text):

```csharp
// 1. Parent and child rows get their classes: DataGridRow.Classes contains
//    "projectParent" (index 0) / "projectChild" (index 1) after expansion.
// 2. Geometry: parent row Bounds.Height == 40, child row Bounds.Height == 32
//    (design 2a row heights; the M2c-1 lesson — visual claims need pixel
//    probes, not faith).
// 3. Chevron click toggles: click the ToggleButton in row 0, expect row count
//    change and class flip of IsExpanded-bound chevron rotation class.
// 4. In-place status change flips the row's status cell text without
//    LoadingRow re-firing (mutate holder.Data — the Tasks Task-7 regression
//    stamped here).
// 5. Single-host scope hides the Hosts column (header-text lookup, the
//    identical-Strings-symbol invariant — stamp the Tasks Host-column test;
//    design 2a; Codex P2, round 2).
```

Write all four as real tests. For (2):

```csharp
[AvaloniaFact]
public async Task Parent_and_child_rows_render_design_heights()
{
    var (view, vm) = await ShowProjectsViewWithTwoHostProject();
    vm.ToggleExpandCommand.Execute("http://p/");
    await HeadlessSync.WaitUntilAsync(() => vm.Rows.Count == 3);
    view.UpdateLayout();

    var parent = VisualTree.FindRow(view.Grid, 0);
    var child = VisualTree.FindRow(view.Grid, 1);
    Assert.Equal(40, parent.Bounds.Height, precision: 0);
    Assert.Equal(32, child.Bounds.Height, precision: 0);
}
```

(`VisualTree.FindRow` exists for the Tasks tests; reuse. If per-row `Height` via style turns out not to be honored by the DataGrid's row measurement — the one API risk this plan carries — the fallback is setting `e.Row.Height` directly in `OnLoadingRow` from the row class; verify against avalonia-docs MCP + DataGrid 12.1.0 source before inventing anything else. Either way THIS geometry test is the acceptance gate.)

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Lattice.App.Tests --filter ProjectsView -v minimal`
Expected: FAIL (view type missing).

- [ ] **Step 3: Implement the XAML**

`ProjectsView.axaml` — stamped skeleton with the Projects columns (design 2a: chevron 24 · Project 200 · Hosts 110 · Resource share 140 · Avg credit 100 · Total credit 110 · Status *):

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:ui="using:FluentAvalonia.UI.Controls"
             xmlns:vm="using:Lattice.App.ViewModels"
             xmlns:views="using:Lattice.App.Views"
             xmlns:controls="using:Lattice.App.Controls"
             xmlns:loc="using:Lattice.App.Localization"
             x:Class="Lattice.App.Views.ProjectsView"
             x:DataType="vm:ProjectsViewModel">

  <UserControl.KeyBindings>
    <KeyBinding Gesture="F5" Command="{Binding RefreshCommand}" />
  </UserControl.KeyBindings>

  <UserControl.Styles>
    <!-- Hierarchy row heights (design 2a: 40px parent / 32px child). -->
    <Style Selector="DataGridRow.projectParent">
      <Setter Property="Height" Value="40" />
    </Style>
    <Style Selector="DataGridRow.projectChild">
      <Setter Property="Height" Value="32" />
    </Style>
    <Style Selector="DataGridRow.projectParent TextBlock.projectName">
      <Setter Property="FontWeight" Value="SemiBold" />
      <Setter Property="FontSize" Value="14" />
    </Style>
    <Style Selector="DataGridRow.projectChild TextBlock">
      <Setter Property="FontSize" Value="12" />
    </Style>
  </UserControl.Styles>

  <DockPanel>
    <!-- Command bar: title + disabled M3 placeholders + updated/refresh/density
         (stamp TasksView's right-side StackPanel verbatim; no filter/overflow). -->
    <Border DockPanel.Dock="Top" Height="{StaticResource LatticeCommandBarHeight}"
            Background="{DynamicResource LatticeSurfaceBrush}"
            BorderBrush="{DynamicResource LatticeStrokeSubtleBrush}"
            BorderThickness="0,0,0,1">
      <DockPanel Margin="16,0" LastChildFill="False">
        <StackPanel DockPanel.Dock="Left" Orientation="Horizontal" Spacing="8" VerticalAlignment="Center">
          <TextBlock Text="{x:Static loc:Strings.ProjectsTitle}" FontSize="16" FontWeight="SemiBold"
                     Foreground="{DynamicResource LatticeTextPrimaryBrush}" VerticalAlignment="Center" />
          <Button Content="{x:Static loc:Strings.ProjectsUpdate}" IsEnabled="False" Margin="8,0,0,0" />
          <Button Content="{x:Static loc:Strings.Suspend}" IsEnabled="False" />
          <Button Content="{x:Static loc:Strings.ProjectsNoNewTasks}" IsEnabled="False" />
        </StackPanel>
        <!-- right side: stamp TasksView.axaml:69-110 minus the overflow button -->
      </DockPanel>
    </Border>

    <ui:FAInfoBar DockPanel.Dock="Top" Severity="Warning" IsOpen="{Binding ShowPartialBar}"
                  Title="{x:Static loc:Strings.PartialTitle}" Message="{Binding PartialBarText}"
                  Margin="16,8,16,0" Closed="OnPartialBarClosed">
      <ui:FAInfoBar.ActionButton>
        <Button Content="{x:Static loc:Strings.RetryNow}" Command="{Binding RefreshCommand}" />
      </ui:FAInfoBar.ActionButton>
    </ui:FAInfoBar>

    <controls:StatusBarControl DockPanel.Dock="Bottom"
                                LeftText="{Binding CountsText}"
                                RightText="{Binding PollingText}" />

    <Panel>
      <DataGrid x:Name="Grid" x:FieldModifier="public" Classes="lattice" Classes.compact="{Binding IsCompact}"
                ItemsSource="{Binding Rows}" IsReadOnly="True" CanUserSortColumns="False">
        <DataGrid.Columns>
          <!-- Chevron 24 -->
          <DataGridTemplateColumn Width="24">
            <DataGridTemplateColumn.CellTemplate>
              <DataTemplate x:DataType="vm:ProjectRow">
                <ToggleButton Classes="chevron" Background="Transparent" Padding="2"
                              IsVisible="{Binding Data.ShowChevron}"
                              IsChecked="{Binding Data.IsExpanded, Mode=OneWay}"
                              Command="{Binding $parent[UserControl].((vm:ProjectsViewModel)DataContext).ToggleExpandCommand}"
                              CommandParameter="{Binding Data.MasterUrl}">
                  <PathIcon Width="10" Height="10" Data="{StaticResource IconChevronRightRegular}" />
                </ToggleButton>
              </DataTemplate>
            </DataGridTemplateColumn.CellTemplate>
          </DataGridTemplateColumn>
          <!-- Project 200 (parent: name; child: host name, indented) -->
          <DataGridTemplateColumn Header="{x:Static loc:Strings.ColProject}" Width="200">
            <DataGridTemplateColumn.CellTemplate>
              <DataTemplate x:DataType="vm:ProjectRow">
                <StackPanel Orientation="Horizontal" VerticalAlignment="Center" Spacing="6">
                  <TextBlock Classes="projectName" Text="{Binding Data.Name}"
                             TextTrimming="CharacterEllipsis" ToolTip.Tip="{Binding Data.Name}" />
                  <TextBlock Text="{Binding Data.TasksText}" FontSize="12"
                             Foreground="{DynamicResource LatticeTextSecondaryBrush}" />
                </StackPanel>
              </DataTemplate>
            </DataGridTemplateColumn.CellTemplate>
          </DataGridTemplateColumn>
          <!-- Hidden in single-host scope (design 2a) — same treatment as the
               Tasks Host column; the single-host headless test asserts it. -->
          <DataGridTextColumn Header="{x:Static loc:Strings.ProjectsColHosts}"
                               Binding="{Binding Data.HostsText}" Width="110"
                               IsVisible="{Binding IsAllHostsScope}" />
          <!-- Resource share 140: bar (when ShowShareBar) + text -->
          <DataGridTemplateColumn Header="{x:Static loc:Strings.ProjectsColShare}" Width="140">
            <DataGridTemplateColumn.CellTemplate>
              <DataTemplate x:DataType="vm:ProjectRow">
                <StackPanel Orientation="Horizontal" Spacing="8" VerticalAlignment="Center">
                  <!-- Track is 56px because FractionToWidthConverter hard-codes a
                       56px track (TaskGridConverters.cs) — do not shrink the track
                       without parameterizing the converter (Codex P2, round 2). -->
                  <Border Width="56" Height="3" CornerRadius="2" Background="{DynamicResource LatticeStrokeBrush}"
                          IsVisible="{Binding Data.ShowShareBar}">
                    <Border Background="{DynamicResource LatticeAccentBrush}" HorizontalAlignment="Left"
                            Height="3" CornerRadius="2"
                            Width="{Binding Data.ShareFraction, Converter={x:Static views:FractionToWidthConverter.Instance}}" />
                  </Border>
                  <TextBlock Text="{Binding Data.ShareText}" FontSize="12" Classes="numeric" />
                </StackPanel>
              </DataTemplate>
            </DataGridTemplateColumn.CellTemplate>
          </DataGridTemplateColumn>
          <DataGridTextColumn Header="{x:Static loc:Strings.ProjectsColAvgCredit}"
                               Binding="{Binding Data.AvgCreditText}" Width="100" />
          <DataGridTextColumn Header="{x:Static loc:Strings.ProjectsColTotalCredit}"
                               Binding="{Binding Data.TotalCreditText}" Width="110" />
          <!-- Status *: icon + text, no pills (explicit design decision) -->
          <DataGridTemplateColumn Header="{x:Static loc:Strings.ColState}" Width="*">
            <DataGridTemplateColumn.CellTemplate>
              <DataTemplate x:DataType="vm:ProjectRow">
                <StackPanel Orientation="Horizontal" Spacing="4" VerticalAlignment="Center">
                  <Panel Width="12" Height="12">
                    <PathIcon Width="12" Height="12" Data="{StaticResource IconCheckmarkCircleRegular}"
                              Foreground="{DynamicResource LatticeSuccessBrush}"
                              IsVisible="{Binding Data.StatusKind, Converter={x:Static views:EnumMatchConverter.Instance}, ConverterParameter=Active}" />
                    <PathIcon Width="12" Height="12" Data="{StaticResource IconPauseRegular}"
                              Foreground="{DynamicResource LatticeNeutralFgBrush}"
                              IsVisible="{Binding Data.StatusKind, Converter={x:Static views:EnumMatchConverter.Instance}, ConverterParameter=Suspended}" />
                    <PathIcon Width="12" Height="12" Data="{StaticResource IconHandRightRegular}"
                              Foreground="{DynamicResource LatticeNeutralFgBrush}"
                              IsVisible="{Binding Data.StatusKind, Converter={x:Static views:EnumMatchConverter.Instance}, ConverterParameter=NoNewTasks}" />
                    <PathIcon Width="12" Height="12" Data="{StaticResource IconMoreHorizontalRegular}"
                              Foreground="{DynamicResource LatticeNeutralFgBrush}"
                              IsVisible="{Binding Data.StatusKind, Converter={x:Static views:EnumMatchConverter.Instance}, ConverterParameter=Mixed}" />
                  </Panel>
                  <TextBlock Text="{Binding Data.StatusText}" FontSize="12"
                             Foreground="{DynamicResource LatticeTextSecondaryBrush}"
                             TextTrimming="CharacterEllipsis" ToolTip.Tip="{Binding Data.StatusText}" />
                </StackPanel>
              </DataTemplate>
            </DataGridTemplateColumn.CellTemplate>
          </DataGridTemplateColumn>
        </DataGrid.Columns>
      </DataGrid>

      <!-- Loading + empty overlays: stamp TasksView.axaml:208-231; empty text key
           ProjectsEmpty ("No projects attached — attach via the BOINC client on
           the host.", design 2a; same text both scopes). -->
    </Panel>
  </DockPanel>
</UserControl>
```

**Plan-time gaps the implementer must close (not placeholders — bounded lookups):**
- Icon resources: `IconChevronRightRegular`, `IconCheckmarkCircleRegular`, `IconHandRightRegular` — check `src/Lattice.App/Resources/` (or wherever `IconPlayRegular` etc. live; grep for `IconPauseRegular`); add the Fluent-icon Path data for any missing ones following the existing entries' sourcing convention.
- `$parent[UserControl]` typed-DataContext binding: this is the established Avalonia idiom for reaching the view's VM from inside a cell template; if the compiled-binding syntax fights back, consult avalonia-docs MCP (do not fall back to ReflectionBinding — binding errors are build failures in this repo).
- The chevron's expanded rotation (RotateTransform 90° when checked) rides `ToggleButton.chevron:checked` styles; add to the Styles block.

- [ ] **Step 4: Implement the code-behind**

`ProjectsView.axaml.cs` — row-class liveness comes from the shared `RowClassBinder` (w2a2); the view attaches once in the constructor and structurally cannot forget the detach drain (PR #42 round-1 P2 provenance — see the w2a2 plan header):

```csharp
_rowBinder = RowClassBinder<ProjectRow>.Attach(Grid, static (row, holder) =>
{
    row.Classes.Set("projectParent", holder.Data.IsParent);
    row.Classes.Set("projectChild", !holder.Data.IsParent);
});
```

with `internal int RowSubscriptionCount => _rowBinder.Count;` for the probe.

Plus `OnPartialBarClosed` — stamp from TasksView.axaml.cs (the FAInfoBar Closed→CloseButton reason → DismissPartialCommand path; hard-won FA fact in the class doc there).

Add the teardown-drain regression test as headless test 5 (stamp the Tasks one — find it via `grep -n RowSubscriptionCount tests/Lattice.App.Tests/Headless/TasksViewTests.cs`): render rows, detach the view from the visual tree, assert `view.RowSubscriptionCount == 0`. The binder makes this hard to break, but the test still ships per view: it pins the ATTACHMENT (a view that forgets to attach the binder, or attaches it to the wrong grid, fails here).

- [ ] **Step 5: Run headless tests**

Run: `dotnet test tests/Lattice.App.Tests --filter ProjectsView -v minimal`
Expected: PASS (all five, including the 40/32 geometry assert and the teardown drain).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(app): ProjectsView — flattened hierarchical grid with geometry-pinned row heights"
```

---

### Task 5: Shell wiring + resx + journey

**Files:**
- Modify: `src/Lattice.App/ViewModels/ShellViewModel.cs` (Views[1] + scope push)
- Modify: `src/Lattice.App/Views/ShellWindow.axaml` (DataTemplate)
- Modify: `src/Lattice.App/Localization/Strings.resx` (+ designer regen via build)
- Create: `tests/Lattice.App.Tests/Headless/Journeys/ProjectsScopeJourney.cs`

- [ ] **Step 1: resx keys** (skip any already added in Task 2; meaning-based names per T1 conventions, `Projects` prefix, group banner comment):

| Key | Value |
|---|---|
| `ProjectsTitle` | `Projects` |
| `ProjectsUpdate` | `Update` |
| `ProjectsNoNewTasks` | `No new tasks` |
| `ProjectsColHosts` | `Hosts` |
| `ProjectsColShare` | `Resource share` |
| `ProjectsColAvgCredit` | `Avg credit` |
| `ProjectsColTotalCredit` | `Total credit` |
| `ProjectsCountsFmt` | `{0} projects · {1} hosts` |
| `ProjectsTaskCountFmt` | `{0} tasks` |
| `ProjectsShareVariesFmt` | `Varies · {0}–{1}` |
| `ProjectsStatusActive` | `Active` |
| `ProjectsStatusSuspended` | `Suspended` |
| `ProjectsStatusNoNewTasks` | `No new tasks` |
| `ProjectsStatusActiveAll` | `Active on all hosts` |
| `ProjectsStatusAllFmt` | `{0} on all hosts` |
| `ProjectsStatusDeviationFmt` | `{0} · {1}/{2} hosts` |
| `ProjectsStatusMixedFmt` | `Mixed · {0} suspended · {1} no new tasks` |
| `ProjectsEmpty` | `No projects attached — attach via the BOINC client on the host.` |

- [ ] **Step 2: Shell wiring (red-first: extend `ShellViewModelTests` with a scope-push test for Projects, run, see it fail)**

`ShellViewModel.cs`:
- ctor: `Projects = new ProjectsViewModel(store, clock, uiState);` and `Views[1]`'s `new PlaceholderViewModel(Strings.NavProjects)` → `Projects`.
- property: `public ProjectsViewModel Projects { get; }`
- `OnScopeChanged`: add `Projects.Scope = value;`
- `Dispose`: add `Projects.Dispose();`

`ShellWindow.axaml` ContentControl.DataTemplates: add

```xml
<DataTemplate DataType="vm:ProjectsViewModel">
  <v:ProjectsView />
</DataTemplate>
```

- [ ] **Step 3: Journey**

`ProjectsScopeJourney.cs`, following `TasksScopeJourney.cs`'s harness idiom: two fake hosts sharing one project URL → navigate to Projects (SelectView "1") → assert one parent row rendered with "2" hosts text → select host-a in the rail → assert child rows impossible (no chevron) and parent shows host-a's values only → back to All hosts → expand → child rows visible. Settle on expected text (`HeadlessSync.WaitUntilAsync`), never transient state.

- [ ] **Step 4: Full suite**

Run: `dotnet build Lattice.sln -warnaserror && dotnet test Lattice.sln -v minimal` (then Release too)
Expected: green, zero warnings; `LocalizationTests` passes with the new keys.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(app): Projects view wired into shell + scope push + journey"
```

---

### Task 6: Wrap-up

- [ ] Rebase on latest main (`git fetch && git rebase origin/main`), rerun full suite Debug+Release.
- [ ] Push branch `m2c2-w2b-projects-view`, open the PR (body: issue #31 Projects leg; cite the flattened-DataGrid decision + the 40/32 geometry gate; note zero HostMonitor changes so the verification sync rule is not in play).
- [ ] Trigger `@codex review` (comment on the PR), dispatch pr-monitor with the PR number + trigger comment id, read the raw review threads yourself on completion (≥60 s re-poll after the review object posts), adjudicate findings red-first, merge on clean per the standing cadence.

---

## Explicitly out of scope

- Transfers / Event-log views (parallel plans `2026-07-11-m2c2-w2c-transfers-view.md` / `...-w2d-eventlog-view.md`).
- Column-header sorting on the Projects grid (disabled by decision; revisit only on user demand).
- Expansion-state persistence (session-local by decision).
- Any motion/animation (Wave 3, #32); Mica-adjacent work (#32).
- Per-view column breakpoints: design §Responsive (2f) names only Tasks columns (Elapsed → Application at 1000–1099); the Projects column set (684px + star) fits the 1000px minimum window, so no width-driven column hiding ships here (design-authoritative adjudication of the spec's "responsive column-breakpoint behavior ships per view" line — cite 2f in the PR body).

## Self-review notes (already applied)

- Design 2a coverage: columns/widths ↔ Task 4 XAML; three status tiers + share Varies ↔ Task 1 F# + Task 2 rendering + tests; no-pill status ↔ icon+text templates; 40/32 heights ↔ geometry test; single-host degradation ↔ VM test 2 + journey; default RAC-desc sort ↔ Task 1; credit Host*-mapping ↔ ProjectAttachment doc comments + Task 1 test.
- Known judgment calls the executor must not "fix": sorting disabled grid-wide; expansion not persisted; `Num()` whole-number rendering; suspended-wins status precedence.
- API risks front-loaded: per-row Height via row-class style (geometry test is the gate; `e.Row.Height` in OnLoadingRow is the sanctioned fallback), `$parent[UserControl]` cell-template command binding.
