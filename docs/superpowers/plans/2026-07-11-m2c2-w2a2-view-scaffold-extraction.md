# M2c-2 Wave 2a2 — View-Scaffold Extraction (behavior-invariant)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract the three transcription-prone pieces of the Tasks-view pattern into shared, single-copy machinery — `RowClassBinder` (row-subscription liveness kit), `ViewSliceProjection` (facts projection incl. the `inScope && isRowSource` gate), `PartialBarState` (fingerprint advance/dismiss) — and retrofit Tasks onto them, so the three Wave-2 view PRs consume structure instead of copying prose.

**Why this exists (root cause, not the symptom):** Codex round 1 on PR #42 caught the Projects plan omitting TasksView's detach-drain. The drain itself was patched into the plans, but the generative defect is that the liveness kit existed only as "stamp these ~40 lines" instructions — four hand-transcriptions of one invariant, each free to drop a line. Same exposure for the facts-projection gate (protected today by a comment) and the partial-bar block. This PR turns each invariant into code that consumers cannot get wrong (owner-directed escalation, 2026-07-11: restructure the invariant instead of patching its copies).

**Architecture:** Pure mechanical extraction, zero behavior change: `TasksViewModel.Rebuild` and `TasksView.axaml.cs` keep their semantics, tests prove it (full suite stays green; the teardown-drain and no-Reset regression tests keep passing untouched except for probe plumbing). New unit tests cover the binder's own contract (apply-on-load, re-apply on holder change, recycled-row unsubscribe, drain-on-detach).

**Tech Stack:** C# / Avalonia 12, existing `Lattice.App.Aggregation` (no F# changes).

**Sequencing:** Lands on main BEFORE the three view PRs (w2b/w2c/w2d plans consume these helpers). Small PR, standard Codex+CI gate.

**Standing rules that bind every task:**
- Red-first for new code; behavior-invariance for the retrofit (a failing existing test is a real finding, never a test to patch).
- `-warnaserror` clean, Debug + Release, on every commit.
- No `Lattice.Core` / `HostMachine.fs` changes (verification sync rule not in play; say so in the PR body).
- Avalonia API questions go to the avalonia-docs MCP, never guessed.
- Repo language: commits/comments in English.

---

## File structure

```
src/Lattice.App/
├── Infrastructure/RowClassBinder.cs    NEW — self-attaching row-subscription liveness kit
├── ViewModels/ViewSliceProjection.cs   NEW — HostEntry[] + scope → Slice<TRow>
├── ViewModels/PartialBarState.cs       NEW — fingerprint advance/dismiss wrapper
├── ViewModels/TasksViewModel.cs        MODIFY — Rebuild rides the two helpers
└── Views/TasksView.axaml.cs            MODIFY — subscription kit replaced by binder
    Views/TasksView.axaml               MODIFY — LoadingRow/UnloadingRow attributes dropped

tests/Lattice.App.Tests/
├── RowClassBinderTests.cs              NEW — the binder contract
└── Headless/TasksViewTests.cs          MODIFY — probe reads the binder count
```

---

### Task 1: `RowClassBinder`

**Files:**
- Create: `src/Lattice.App/Infrastructure/RowClassBinder.cs`
- Create: `tests/Lattice.App.Tests/RowClassBinderTests.cs`

- [ ] **Step 1: Write failing tests** (headless where a real DataGrid is needed — follow `Headless/DataGridInfraTests.cs` for grid construction; plain unit tests where events can be raised directly):

1. Attach + load a row → applier called with (row, holder).
2. Holder `PropertyChanged` while loaded → applier re-invoked with current Data.
3. Recycled row (second LoadingRow for the same `DataGridRow`, new holder) → old holder unsubscribed (mutating it no longer re-applies), new one live.
4. UnloadingRow → unsubscribed, `Count` decremented.
5. Grid detached from the visual tree → `Count == 0` and every holder unsubscribed (THE invariant this class exists for).

- [ ] **Step 2: Run to verify failure.**

Run: `dotnet test tests/Lattice.App.Tests --filter RowClassBinder -v minimal`
Expected: FAIL — type not defined.

- [ ] **Step 3: Implement**

`src/Lattice.App/Infrastructure/RowClassBinder.cs`:

```csharp
using System.ComponentModel;
using Avalonia.Controls;

namespace Lattice.App.Infrastructure;

/// <summary>
/// Owns the row-class subscription lifecycle for a DataGrid bound to
/// RowHolder rows: apply on load, re-apply when the holder's Data is swapped
/// in place by the reconciler, unsubscribe on unload AND on row recycling,
/// and drain everything when the grid leaves the visual tree — the shell's
/// ContentControl swaps views without touching ItemsSource, so UnloadingRow
/// never fires on navigation (the a2e0420 regression). Views attach once and
/// structurally cannot forget any leg of this.
/// </summary>
public sealed class RowClassBinder<THolder> where THolder : class, INotifyPropertyChanged
{
    private readonly Dictionary<DataGridRow, (THolder Holder, PropertyChangedEventHandler Handler)> _subscriptions = new();
    private readonly Action<DataGridRow, THolder> _apply;

    private RowClassBinder(Action<DataGridRow, THolder> apply) => _apply = apply;

    /// <summary>Rows currently tracked; the teardown-drain tests pin this to 0 after detach.</summary>
    public int Count => _subscriptions.Count;

    public static RowClassBinder<THolder> Attach(DataGrid grid, Action<DataGridRow, THolder> apply)
    {
        var binder = new RowClassBinder<THolder>(apply);
        grid.LoadingRow += binder.OnLoadingRow;
        grid.UnloadingRow += binder.OnUnloadingRow;
        grid.DetachedFromVisualTree += (_, _) => binder.Drain();
        return binder;
    }

    private void OnLoadingRow(object? sender, DataGridRowEventArgs e)
    {
        // Row recycling: a recycled DataGridRow gets a fresh LoadingRow for its
        // new item — unsubscribe any previous entry before overwriting.
        if (_subscriptions.Remove(e.Row, out var stale))
            stale.Holder.PropertyChanged -= stale.Handler;
        if (e.Row.DataContext is not THolder holder) return;

        _apply(e.Row, holder);
        // Holders raise PropertyChanged only for Data (the reconciler's
        // in-place swap), so no property-name filter is needed.
        PropertyChangedEventHandler handler = (_, _) => _apply(e.Row, holder);
        holder.PropertyChanged += handler;
        _subscriptions[e.Row] = (holder, handler);
    }

    private void OnUnloadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (_subscriptions.Remove(e.Row, out var sub))
            sub.Holder.PropertyChanged -= sub.Handler;
    }

    private void Drain()
    {
        foreach (var (holder, handler) in _subscriptions.Values)
            holder.PropertyChanged -= handler;
        _subscriptions.Clear();
    }
}
```

(API check before committing, per standing rule: `DataGrid.LoadingRow`/`UnloadingRow` are CLR events on the control — TasksView already wires them from XAML today, so subscribing in code is equivalent; `DetachedFromVisualTree` is the `Visual` event backing the `OnDetachedFromVisualTree` override TasksView currently uses. Confirm both signatures against the local package metadata the way the plan round did — `~/.nuget/packages/avalonia.controls.datagrid/12.1.0/lib/net10.0/Avalonia.Controls.DataGrid.xml`.)

- [ ] **Step 4: Run tests** — Expected: PASS (5).

- [ ] **Step 5: Mutation falsification, then commit**

Temporarily delete the `Drain()` body, run, confirm test 5 FAILS; revert; record in the commit body.

```bash
git add -A
git commit -m "feat(app): RowClassBinder — single-copy row-subscription liveness kit"
```

---

### Task 2: `ViewSliceProjection` + `PartialBarState`

**Files:**
- Create: `src/Lattice.App/ViewModels/ViewSliceProjection.cs`
- Create: `src/Lattice.App/ViewModels/PartialBarState.cs`

No new tests in this step: both are verbatim extractions whose behavior is already pinned by `TasksViewModelTests` (the retrofit in Task 3 is the proof — if either helper drifts from the original semantics, those tests go red).

- [ ] **Step 1: Implement `ViewSliceProjection`**

```csharp
using Lattice.App.Aggregation;
using Lattice.App.Infrastructure;

namespace Lattice.App.ViewModels;

/// <summary>
/// The one copy of the per-host facts projection every data view feeds to
/// ViewSlice.compute. Encodes the Codex-adjudicated subtleties structurally:
/// unreachable/stale tiers span ALL hosts (scope-independent episode
/// semantics) while row materialization is gated on inScope AND isRowSource —
/// out-of-scope hosts' rows are never built (Rebuild runs every 1 s tick;
/// Codex P2, PR #37).
/// </summary>
public static class ViewSliceProjection
{
    public static Slice<TRow> Compute<TRow>(
        IReadOnlyList<HostEntry> hosts,
        ScopeSelection scope,
        Func<HostEntry, TRow[]> rowsOf)
    {
        var facts = hosts.Select(h =>
        {
            var rail = RailStateProjection.From(h.Status);
            var inScope = scope.IsAllHosts || h.Config.Id == scope.HostId;
            var isRowSource = rail == RailState.Connected && h.Snapshot is not null;
            return new HostFacts<TRow>(
                h.Config.Id,
                inScope,
                isRowSource,
                rail is RailState.Unreachable or RailState.AuthFailed,
                rail is RailState.Retrying or RailState.Unreachable,
                isRowSource ? h.Snapshot!.Timestamp : null,
                inScope && isRowSource ? rowsOf(h) : []);
        }).ToArray();
        return ViewSlice.compute(facts);
    }
}
```

- [ ] **Step 2: Implement `PartialBarState`**

```csharp
namespace Lattice.App.ViewModels;

/// <summary>
/// The one copy of the partial-bar episode wiring: holds the dismissed and
/// current fingerprints, advances them per PartialBarPolicy, and applies the
/// All-hosts scope gate. Episode semantics stay in PartialBarPolicy; this
/// class only removes the three-per-view transcription of its call protocol.
/// </summary>
public sealed class PartialBarState
{
    private PartialBarPolicy.Fingerprint _dismissed = PartialBarPolicy.EmptyFingerprint;
    private PartialBarPolicy.Fingerprint _current = PartialBarPolicy.EmptyFingerprint;

    /// <summary>Advance with this rebuild's slice facts; returns whether the bar shows.</summary>
    public bool Advance(IReadOnlySet<Guid> unreachableIds, IReadOnlySet<Guid> coveredIds, bool isAllHostsScope)
    {
        _current = new PartialBarPolicy.Fingerprint(unreachableIds, coveredIds);
        (PartialBarPolicy.Fingerprint dismissed, bool visible) = PartialBarPolicy.Advance(_dismissed, _current);
        _dismissed = dismissed;
        return isAllHostsScope && visible;
    }

    public void Dismiss() => _dismissed = PartialBarPolicy.Dismiss(_current);
}
```

(Adapt the two parameter types to `PartialBarPolicy.Fingerprint`'s actual ctor parameter types — it takes the `HashSet<Guid>`s from the slice today; keep the signature identical to what the fingerprint expects rather than inventing conversions.)

- [ ] **Step 3: Build** — `dotnet build Lattice.sln -warnaserror` clean. Commit:

```bash
git add -A
git commit -m "feat(app): ViewSliceProjection + PartialBarState — single-copy view scaffolding"
```

---

### Task 3: Retrofit Tasks onto the helpers (behavior-invariant)

**Files:**
- Modify: `src/Lattice.App/ViewModels/TasksViewModel.cs`
- Modify: `src/Lattice.App/Views/TasksView.axaml.cs` + `TasksView.axaml`
- Modify: `tests/Lattice.App.Tests/Headless/TasksViewTests.cs` (probe plumbing ONLY)

- [ ] **Step 1: `TasksViewModel`** — in `Rebuild()`, replace the facts-projection block with:

```csharp
var slice = ViewSliceProjection.Compute(_store.Hosts, Scope,
    h => h.Snapshot!.Tasks.Select(t => TaskRowViewModel.From(t, h.Config.Id, h.Config.DisplayName)).ToArray());
```

and the fingerprint block with the `PartialBarState` field (`private readonly PartialBarState _partialBar = new();`):

```csharp
ShowPartialBar = _partialBar.Advance(slice.UnreachableIds, slice.CoveredIds, Scope.IsAllHosts);
```

`DismissPartial` becomes `_partialBar.Dismiss(); ShowPartialBar = false;`. Delete the two `PartialBarPolicy.Fingerprint` fields. Everything else in Rebuild stays byte-identical.

- [ ] **Step 2: `TasksView`** — delete `OnLoadingRow`/`OnUnloadingRow`/`OnDetachedFromVisualTree`'s drain call/`DrainRowSubscriptions`/`_rowSubscriptions`; drop the `LoadingRow=`/`UnloadingRow=` XAML attributes; in the constructor after `InitializeComponent()`:

```csharp
_rowBinder = RowClassBinder<TaskRow>.Attach(Grid, static (row, holder) =>
{
    row.Classes.Set("atRisk", holder.Data.IsDeadlineAtRisk);
    row.Classes.Set("suspended", holder.Data.IsSuspended);
});
```

Keep the `internal int RowSubscriptionCount => _rowBinder.Count;` forward so the existing teardown-drain test compiles unchanged. If `OnDetachedFromVisualTree` does other work today (`TasksView.axaml.cs:108-114` — read it), keep that other work; only the drain leg moves.

- [ ] **Step 3: Full suite, both configs.** Every existing Tasks test must pass UNCHANGED (probe forward aside). A red here is a semantics drift in the extraction — fix the helper, never the test.

Run: `dotnet build Lattice.sln -c Release -warnaserror && dotnet test Lattice.sln -v minimal && dotnet test Lattice.sln -c Release -v minimal`

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "refactor(app): Tasks rides RowClassBinder + ViewSliceProjection + PartialBarState

Behavior-invariant: full suite green with no test-body changes (the
RowSubscriptionCount probe now forwards to the binder)."
```

---

### Task 4: Wrap-up

- [ ] Rebase on main, full suite Debug+Release, push branch `m2c2-w2a2-view-scaffold`, open PR (body: the root-cause paragraph from this plan's header, the Codex-P2 provenance, behavior-invariance evidence). Trigger `@codex review`, pr-monitor, read raw threads yourself (≥60 s re-poll), adjudicate red-first, merge on clean.

---

## Explicitly out of scope

- Any change to what the helpers compute (pure extraction; drift = bug).
- Command-bar / overlay XAML sharing (layout duplication is visible-at-a-glance in axaml and cheap to diff; no invariant hides in it — extraction there is speculative generality today).
- The three view PRs themselves (w2b/w2c/w2d plans consume this PR's output and are amended accordingly).

## Self-review notes (already applied)

- The three extractions map 1:1 to the three transcription risks named in the PR #42 round-1 adjudication; nothing speculative added.
- Binder is generic over the holder, not over "row classes" — appliers stay per-view static lambdas, so per-view styling logic remains local and testable.
- PartialBarState deliberately does NOT own the bar text formatting (per-view resx) or the scope gate's meaning — only the call protocol that was being transcribed.
