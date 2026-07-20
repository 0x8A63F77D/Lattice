using System.Collections.ObjectModel;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lattice.App.Aggregation;
using Lattice.App.Infrastructure;
using Lattice.App.Localization;
using Lattice.App.Views;
using Lattice.Core;
using Microsoft.FSharp.Collections;
// The F# policy DUs deliberately duplicate the GuiRpc enums (Aggregation is
// GuiRpc-free by module rule); alias both so neither is ever named bare.
using AggProjectOp = Lattice.App.Aggregation.ProjectOp;
using GuiProjectOp = Lattice.Boinc.GuiRpc.ProjectOp;

namespace Lattice.App.ViewModels;

/// <summary>
/// Scopes, groups and aggregates projects across one or all hosts for the
/// Projects view's hierarchical (flattened) DataGrid. Same shape as
/// TasksViewModel minus UiStateStore — Projects persists nothing: row heights
/// are fixed by design 2a (40px parent / 32px child; the medium/compact
/// densities exist only for Tasks), and expansion is session-local. Scope is
/// pushed by ShellViewModel; ViewSlice + Reconcile pipeline.
/// </summary>
public sealed partial class ProjectsViewModel : ObservableObject, IDisposable
{
    private readonly HostStore _store;
    private readonly IUiClock _clock;
    private readonly HostControlService _control;
    // The attach-flow seam + UI dispatcher for the "Add project…" dialog (M3 PR I).
    // Null when the shell did not wire them (the pre-attach test call sites): the
    // AddProject command then stays permanently disabled — never a NRE.
    private readonly AttachFlowRun? _attachRun;
    private readonly IUiDispatcher? _ui;
    private ScopeSelection _scope = ScopeSelection.AllHosts;

    // The last Rebuild's groups, reused by the control ops to resolve a selected
    // row's blast radius (parent = every attachment host; child = its one host).
    // Rebuild runs on every store change / tick, so this stays fresh between
    // rebuilds — a selection change reads it without recomputing the aggregation.
    private ProjectGroup[] _groups = [];

    // Expansion is per project URL, session-local (not persisted — a monitoring
    // dashboard reopens collapsed; revisit only if users ask).
    private readonly HashSet<string> _expanded = [];

    // Header-click sort, session-local. DefaultSort = compute's RAC-desc order
    // (lights no header arrow). The VIEW owns display order via RowsView's single
    // sort description; ToggleSort swaps it. Only the parent AGGREGATE sorts;
    // children always follow their parent (design — encoded in
    // ProjectRows.compareRows / orderedRows, not by ordering the source here).
    private ProjectSort _sort = ProjectSort.DefaultSort;

    // Episode semantics live in PartialBarPolicy, the call protocol (current/
    // dismissed fingerprints, scope gate) in PartialBarState; this class only
    // holds the instance.
    private readonly PartialBarState _partialBar = new();

    public ProjectsViewModel(HostStore store, IUiClock clock, HostControlService control,
        AttachFlowRun? attachRun = null, IUiDispatcher? ui = null)
    {
        _store = store;
        _clock = clock;
        _control = control;
        _attachRun = attachRun;
        _ui = ui;
        RowsView = new DataGridCollectionView(Rows);
        // Install the always-on default order BEFORE the first Rebuild, so the
        // grid (which binds RowsView, not Rows) is never unsorted for a frame.
        RowsView.SortDescriptions.Add(ProjectSortDescription.For(ProjectSort.DefaultSort));
        store.Changed += OnStoreChanged;
        clock.Tick += OnTick;
        Rebuild();
    }

    // Base-holder-typed collection: CollectionReconciler.Apply's generic
    // signature requires ObservableCollection<RowHolder<TKey,TRow>> exactly
    // (ObservableCollection<T> is invariant, and ProjectRow is a closed subclass
    // for XAML's benefit, not a type substitution). The factory below still
    // creates real ProjectRow instances, so DataContext at the view layer is
    // always a ProjectRow at runtime.
    public ObservableCollection<RowHolder<ProjectRowKey, ProjectRowViewModel>> Rows { get; } = [];

    /// <summary>
    /// The grid's ItemsSource: a live view over <see cref="Rows"/> whose single
    /// sort description (index 0) is swapped by <see cref="ToggleSort"/>. The grid
    /// binds this rather than Rows so the native header sort arrows light (via the
    /// description's PropertyPath ↔ column SortMemberPath) and the VIEW owns
    /// display order — the source collection stays in reconcile-friendly order.
    /// </summary>
    public DataGridCollectionView RowsView { get; }

    /// <summary>Pushed by ShellViewModel whenever the global rail scope changes.</summary>
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

    /// <summary>Why "Add project…" is disabled (DI-3 tooltip on the disabled
    /// button), or null when it is enabled. Only set when the attach flow is wired
    /// (production): the scope has no Connected host to attach on.</summary>
    [ObservableProperty] private string? _addProjectDisabledReason;

    // --- M3 control ops (design 2.5; DI-1/DI-2/DI-3). A parent row acts on ALL
    //     of its attachment hosts; a child row on its one host. Flow per command:
    //     build the F# ControlIntent (carrying the blast-radius host count) →
    //     ConfirmationPolicy.classify → Instant executes, Confirm consults the
    //     dialog seam first with a body that enumerates the hosts (the DI-2
    //     receipt). Fan-out is sequential; failures aggregate into one surface
    //     message naming the failed hosts. The dialog is never constructed here:
    //     ProjectsView assigns ConfirmationHandler (headless tests fake it). ---

    /// <summary>Failure surface behind the view's dismissible InfoBar: last
    /// control-op failure only; any all-succeeded op clears it.</summary>
    public ControlFailureSurface ControlFailure { get; } = new();

    /// <summary>
    /// The dialog seam: shows a confirmation and resolves to the user's choice.
    /// Assigned by ProjectsView (production: <see cref="ConfirmationDialog.ConfirmAsync(Avalonia.Controls.TopLevel, ConfirmationRequest)"/>);
    /// null (nothing wired yet) fails SAFE — Confirm-class ops decline.
    /// </summary>
    public Func<ConfirmationRequest, Task<bool>>? ConfirmationHandler { get; set; }

    /// <summary>The grid's selected row holder (TwoWay-bound to DataGrid.SelectedItem).
    /// Typed object: the grid hands back the RowHolder-derived ProjectRow.</summary>
    [ObservableProperty] private object? _selectedRow;

    /// <summary>Why the control commands are disabled (DI-3 tooltip), or null when
    /// they are enabled or nothing is selected.</summary>
    [ObservableProperty] private string? _controlDisabledReason;

    private ProjectRowViewModel? SelectedProjectRow =>
        (SelectedRow as RowHolder<ProjectRowKey, ProjectRowViewModel>)?.Data;

    // The host ids a command on the current selection would act on — enablement
    // only; recomputed on selection change and every Rebuild so a host dropping
    // out flips enablement live. Execution re-resolves via ResolveTarget.
    private IReadOnlyList<Guid> _selectedHostIds = [];

    partial void OnSelectedRowChanged(object? value) => RefreshControlState();

    // DI-3 / G2: enabled only while EVERY covered host is Connected — never
    // queued, and no partial fan-out to a reachable subset (the receipt must match
    // reality). A parent's covered set is its group's current attachments; a child
    // its own host.
    private bool CanControlSelected() =>
        _selectedHostIds.Count > 0 && _selectedHostIds.All(IsHostConnected);

    private bool IsHostConnected(Guid hostId) =>
        _store.Hosts.FirstOrDefault(h => h.Config.Id == hostId)?.Status.State
            == HostConnectionState.Connected;

    [RelayCommand(CanExecute = nameof(CanControlSelected))]
    private Task UpdateSelectedAsync() =>
        RunProjectOpAsync(AggProjectOp.ProjectUpdate, GuiProjectOp.Update, Strings.ProjectsUpdate);

    [RelayCommand(CanExecute = nameof(CanControlSelected))]
    private Task SuspendSelectedAsync() =>
        RunProjectOpAsync(AggProjectOp.ProjectSuspend, GuiProjectOp.Suspend, Strings.Suspend);

    [RelayCommand(CanExecute = nameof(CanControlSelected))]
    private Task ResumeSelectedAsync() =>
        RunProjectOpAsync(AggProjectOp.ProjectResume, GuiProjectOp.Resume, Strings.Resume);

    [RelayCommand(CanExecute = nameof(CanControlSelected))]
    private Task DetachSelectedAsync() =>
        RunProjectOpAsync(AggProjectOp.ProjectDetach, GuiProjectOp.Detach, Strings.Detach);

    private async Task RunProjectOpAsync(AggProjectOp intentOp, GuiProjectOp wireOp, string opLabel)
    {
        if (SelectedProjectRow is not { } row || ResolveTarget(row) is not { } target)
            return;

        var intent = ControlIntent.NewOfProject(intentOp, target.Hosts.Count);
        if (ConfirmationPolicy.classify(intent) is ConfirmationClass.Confirm confirm)
        {
            var request = BuildConfirmation(intentOp, opLabel, confirm.Item, target);
            if (ConfirmationHandler is not { } confirmationHandler || !await confirmationHandler(request))
                return;
        }

        // Sequential fan-out across the target hosts (they are user-initiated and
        // rare; the per-host control lane serializes each anyway). Aggregate the
        // failures into one surface message so a two-host partial failure is one
        // InfoBar naming the host that failed, not two racing reports.
        var failures = new List<(string HostName, string Error)>();
        foreach (var attachment in target.Hosts)
        {
            var result = await _control.PerformProjectOpAsync(attachment.HostId, wireOp, target.ProjectUrl);
            if (result.Outcome != ControlOpOutcome.Succeeded)
                failures.Add((attachment.HostName, result.Error ?? ""));
        }

        if (failures.Count == 0)
            ControlFailure.Clear();
        else
            ControlFailure.Report(
                string.Format(Strings.ControlOpFailedTitleFmt, opLabel),
                FormatFailures(failures, target.Hosts.Count));
        // Success needs no optimistic mutation: each op nudged its monitor, so the
        // grid converges on the next poll tick (~1 s — project status is live in
        // the tick since PR C / DI-5, #127).
    }

    // Single-host target: the bare error (matches the Tasks surface). Multi-host:
    // prefix each failure with its host name so the receipt says which host failed.
    private static string FormatFailures(
        IReadOnlyList<(string HostName, string Error)> failures, int targetCount) =>
        targetCount == 1
            ? failures[0].Error
            : string.Join("; ", failures.Select(f => $"{f.HostName}: {f.Error}"));

    // The dialog CONTENT (DI-2's host enumeration) is the view layer's job — this
    // is where it lives for Projects. Detach is Destructive at any host count and
    // states the in-progress-task loss; a reversible multi-host op is Caution and
    // is purely the blast-radius receipt.
    private static ConfirmationRequest BuildConfirmation(
        AggProjectOp intentOp, string opLabel, ConfirmSeverity severity, ControlTarget target)
    {
        var hostList = string.Join(", ", target.Hosts.Select(a => a.HostName));
        var count = target.Hosts.Count;
        if (intentOp.IsProjectDetach)
        {
            var body = count > 1
                ? string.Format(Strings.ProjectDetachConfirmMultiBodyFmt, target.ProjectName, count, hostList)
                : string.Format(Strings.ProjectDetachConfirmBodyFmt, target.ProjectName, target.Hosts[0].HostName);
            return new ConfirmationRequest(Strings.ProjectDetachConfirmTitle, body, opLabel, severity);
        }
        // classify only returns Caution here for count > 1 (single-host reversible
        // ops are Instant and never reach this method).
        return new ConfirmationRequest(
            string.Format(Strings.ProjectMultiHostConfirmTitleFmt, opLabel, count),
            string.Format(Strings.ProjectMultiHostConfirmBodyFmt, opLabel, target.ProjectName, count, hostList),
            opLabel, severity);
    }

    // The blast radius of a selected row: its project + the hosts to act on. A
    // parent row (HostId == null) spans every attachment in its group; a child row
    // its one host. Resolved off the last Rebuild's groups (fresh — store-change
    // driven), so it tracks hosts dropping in/out of the row-source set.
    private ControlTarget? ResolveTarget(ProjectRowViewModel row)
    {
        var group = _groups.FirstOrDefault(g => g.MasterUrl == row.MasterUrl);
        if (group is null)
            return null;
        var hosts = row.HostId is { } hostId
            ? group.Attachments.Where(a => a.HostId == hostId).ToArray()
            : group.Attachments;
        return hosts.Length == 0 ? null : new ControlTarget(group.MasterUrl, group.DisplayName, hosts);
    }

    private void RefreshControlState()
    {
        var row = SelectedProjectRow;
        _selectedHostIds = TargetHostIds(row);
        UpdateSelectedCommand.NotifyCanExecuteChanged();
        SuspendSelectedCommand.NotifyCanExecuteChanged();
        ResumeSelectedCommand.NotifyCanExecuteChanged();
        DetachSelectedCommand.NotifyCanExecuteChanged();
        // A disabled command with nothing (still) selectable is self-explanatory
        // (reason null); a live selection whose host went offline gets the tooltip.
        // Only a CHILD reaches this: a parent's covered set is its group's current
        // attachments, which are Connected by construction (offline hosts have
        // already dropped from the aggregate) — so a parent is either enabled or
        // its group is gone (reason null), never disabled-with-reason.
        ControlDisabledReason = _selectedHostIds.Count > 0 && !CanControlSelected()
            ? Strings.ControlHostNotConnected
            : null;

        // "Add project…" enablement (DI-3): a Connected host must exist in scope;
        // when attach is wired but none is, surface the reason as a tooltip.
        AddProjectCommand.NotifyCanExecuteChanged();
        AddProjectDisabledReason = _attachRun is not null && !ConnectableHosts().Any()
            ? Strings.ProjectsAddProjectDisabledReason
            : null;
    }

    // Hosts a command on the selected row would act on, for ENABLEMENT. A child
    // resolves to its own host directly (so a stale selection still evaluates
    // after the host leaves the grid); a parent to its group's current
    // attachments (the receipt-matches-reality set — offline hosts have already
    // dropped out). Execution re-resolves via ResolveTarget for url / names.
    private IReadOnlyList<Guid> TargetHostIds(ProjectRowViewModel? row)
    {
        if (row is null)
            return [];
        if (row.HostId is { } childHostId)
            return [childHostId];
        var group = _groups.FirstOrDefault(g => g.MasterUrl == row.MasterUrl);
        return group is null ? [] : group.Attachments.Select(a => a.HostId).ToArray();
    }

    private sealed record ControlTarget(
        string ProjectUrl, string ProjectName, IReadOnlyList<ProjectAttachment> Hosts);

    // --- M3 PR I: "Add project…" entry. The dialog itself is opened by the shell
    //     window (FA dialog constructed only in the view layer); this raises a
    //     ready-built dialog VM with the scope-resolved host options. ---

    /// <summary>Raised when "Add project…" is clicked, carrying a ready-built
    /// dialog view model (host options + locked scope resolved here).</summary>
    public event EventHandler<AttachProjectViewModel>? AddProjectRequested;

    // DI-3: attach targets a Connected host only. All-hosts scope offers every
    // Connected host; a single-host scope offers just that host (locked below).
    private IEnumerable<HostEntry> ConnectableHosts() =>
        (Scope.IsAllHosts ? _store.Hosts : _store.Hosts.Where(h => h.Config.Id == Scope.HostId))
            .Where(h => IsHostConnected(h.Config.Id));

    private bool CanAddProject() => _attachRun is not null && _ui is not null && ConnectableHosts().Any();

    [RelayCommand(CanExecute = nameof(CanAddProject))]
    private void AddProject()
    {
        if (_attachRun is null || _ui is null)
            return;
        // Materialize ONCE: CanExecute already checked ConnectableHosts, but a host
        // can drop between that check and this body (background poll). Bail rather
        // than open an empty, unsubmittable dialog.
        var connectable = ConnectableHosts().ToList();
        if (connectable.Count == 0)
            return;
        var options = connectable
            .Select(h => new AttachHostOption(h.Config.Id, h.Config.DisplayName)).ToList();
        // A single-host scope locks the picker to itself; All-hosts leaves it open.
        Guid? locked = Scope.IsAllHosts ? null : Scope.HostId;
        AddProjectRequested?.Invoke(this, new AttachProjectViewModel(_attachRun, options, locked, _ui));
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

    /// <summary>
    /// Header-click sort, driven from the view's Sorting handler. Re-clicking the
    /// same column flips direction; a new column selects it ascending. Only the
    /// parent aggregate sorts — child (per-host) rows stay grouped under their
    /// parent. No Rebuild: the row SET is unchanged, so this only swaps the view's
    /// single sort description; the view re-sorts itself and refreshes the header
    /// arrows on every column.
    /// </summary>
    public void ToggleSort(ProjectSortColumn column)
    {
        _sort = ProjectRows.toggleSort(column, _sort);
        // Replacing SortDescriptions[0] (an AvaloniaList indexer) raises one
        // Replace event ⇒ one view refresh + a header pseudo-class update on
        // every column. Never Add a second description — the view carries exactly
        // one, swapped in place.
        RowsView.SortDescriptions[0] = ProjectSortDescription.For(_sort);
    }

    /// <summary>The active sort (DefaultSort = RAC-descending, no header arrow), so
    /// tests and the view can read the current column/direction.</summary>
    public ProjectSort Sort => _sort;

    private void OnStoreChanged(object? sender, EventArgs e) => Rebuild();

    // The clock tick only ever moves freshness text/staleness forward, but
    // Rebuild recomputes everything together (no partial invalidation) rather
    // than special-casing a freshness-only path.
    private void OnTick(object? sender, EventArgs e) => Rebuild();

    private void Rebuild()
    {
        var scoped = Scope.IsAllHosts
            ? _store.Hosts
            : _store.Hosts.Where(h => h.Config.Id == Scope.HostId).ToList();
        IsAllHostsScope = Scope.IsAllHosts && _store.Hosts.Count > 1;

        // Facts projection (incl. the inScope && isRowSource gate) lives once
        // in ViewSliceProjection (w2a2) — this callback only shapes rows.
        var slice = ViewSliceProjection.Compute(_store.Hosts, Scope, AttachmentsOf);

        var groups = ProjectRows.compute(slice.AllRows);
        // Cache for the control ops' blast-radius resolution (parent = every
        // attachment host); refreshed here on every store change / tick.
        _groups = groups;

        // Canonical display order is decided by the pure core (orderedRows): only
        // the parent aggregate sorts, children follow their parent. The grid's
        // RowsView re-sorts to match via ProjectSortDescription — the C# here only
        // reconciles the row SET. Map each ordered slot 1:1 to a keyed holder.
        var slots = ProjectRows.orderedRows(_sort, IsAllHostsScope, SetModule.OfSeq(_expanded), groups);
        var target = slots.Select(slot => slot switch
        {
            RowSlot.ParentSlot p =>
                ProjectRowViewModel.Parent(p.group, IsAllHostsScope) with { IsExpanded = p.isExpanded },
            RowSlot.ChildSlot c => ProjectRowViewModel.Child(c.group, c.attachment),
            _ => throw new InvalidOperationException("unreachable: closed DU"),
        }).Select(r => (r.Key, r)).ToArray();
        var canonicalKeys = target.Select(t => t.Key).ToArray();

        // Align the target so surviving keys keep their current source order: the
        // diff then emits no Move, so nothing churns through the applier's
        // Move→Remove+Insert translation (RowsView owns display order here). Keyed
        // reconcile keeps holder identity (DataGrid selection) and lets
        // steady-state polls raise no CollectionChanged at all (issue #24).
        var existingKeys = Rows.Select(h => h.Key).ToArray();
        var aligned = Reconcile.alignToExisting(existingKeys, target);
        var existing = Rows.Select(h => (h.Key, h.Data)).ToArray();
        CollectionReconciler.Apply(Rows, Reconcile.diff(existing, aligned),
            (key, row) => new ProjectRow(key, row));

        // The view does NO live shaping: an in-place Update can change a
        // sort-relevant value without the view re-sorting, and survivors are never
        // re-compared. Compare the view's displayed keys to the canonical order and
        // Refresh only on a mismatch — conditional so steady-state polls stay
        // zero-event (issue #24), yet a value change that reorders is honored.
        if (!RowsView.Cast<ProjectRow>().Select(r => r.Key).SequenceEqual(canonicalKeys))
            RowsView.Refresh();

        CountsText = string.Format(Strings.ProjectsCountsFmt, groups.Length, slice.CoveredIds.Count);
        PollingText = string.Format(Strings.PollingFmt, _store.PollingIntervalSeconds);

        // Oldest of the scoped snapshots: the pessimistic "how stale could this
        // be" reading, not the newest arrival.
        UpdatedText = slice.OldestTimestamp is { } oldest ? TimeText.UpdatedAgo(oldest, _clock.Now) : "";
        IsUpdateStale = slice.IsUpdateStale;

        // Partial-results bar: UnreachableIds spans ALL hosts (episode
        // semantics), CoveredIds counts exactly the hosts feeding the grid —
        // both decided in ViewSlice, not recomputed here (Codex P2).
        ShowPartialBar = _partialBar.Advance(slice.UnreachableIds, slice.CoveredIds, IsAllHostsScope);
        if (ShowPartialBar)
        {
            // Projects-specific copy ("projects below cover ..."): the shared
            // PartialFmt says "tasks below", wrong on this page (Codex P3).
            PartialBarText = string.Format(
                Strings.ProjectsPartialFmt, slice.UnreachableIds.Count, _store.Hosts.Count, slice.CoveredIds.Count);
        }

        // Overlay choice (loading skeleton vs empty message vs neither) is
        // TasksOverlayPolicy's; all-terminal scopes get neither (Codex P2).
        (IsLoading, IsEmpty) = TasksOverlayPolicy.Decide(
            [.. scoped.Select(h => new TasksOverlayPolicy.HostFacts(
                RailStateProjection.From(h.Status), h.Snapshot is not null))],
            groups.Length > 0);
        LoadingText = IsLoading
            ? string.Format(Strings.LoadingFromFmt, string.Join(", ", scoped.Select(h => h.Config.DisplayName)))
            : "";

        // Connection states / the row-source set may have changed (Rebuild runs on
        // every store change / tick): re-derive the DI-3 enablement and its tooltip.
        RefreshControlState();
    }

    // The per-host attachment projection ViewSlice feeds to ProjectRows.compute;
    // shared by Rebuild and (via _groups) the control ops' target resolution.
    private static ProjectAttachment[] AttachmentsOf(HostEntry h) =>
        h.Snapshot!.Projects.Select(p => new ProjectAttachment(
            p.Project.MasterUrl, p.Project.ProjectName,
            h.Config.Id, h.Config.DisplayName, p.TaskCount,
            p.Project.ResourceShare,
            p.Project.HostExpavgCredit, p.Project.HostTotalCredit,
            p.Project.SuspendedViaGui, p.Project.DontRequestMoreWork)).ToArray();

    public void Dispose()
    {
        _store.Changed -= OnStoreChanged;
        _clock.Tick -= OnTick;
    }
}
