# M2c-2 Wave 2d — Event Log View (message data path + merged stream)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The Event-log data view: the new Core→App message data path (`HostStore` consumption of `MessagesAdded`), per-host 5,000-message retention, time-merged all-hosts stream, Info/Warning/Error filter pills, Following auto-scroll, and the unread InfoBadge on the nav item.

**Architecture:** Messages are a *stream*, not a snapshot — this view gets its own pipeline. A pure F# `MessageLog` module (in `Lattice.App.Aggregation`) models the retained log as a **set keyed by message identity `(host, seqno, timestamp)`**: ingestion is an idempotent set-union that returns the actually-new entries as a delta. This dissolves the reconnect-replay problem at the model level (owner decision, 2026-07-11): the first tick of a reconnection re-fetches the daemon's whole buffer, and `HostMonitor`'s event carries no replace marker — with set-ingest semantics none is needed. Seqno alone is NOT identity (it resets when the daemon restarts); the timestamp disambiguates reused seqnos, so replays dedup exactly while a restarted daemon's reused seqnos correctly survive as new lines — and pre-restart history is *retained* (a log viewer keeps history; Core's own buffer-replace semantics are Core's concern and stay untouched). `HostStore` gains a thin marshaled `MessagesReceived` forwarding event (separate from `Changed` — a message batch must not trigger a full rebuild of every other view). `EventLogViewModel` owns the fold over `MessageLog`, filters, and feeds the shared `Reconcile`/`CollectionReconciler` machinery; `ShellViewModel` mirrors its unread count into the nav item's `FANavigationViewItem.InfoBadge` (confirmed present in FluentAvalonia 3.0.1) exactly the way it mirrors `TasksCount` today.

**Tech Stack:** F# (net10.0, `--warnaserror`) + FsCheck 2.16.6 (xunit v2), C# / Avalonia 12 DataGrid 12.1.0, FluentAvalonia 3.0.1, CommunityToolkit.Mvvm.

**Authoritative sources:** issue #31 (scope), spec `docs/superpowers/specs/2026-07-10-m2c2-data-views-design.md` §Wave 2, design package `docs/design/m2/README.md` §"Event log view (2c)". The design package wins over this plan when they conflict.

**Standing rules that bind every task:**
- Red-first: run the failing test and see it fail before implementing. Reviewer repeats falsification.
- `-warnaserror` clean, Debug + Release, on every commit.
- **Nothing here touches `src/Lattice.Core/HostMonitor.cs` or `HostMachine.fs`.** The set-ingest model was chosen specifically so the Core event surface stays untouched. `HostStore` (App layer) changes are in scope. If a task somehow needs a HostMonitor change, the verification sync rule (CLAUDE.md) applies — stop and escalate.
- F# style canon (CLAUDE.md) applies; idiom review is a blocking review step. The F# below was typechecked and smoke-run via `dotnet fsi` at plan time (idempotence, seqno-reset survival, capacity eviction, merge order all exercised) — transcribe it verbatim.
- Avalonia API questions go to the avalonia-docs MCP, never guessed.
- Conflict isolation (parallel Wave-2 worktrees): this PR owns its View/ViewModel/test files plus the `HostStore` addition; shared touch points (`ShellViewModel.Views[3]`, one DataTemplate block, one InfoBadge attribute on `NavEventLog`, resx keys prefixed `EventLog*`) are additive; rebase on main before opening the PR.
- Culture: timestamps render `MM-dd HH:mm:ss` with InvariantCulture (calendar leak pin — see `TaskRowViewModel`'s deadline comment).
- Repo language: commits/comments in English.

---

## File structure

```
src/Lattice.App.Aggregation/
└── MessageLog.fs                       NEW — MessageKey/LogEntry/MessageLog set-ingest model (+ fsproj Compile entry)

tests/Lattice.Aggregation.Tests/
└── MessageLogTests.fs                  NEW — examples + FsCheck properties (+ fsproj Compile entry)

src/Lattice.App/
├── Infrastructure/HostStore.cs         MODIFY — MessagesReceived marshaled forwarding
├── ViewModels/EventLogRow.cs           NEW — closed holder + row record + priority mapping
├── ViewModels/EventLogViewModel.cs     NEW — MessageLog fold + filters + unread counter
├── Views/EventLogView.axaml            NEW
├── Views/EventLogView.axaml.cs         NEW — row tints + Following auto-scroll
├── ViewModels/ShellViewModel.cs        MODIFY — Views[3] swap + scope push + unread mirror + active-view signal
├── Views/ShellWindow.axaml             MODIFY — DataTemplate + NavEventLog InfoBadge
└── Localization/Strings.resx           MODIFY — EventLog* keys

tests/Lattice.App.Tests/
├── HostStoreTests.cs                   MODIFY — MessagesReceived cases
├── EventLogRowViewModelTests.cs        NEW
├── EventLogViewModelTests.cs           NEW
├── Headless/EventLogViewTests.cs       NEW
└── Headless/Journeys/EventLogJourney.cs  NEW
```

---

### Task 1: F# `MessageLog` — identity-keyed set ingest

**Files:**
- Create: `src/Lattice.App.Aggregation/MessageLog.fs`
- Modify: `src/Lattice.App.Aggregation/Lattice.App.Aggregation.fsproj` (Compile entry after ViewSlice.fs)
- Create: `tests/Lattice.Aggregation.Tests/MessageLogTests.fs` (+ fsproj Compile entry)

- [ ] **Step 1: Write example-based failing tests**

`tests/Lattice.Aggregation.Tests/MessageLogTests.fs`:

```fsharp
module Lattice.Aggregation.Tests.MessageLogTests

open System
open Xunit
open FsCheck
open FsCheck.Xunit
open Lattice.App.Aggregation

let hostA = Guid.NewGuid()
let hostB = Guid.NewGuid()

let entry hostId seqno ticks (body: string) =
    { Key = { HostId = hostId; Seqno = seqno; TimestampTicks = ticks }; Message = body }

[<Fact>]
let ``ingest returns new entries and retains them`` () =
    let log, added = MessageLog.ingest hostA [| entry hostA 1 10L "m1"; entry hostA 2 20L "m2" |] (MessageLog.empty 100)
    Assert.Equal(2, added.Length)
    Assert.Equal(2, (MessageLog.merged log).Length)

[<Fact>]
let ``reconnect replay is a no-op: same batch twice adds nothing`` () =
    let batch = [| entry hostA 1 10L "m1"; entry hostA 2 20L "m2" |]
    let log1, _ = MessageLog.ingest hostA batch (MessageLog.empty 100)
    let log2, added = MessageLog.ingest hostA batch log1
    Assert.Empty added
    Assert.Equal<MessageLog<string>>(log1, log2)

[<Fact>]
let ``daemon restart: reused seqno with different timestamp is a new line, history retained`` () =
    let log1, _ = MessageLog.ingest hostA [| entry hostA 1 10L "old-life" |] (MessageLog.empty 100)
    let log2, added = MessageLog.ingest hostA [| entry hostA 1 99L "new-life" |] log1
    Assert.Single added |> ignore
    Assert.Equal(2, (MessageLog.merged log2).Length)

[<Fact>]
let ``capacity keeps the newest per host`` () =
    let log1, _ = MessageLog.ingest hostA [| for i in 1 .. 5 -> entry hostA i (int64 (i * 10)) $"m{i}" |] (MessageLog.empty 3)
    let retained = MessageLog.merged log1
    Assert.Equal(3, retained.Length)
    Assert.All(retained, fun e -> Assert.True(e.Key.Seqno >= 3))

[<Fact>]
let ``merged stream is time-ordered across hosts`` () =
    let log1, _ = MessageLog.ingest hostA [| entry hostA 1 10L "a1"; entry hostA 2 30L "a2" |] (MessageLog.empty 100)
    let log2, _ = MessageLog.ingest hostB [| entry hostB 1 20L "b1" |] log1
    let bodies = MessageLog.merged log2 |> Array.map (fun e -> e.Message)
    Assert.Equal<string[]>([| "a1"; "b1"; "a2" |], bodies)

[<Fact>]
let ``prune drops removed hosts`` () =
    let log1, _ = MessageLog.ingest hostA [| entry hostA 1 10L "a" |] (MessageLog.empty 100)
    let log2, _ = MessageLog.ingest hostB [| entry hostB 1 20L "b" |] log1
    let pruned = MessageLog.prune (System.Collections.Generic.HashSet [ hostA ]) log2
    Assert.Single(MessageLog.merged pruned) |> ignore
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Lattice.Aggregation.Tests -v minimal`
Expected: FAIL — types not defined.

- [ ] **Step 3: Implement**

`src/Lattice.App.Aggregation/MessageLog.fs` (typechecked + smoke-run via dotnet fsi at plan time — transcribe verbatim):

```fsharp
namespace Lattice.App.Aggregation

open System
open System.Collections.Generic

/// One daemon log line's identity. Seqno alone is NOT unique across daemon
/// restarts (it resets); the timestamp disambiguates reused seqnos while
/// staying stable under reconnect replay (the same line re-fetched carries
/// the same triple). Null message timestamps map to 0 ticks at the boundary.
[<Struct>]
type MessageKey =
    { HostId: Guid
      Seqno: int
      TimestampTicks: int64 }

type LogEntry<'Msg> =
    { Key: MessageKey
      Message: 'Msg }

/// Per-host retained log with set-ingest semantics. Treat as opaque outside
/// this module; consumers go through ingest/merged/prune.
type MessageLog<'Msg> =
    { Capacity: int
      ByHost: Map<Guid, LogEntry<'Msg>[]> }

module MessageLog =
    let empty (capacity: int) : MessageLog<'Msg> =
        { Capacity = capacity; ByHost = Map.empty }

    /// Set-semantics ingest of one host's batch: entries whose key is already
    /// retained are dropped (that is the reconnect-replay dedup), the rest
    /// merge in (timestamp, seqno) order, oldest evicted beyond capacity.
    /// Returns the new log and the entries that were actually new — the
    /// unread-badge delta. Idempotent: ingesting a batch twice equals once.
    /// Precondition: every key's HostId = hostId (the event is per-host).
    let ingest (hostId: Guid) (batch: LogEntry<'Msg>[]) (log: MessageLog<'Msg>) : MessageLog<'Msg> * LogEntry<'Msg>[] =
        let existing = log.ByHost |> Map.tryFind hostId |> Option.defaultValue [||]
        let known = HashSet(existing |> Seq.map (fun e -> e.Key))
        let fresh =
            batch
            |> Array.distinctBy (fun e -> e.Key)
            |> Array.filter (fun e -> not (known.Contains e.Key))
        if fresh.Length = 0 then
            log, [||]
        else
            let merged =
                Array.append existing fresh
                |> Array.sortBy (fun e -> e.Key.TimestampTicks, e.Key.Seqno)
            let retained =
                if merged.Length > log.Capacity then merged[merged.Length - log.Capacity ..]
                else merged
            { log with ByHost = log.ByHost |> Map.add hostId retained }, fresh

    /// Every retained line across hosts as one time-merged stream, oldest
    /// first; (host, seqno) break timestamp ties deterministically.
    let merged (log: MessageLog<'Msg>) : LogEntry<'Msg>[] =
        log.ByHost
        |> Map.toArray
        |> Array.collect snd
        |> Array.sortBy (fun e -> e.Key.TimestampTicks, e.Key.HostId, e.Key.Seqno)

    /// Drops hosts no longer in the registry.
    let prune (liveHosts: HashSet<Guid>) (log: MessageLog<'Msg>) : MessageLog<'Msg> =
        { log with ByHost = log.ByHost |> Map.filter (fun id _ -> liveHosts.Contains id) }
```

(`HashSet` here is query-only after construction — boundary-pure per the canon; no boundary comment needed since no mutation escapes and none persists.)

- [ ] **Step 4: Run tests** — Expected: PASS (6).

- [ ] **Step 5: Add FsCheck properties (red-first via mutation falsification)**

Append to `MessageLogTests.fs`:

```fsharp
let batchGen hostId =
    gen {
        let! n = Gen.choose (0, 12)
        let! entries =
            Gen.listOfLength n (gen {
                let! seqno = Gen.choose (1, 6)
                let! ticks = Gen.elements [ 10L; 20L; 30L ]
                return entry hostId seqno ticks $"s{seqno}t{ticks}"
            })
        return Array.ofList entries
    }

type LogArbs =
    static member Batches() =
        gen {
            let! b1 = batchGen hostA
            let! b2 = batchGen hostA
            return (b1, b2)
        }
        |> Arb.fromGen

[<Property(Arbitrary = [| typeof<LogArbs> |])>]
let ``ingest is idempotent`` ((b1, b2)) =
    let log1, _ = MessageLog.ingest hostA b1 (MessageLog.empty 50)
    let log2, _ = MessageLog.ingest hostA b2 log1
    let log3, added = MessageLog.ingest hostA b2 log2
    log3 = log2 && Array.isEmpty added

[<Property(Arbitrary = [| typeof<LogArbs> |])>]
let ``retained set is the distinct union, capped to newest`` ((b1, b2)) =
    let log1, _ = MessageLog.ingest hostA b1 (MessageLog.empty 50)
    let log2, _ = MessageLog.ingest hostA b2 log1
    let expected =
        Array.append b1 b2
        |> Array.distinctBy (fun e -> e.Key)
        |> Array.sortBy (fun e -> e.Key.TimestampTicks, e.Key.Seqno)
    MessageLog.merged log2 = expected

[<Property(Arbitrary = [| typeof<LogArbs> |])>]
let ``delta is exactly the not-yet-known entries`` ((b1, b2)) =
    let log1, _ = MessageLog.ingest hostA b1 (MessageLog.empty 50)
    let _, added = MessageLog.ingest hostA b2 log1
    let knownKeys = b1 |> Array.map (fun e -> e.Key) |> Set.ofArray
    let expected =
        b2 |> Array.distinctBy (fun e -> e.Key) |> Array.filter (fun e -> not (knownKeys.Contains e.Key))
    (added |> Array.map (fun e -> e.Key) |> Set.ofArray) = (expected |> Array.map (fun e -> e.Key) |> Set.ofArray)
```

Mutation falsification: temporarily drop the `known.Contains` filter (ingest everything), run, confirm the idempotence property FAILS; revert. Record the observed failure in the commit body.

- [ ] **Step 6: Run tests, commit**

```bash
git add -A
git commit -m "feat(aggregation): MessageLog identity-keyed set ingest with idempotence properties"
```

---

### Task 2: `HostStore.MessagesReceived` forwarding

**Files:**
- Modify: `src/Lattice.App/Infrastructure/HostStore.cs`
- Modify: `tests/Lattice.App.Tests/HostStoreTests.cs`

- [ ] **Step 1: Write failing tests** (mirror the existing HostStoreTests fixture — QueueUiDispatcher + fake manager events):

1. `MessagesReceived` fires on the UI thread (via the queued dispatcher) with the host id + batch when the manager raises `MessagesAdded`.
2. A message batch does NOT raise `Changed` (the whole point of the separate channel — assert zero `Changed` invocations).
3. After `Dispose`, a queued message batch is dropped (stamp the existing disposed-guard test pattern).

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: Implement.** In `HostStore.cs`:

```csharp
// ctor, next to the existing subscriptions:
manager.MessagesAdded += OnMessagesAdded;

// Dispose, next to the existing unsubscriptions:
_manager.MessagesAdded -= OnMessagesAdded;
MessagesReceived = null;

/// <summary>
/// Raised on the UI thread with each host's freshly polled message batch.
/// Deliberately separate from <see cref="Changed"/>: messages arrive every
/// poll tick and must not trigger a full rebuild of the snapshot-driven
/// views. Consumers own retention/dedup (EventLogViewModel's MessageLog —
/// reconnect replays are deduped there by message identity, so this event
/// forwards batches verbatim, replay or not).
/// </summary>
public event EventHandler<MessagesAddedEventArgs>? MessagesReceived;

private void OnMessagesAdded(object? sender, MessagesAddedEventArgs e) =>
    _dispatcher.Post(() =>
    {
        if (_disposed) return;
        MessagesReceived?.Invoke(this, e);
    });
```

(`MessagesAddedEventArgs` is the existing `Lattice.Core` record `(Guid HostId, IReadOnlyList<Message> Messages)` — reused as-is; no Core changes.)

- [ ] **Step 4: Run tests** — Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(app): HostStore forwards MessagesAdded on the UI thread as MessagesReceived"
```

---

### Task 3: `EventLogRow` + priority mapping

**Files:**
- Create: `src/Lattice.App/ViewModels/EventLogRow.cs`
- Create: `tests/Lattice.App.Tests/EventLogRowViewModelTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
using Lattice.App.Aggregation;
using Lattice.App.ViewModels;
using Lattice.Boinc.GuiRpc;

namespace Lattice.Tests;

public class EventLogRowViewModelTests
{
    private static readonly DateTimeOffset T = new(2026, 7, 4, 14, 32, 8, TimeSpan.Zero);

    [Fact]
    public void Renders_timestamp_host_project_body()
    {
        var msg = new Message("Einstein@Home", MessagePriority.Info, 7, T, "work fetch");
        var row = EventLogRowViewModel.From(msg, Guid.NewGuid(), "host-a");
        Assert.Equal("07-04 14:32:08", row.TimestampText); // local-time render of T's local equivalent
        Assert.Equal("host-a", row.Host);
        Assert.Equal("Einstein@Home", row.Project);
        Assert.Equal("work fetch", row.Body);
        Assert.Equal(EventLogPriority.Info, row.Priority);
    }

    [Fact]
    public void Priority_maps_user_alert_to_warning_and_internal_error_to_error()
    {
        Assert.Equal(EventLogPriority.Warning, EventLogRowViewModel.MapPriority(MessagePriority.UserAlert));
        Assert.Equal(EventLogPriority.Error, EventLogRowViewModel.MapPriority(MessagePriority.InternalError));
    }

    [Fact]
    public void Key_carries_identity_with_null_timestamp_as_zero_ticks()
    {
        var hostId = Guid.NewGuid();
        var msg = new Message("", MessagePriority.Info, 3, null, "b");
        var key = EventLogRowViewModel.KeyOf(msg, hostId);
        Assert.Equal(hostId, key.HostId);
        Assert.Equal(3, key.Seqno);
        Assert.Equal(0L, key.TimestampTicks);
    }
}
```

(For the first test, compute the expected literal from `T.ToLocalTime()` the way `TaskRowViewModel`'s deadline test does — follow that file's established approach to local-time assertions rather than hardcoding a zone.)

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: Implement**

`src/Lattice.App/ViewModels/EventLogRow.cs`:

```csharp
using System.Globalization;
using Lattice.App.Aggregation;
using Lattice.App.Infrastructure;
using Lattice.Boinc.GuiRpc;

namespace Lattice.App.ViewModels;

/// <summary>The three filter-pill priorities (design 2c). BOINC's
/// MessagePriority maps Info→Info, UserAlert→Warning, InternalError→Error.</summary>
public enum EventLogPriority
{
    Info,
    Warning,
    Error,
}

/// <summary>Closed holder so XAML can use x:DataType.</summary>
public sealed class EventLogRow(MessageKey key, EventLogRowViewModel data)
    : RowHolder<MessageKey, EventLogRowViewModel>(key, data);

/// <summary>Immutable row projection for one daemon log line.</summary>
public sealed record EventLogRowViewModel(
    string TimestampText,
    string Host,
    string Project,
    string Body,
    EventLogPriority Priority)
{
    public static MessageKey KeyOf(Message msg, Guid hostId) =>
        new(hostId, msg.Seqno, msg.Timestamp?.UtcTicks ?? 0L);

    public static EventLogPriority MapPriority(MessagePriority p) => p switch
    {
        MessagePriority.Info => EventLogPriority.Info,
        MessagePriority.UserAlert => EventLogPriority.Warning,
        MessagePriority.InternalError => EventLogPriority.Error,
        // BOINC's enum is daemon-defined; unknown values degrade to Info
        // rather than crashing the log view on a newer daemon.
        _ => EventLogPriority.Info,
    };

    public static EventLogRowViewModel From(Message msg, Guid hostId, string host) =>
        new(
            TimestampText: msg.Timestamp?.ToLocalTime()
                .ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "—",
            Host: host,
            Project: msg.Project,
            Body: msg.Body,
            Priority: MapPriority(msg.Priority));
}
```

(`MessageKey` positional construction from C#: the F# struct record's compiler ctor takes camelCase `(hostId, seqno, timestampTicks)` — positional as shown is the reliable path, the w2a lesson.)

- [ ] **Step 4: Run tests, commit**

```bash
git add -A
git commit -m "feat(app): EventLogRow record + BOINC priority mapping"
```

---

### Task 4: `EventLogViewModel`

**Files:**
- Create: `src/Lattice.App/ViewModels/EventLogViewModel.cs`
- Create: `tests/Lattice.App.Tests/EventLogViewModelTests.cs`

**Shape:** same ctor/Scope/Dispose skeleton as `TasksViewModel`, but the data source is the `MessagesReceived` stream folded through `MessageLog`, not the snapshot rebuild. `store.Changed` is still consumed — for host add/remove (prune) and the reachable-host count in the status bar — but does NOT rebuild rows.

- [ ] **Step 1: Write failing tests** (each real, fixture from TasksViewModelTests + the new HostStore event):

1. A message batch appends rows in timestamp order; a replayed identical batch appends nothing (reconnect journey at VM level).
2. All-hosts scope merges two hosts' messages by time; single-host scope shows that host's only.
3. Filter pills: Warning-off hides UserAlert rows (unfiltered retention intact — re-enabling restores them).
4. Search text filters on body (case-insensitive), like the Tasks filter.
5. Unread: warning+error arrivals increment `UnreadCount` while `IsViewActive == false`; setting `IsViewActive = true` zeroes it and arrivals while active don't count. Info-priority arrivals never count.
6. Host removal prunes its rows on `store.Changed`.
7. `CountsText` renders "{visible} messages · {reachable} reachable hosts" facts.

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: Implement**

```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lattice.App.Aggregation;
using Lattice.App.Infrastructure;
using Lattice.App.Localization;
using Lattice.Core;

namespace Lattice.App.ViewModels;

/// <summary>
/// The Event-log view: folds per-host message batches (HostStore.MessagesReceived)
/// through the pure MessageLog model — identity-keyed set ingest, so reconnect
/// replays dedup here and nothing upstream needs a replace marker — then
/// filters and reconciles into the grid. Unlike the snapshot views, rows
/// rebuild on message arrival and scope/filter changes, NOT on every store
/// Changed (which only prunes/updates counts here).
/// </summary>
public sealed partial class EventLogViewModel : ObservableObject, IDisposable
{
    /// <summary>Design 2c: retain the last 5,000 messages per host.</summary>
    internal const int RetentionPerHost = 5000;

    private readonly HostStore _store;
    private MessageLog<EventLogRowViewModel> _log = MessageLog.empty<EventLogRowViewModel>(RetentionPerHost);
    private ScopeSelection _scope = ScopeSelection.AllHosts;

    public EventLogViewModel(HostStore store)
    {
        _store = store;
        store.MessagesReceived += OnMessagesReceived;
        store.Changed += OnStoreChanged;
        Rebuild();
    }

    public ObservableCollection<RowHolder<MessageKey, EventLogRowViewModel>> Rows { get; } = [];

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

    [ObservableProperty] private bool _isAllHostsScope = true;
    [ObservableProperty] private bool _showInfo = true;
    [ObservableProperty] private bool _showWarning = true;
    [ObservableProperty] private bool _showError = true;
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private bool _isFollowing = true;
    [ObservableProperty] private string _countsText = "";
    [ObservableProperty] private bool _isEmpty;

    /// <summary>Unread warning+error count for the nav InfoBadge. Counts only
    /// while the view is not active; activation clears it.</summary>
    [ObservableProperty] private int _unreadCount;

    /// <summary>Set by ShellViewModel when this page becomes / stops being CurrentPage.</summary>
    public bool IsViewActive
    {
        get => _isViewActive;
        set
        {
            _isViewActive = value;
            if (value) UnreadCount = 0;
        }
    }
    private bool _isViewActive;

    partial void OnShowInfoChanged(bool value) => Rebuild();
    partial void OnShowWarningChanged(bool value) => Rebuild();
    partial void OnShowErrorChanged(bool value) => Rebuild();
    partial void OnFilterTextChanged(string value) => Rebuild();

    [RelayCommand]
    private void ResumeFollowing() => IsFollowing = true;

    private void OnMessagesReceived(object? sender, MessagesAddedEventArgs e)
    {
        var host = _store.Hosts.FirstOrDefault(h => h.Config.Id == e.HostId);
        var hostName = host?.Config.DisplayName ?? "";
        var batch = e.Messages
            .Select(m => new LogEntry<EventLogRowViewModel>(
                EventLogRowViewModel.KeyOf(m, e.HostId),
                EventLogRowViewModel.From(m, e.HostId, hostName)))
            .ToArray();

        // F# tuple return: Item1 = new log, Item2 = the actually-new entries.
        var result = MessageLog.ingest(e.HostId, batch, _log);
        _log = result.Item1;
        var added = result.Item2;
        if (added.Length == 0) return;

        if (!IsViewActive)
            UnreadCount += added.Count(a => a.Message.Priority is EventLogPriority.Warning or EventLogPriority.Error);
        Rebuild();
    }

    private void OnStoreChanged(object? sender, EventArgs e)
    {
        // Host set changes only: prune removed hosts, refresh counts. Message
        // arrival is the row-driving event, not store Changed.
        var live = new HashSet<Guid>(_store.Hosts.Select(h => h.Config.Id));
        var pruned = MessageLog.prune(live, _log);
        if (!pruned.Equals(_log))
        {
            _log = pruned;
            Rebuild();
        }
        else
        {
            UpdateCounts(Rows.Count);
        }
    }

    private bool Matches(EventLogRowViewModel row)
    {
        var priorityOn = row.Priority switch
        {
            EventLogPriority.Info => ShowInfo,
            EventLogPriority.Warning => ShowWarning,
            EventLogPriority.Error => ShowError,
            _ => true,
        };
        return priorityOn
            && (FilterText.Length == 0
                || row.Body.Contains(FilterText, StringComparison.OrdinalIgnoreCase)
                || row.Project.Contains(FilterText, StringComparison.OrdinalIgnoreCase));
    }

    private void Rebuild()
    {
        IsAllHostsScope = Scope.IsAllHosts;

        var target = MessageLog.merged(_log)
            .Where(e => Scope.IsAllHosts || e.Key.HostId == Scope.HostId)
            .Where(e => Matches(e.Message))
            .Select(e => (e.Key, e.Message))
            .ToArray();

        var existing = Rows.Select(h => (h.Key, h.Data)).ToArray();
        CollectionReconciler.Apply(Rows, Reconcile.diff(existing, target),
            (key, row) => new EventLogRow(key, row));

        UpdateCounts(target.Length);
        IsEmpty = target.Length == 0;
    }

    private void UpdateCounts(int visible)
    {
        var reachable = _store.Hosts.Count(
            h => RailStateProjection.From(h.Status) == RailState.Connected);
        CountsText = string.Format(Strings.EventLogCountsFmt, visible, reachable);
    }

    public void Dispose()
    {
        _store.MessagesReceived -= OnMessagesReceived;
        _store.Changed -= OnStoreChanged;
    }
}
```

**Interop + design notes for the implementer:**
- `MessageLog.empty<EventLogRowViewModel>(5000)` / `MessageLog.ingest(...)`: F# module functions surface as static methods; the tuple return is `Tuple<MessageLog<T>, LogEntry<T>[]>`. If the generic type-argument syntax on `empty` fights C#, an explicitly-typed local (`MessageLog<EventLogRowViewModel> log = MessageLog.empty(5000)`) resolves inference.
- Host display name is captured at ingest; a later host rename does not retro-rename old rows (recorded decision — a log is a record of what was).
- `MessageKey` is the reconciler key AND the row identity; steady-state batches diff to pure end-Inserts, so the grid never resets and Following can scroll on Add events.
- Design 2c: "New rows do NOT animate while Following" — nothing to do in this wave (no animations exist yet); Wave 3 must gate row-enter motion on `IsFollowing` (leave a one-line note in the PR body for #32).

- [ ] **Step 4: Run tests** — Expected: PASS (7).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(app): EventLogViewModel — MessageLog fold, pills/search, unread counter"
```

---

### Task 5: `EventLogView` (XAML, tints, Following)

**Files:**
- Create: `src/Lattice.App/Views/EventLogView.axaml`
- Create: `src/Lattice.App/Views/EventLogView.axaml.cs`
- Create: `tests/Lattice.App.Tests/Headless/EventLogViewTests.cs`

- [ ] **Step 1: Failing headless tests** (TasksViewTests idiom):

1. Warning row carries class `warning` (tint `#FFF9F5` via `LatticeWarningTintSoftBrush`) and error rows `error`; assert via row Classes after render.
2. Pill toggle off removes rows (settle on row count / expected text).
3. Following: appending a message batch scrolls the last row into view (assert via `Grid.ScrollIntoView` effect — settle on the last row being realized; if realization proves un-assertable headlessly, assert the VM/view contract instead: the view called ScrollIntoView — put the call behind a small overridable seam and observe it, per the fake's-observed-calls determinism canon).
4. Scrolling away from the bottom flips the VM's `IsFollowing` to false (drive the ScrollViewer offset programmatically).

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: Implement.** Structure (stamped command-bar Border, no M3 buttons; design 2c):

- **Filter row** (in the command bar, left side after the title): three `ToggleButton` pills bound two-way to `ShowInfo`/`ShowWarning`/`ShowError` (`Classes="pill"`; selected style = brand tint `LatticeAccentTintBrush` + brand border + check icon — add the pill style to the view's Styles block), search `TextBox` (stamp FilterBox), spacer, **Following control**: a `ToggleButton` bound to `IsFollowing` whose content swaps between `Strings.EventLogFollowing` and `Strings.EventLogResumeFollowing` (`IsVisible` pair or a converter — stamp the UpdatedText dual-TextBlock trick from TasksView.axaml:74-79), copy `Button` calling `CopyVisibleCommand` — add that command to the VM: joins visible rows as `timestamp\thost\tbody` lines and sets the clipboard via `TopLevel.GetTopLevel(view)?.Clipboard` (view-layer concern: implement in code-behind wiring, keep the VM clipboard-free).
- **Grid columns:** Time 110 (monospace) · Host 84 (`IsVisible="{Binding IsAllHostsScope}"`) · Project 120 · Message * (monospace, `TextTrimming` off, single-line). Monospace = `FontFamily="Consolas, Menlo, DejaVu Sans Mono, monospace"` (design says Consolas; the fallback chain is the cross-platform reading — Consolas is Windows-only, record in a XAML comment).
- **Row treatment:** `MinHeight` 26 via the compact-like row style; warning/error row classes with tint brushes + 16px filled priority icon in the Time cell (`IconWarningFilled` exists; add `IconErrorCircleFilled` following the icon-sourcing convention). Rows 26px: if the `lattice` DataGrid theme's row MinHeight fights 26px, add a `Selector="DataGrid.eventlog DataGridRow"` height style — geometry-assert it (M2c-1 lesson).
- **No partial-results InfoBar and no loading skeleton** (design 2c defines neither for this view; reachability lives in the status-bar counts — recorded decision, cite design 2c in the PR body).
- **Status bar:** `LeftText="{Binding CountsText}"`, right text = `Strings.EventLogFollowingLive` shown only while `IsFollowing` (design 2c "Following live"; empty otherwise — this VM has no polling text by design, messages arrive with the poll stream).
- **Code-behind:** row classes via the LoadingRow/UnloadingRow liveness stamp:

```csharp
private static void ApplyRowClasses(DataGridRow row, EventLogRowViewModel data)
{
    row.Classes.Set("warning", data.Priority == EventLogPriority.Warning);
    row.Classes.Set("error", data.Priority == EventLogPriority.Error);
}
```

plus Following wiring:

```csharp
// Rows.CollectionChanged (Add) + IsFollowing → ScrollIntoView(last).
// ScrollViewer.ScrollChanged with a user-initiated offset away from the
// bottom → vm.IsFollowing = false ("Resume following" appears).
// Guard the feedback loop: programmatic ScrollIntoView must not clear
// IsFollowing — set a _autoScrolling flag around the call and ignore
// ScrollChanged while it's set.
```

Write that guard exactly as described (flag around the programmatic scroll); it is the one concurrency-ish trap in this view.

resx additions (`EventLog` prefix): `EventLogTitle` = `Event log`, `EventLogSubtitleAll` = `All hosts · merged stream`, `EventLogPillInfo` = `Info`, `EventLogPillWarning` = `Warning`, `EventLogPillError` = `Error`, `EventLogFollowing` = `Following`, `EventLogResumeFollowing` = `Resume following`, `EventLogCopy` = `Copy`, `EventLogCountsFmt` = `{0} messages · {1} reachable hosts`, `EventLogFollowingLive` = `Following live`, `EventLogEmpty` = `No messages yet.`

- [ ] **Step 4: Run headless tests** — Expected: PASS (4).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(app): EventLogView — merged stream grid, pills, Following auto-scroll"
```

---

### Task 6: Shell wiring + InfoBadge + journey + wrap-up

**Files:**
- Modify: `src/Lattice.App/ViewModels/ShellViewModel.cs`, `src/Lattice.App/Views/ShellWindow.axaml`
- Modify: `tests/Lattice.App.Tests/ShellViewModelTests.cs`
- Create: `tests/Lattice.App.Tests/Headless/Journeys/EventLogJourney.cs`

- [ ] **Step 1: Shell wiring (red-first via ShellViewModelTests):**

`ShellViewModel.cs`:
- ctor: `EventLog = new EventLogViewModel(store);`, `Views[3]` placeholder → `EventLog`; property `public EventLogViewModel EventLog { get; }`.
- `OnScopeChanged`: add `EventLog.Scope = value;`
- `OnSelectedViewChanged` / `NavigateToSettings`: set `EventLog.IsViewActive = ReferenceEquals(CurrentPage, EventLog)` whenever `CurrentPage` changes — put it in a `partial void OnCurrentPageChanged(object value)` so every page-change path hits it.
- Mirror for the badge, exactly like the TasksCount mirror: subscribe `EventLog.PropertyChanged` for `nameof(EventLogViewModel.UnreadCount)` → `[ObservableProperty] int eventLogUnread` + `HasEventLogUnread`.
- `Dispose`: `EventLog.Dispose();` + unsubscribe the mirror.

`ShellWindow.axaml`:
- DataTemplate block: `<DataTemplate DataType="vm:EventLogViewModel"><v:EventLogView /></DataTemplate>`.
- `NavEventLog` item gains the badge:

```xml
<ui:FANavigationViewItem.InfoBadge>
  <ui:InfoBadge Value="{Binding EventLogUnread}" IsVisible="{Binding HasEventLogUnread}" />
</ui:FANavigationViewItem.InfoBadge>
```

(`FANavigationViewItem.InfoBadge` confirmed in FluentAvalonia 3.0.1's API surface at plan time; the badge control's exact type name inside the FA namespace — `InfoBadge` vs `FAInfoBadge` — is a 1-minute decompile/docs check, same method as the FAInfoBar.Closed fact.)

Tests: scope push to EventLog; unread mirror updates; navigating to the Event log page zeroes the badge.

- [ ] **Step 2: Journey** (`EventLogJourney.cs`): host connects → fake emits messages incl. one UserAlert → badge shows 1 while on Tasks → navigate to Event log (SelectView "3") → badge clears, rows visible with warning row → fake replays the same seqnos (reconnect simulation: manager re-raises the batch) → row count unchanged (the dedup acceptance, end-to-end) → second host's messages interleave by time in All-hosts scope.

This journey is the PR's acceptance centerpiece — it exercises the entire new data path (Monitor event → HostStore marshal → MessageLog dedup → grid) including the replay case that motivated the model.

- [ ] **Step 3: Full suite** Debug+Release, `-warnaserror`; LocalizationTests green.

- [ ] **Step 4: Commit, rebase on main, push branch `m2c2-w2d-eventlog-view`, open PR** (body: issue #31 Event-log leg; explain the identity-keyed set-ingest decision and why the Core event needed no replace marker; note zero HostMonitor/HostMachine changes; flag the Wave-3 note about gating row-enter motion on Following). Trigger `@codex review`, pr-monitor, read raw threads yourself (≥60 s re-poll), adjudicate red-first, merge on clean.

```bash
git commit -m "feat(app): Event log wired into shell with unread InfoBadge + journey"
```

---

## Explicitly out of scope

- Any `Lattice.Core` change (`HostMonitor.cs`, `MessageLog.cs` in Core, `HostMachine.fs`) — the Core-side ring buffer and its replace semantics model "the daemon's current buffer" and stay as-is; the App-side model owns viewing history.
- Row-enter animation gating (Wave 3, #32 — noted in the PR body).
- Message-body multi-line expansion, copy-single-row context menu (post-M2 candy; file an issue only if the walkthrough demands it).
- Projects / Transfers views (parallel plans).
- Per-view column breakpoints: design §Responsive (2f) names only Tasks columns; the Event-log column set (314px + message star) fits the 1000px minimum window, so no width-driven column hiding ships here (design-authoritative adjudication — cite 2f in the PR body).

## Self-review notes (already applied)

- Design 2c coverage: no in-view host tabs (scope from rail) ↔ VM Scope; Host column 84px all-hosts-only ↔ Task 5 columns; pills + search + Following + copy ↔ Tasks 4-5; 26px monospace rows + warning/error tints + filled icons ↔ Task 5; 5,000/host retention ↔ `RetentionPerHost` + Task 1 capacity tests; status bar strings ↔ `EventLogCountsFmt`/`EventLogFollowingLive`; virtualization ↔ DataGrid default + reconciler end-inserts; InfoBadge unread ↔ Tasks 4/6.
- The reconnect-dedup acceptance is tested at three levels: F# property (idempotence), VM test 1 (replayed batch), journey Step 2 (end-to-end).
- Known judgment calls the executor must not "fix": timestamp-in-key identity (seqno reset is real — see the MessageKey doc comment); history retention across daemon restarts (deliberate divergence from Core's replace semantics); host-name capture at ingest; unknown MessagePriority degrades to Info.
- API risks front-loaded: InfoBadge type name (1-minute check), 26px row height vs theme MinHeight (geometry assert), ScrollIntoView feedback loop (explicit flag guard).
