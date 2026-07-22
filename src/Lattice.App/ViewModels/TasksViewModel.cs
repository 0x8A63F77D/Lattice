using System.Collections.ObjectModel;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lattice.App.Aggregation;
using Lattice.App.Infrastructure;
using Lattice.App.Localization;
using Lattice.App.Views;
using Lattice.Core;
// The F# policy DUs deliberately duplicate the GuiRpc enums (Aggregation is
// GuiRpc-free by module rule); alias both so neither is ever named bare.
using AggTaskOp = Lattice.App.Aggregation.TaskOp;
using GuiTaskOp = Lattice.Boinc.GuiRpc.TaskOp;

namespace Lattice.App.ViewModels;

/// <summary>
/// Scopes, merges, filters, sorts and counts tasks across one or all hosts for
/// the Tasks view DataGrid. Takes (HostStore, IUiClock, UiStateStore,
/// DensityPreference) only — shell-agnostic and independently testable;
/// ShellViewModel pushes <see cref="Scope"/> on change (design rule: selecting
/// a host scopes every view).
/// </summary>
public sealed partial class TasksViewModel : ObservableObject, IDisposable
{
    private readonly HostStore _store;
    private readonly IUiClock _clock;
    private readonly UiStateStore _uiStateStore;
    private readonly DensityPreference _density;
    private readonly HostControlService _control;
    private ScopeSelection _scope = ScopeSelection.AllHosts;

    // The persisted UI-preference record, loaded once at construction and used
    // for DISPLAY reads only (GetColumnPreference — ColumnVisibility). Every
    // write funnels through UiStateStore.Update, which re-loads fresh before
    // mutating — this cached copy would otherwise go stale the moment another
    // UiStateStore consumer saves its own preference, and a save from here
    // would clobber it (Codex P2, PR #45). Density itself is no longer read
    // from here: DensityPreference (shared with TransfersViewModel) is the
    // single owner of that field (Codex round-3 P2, PR #45).
    private UiState _uiState;

    // Episode semantics live in PartialBarPolicy, the call protocol (current/
    // dismissed fingerprints, scope gate) in PartialBarState; this class only
    // holds the instance.
    private readonly PartialBarState _partialBar = new();

    // The default (no-header-click) display order, view-owned (issue #86): the
    // deadline-ascending-nulls-last ordering Rebuild used to impose on the
    // source collection. FromComparer carries a null PropertyPath, so no header
    // arrow lights until the user clicks a column — a click routes through the
    // grid's built-in ProcessSort, which clears this default and installs the
    // clicked column's FromPath sort in its place (flat grid: no custom
    // DataGridSortDescription subclass needed, unlike Projects' hierarchy).
    private static readonly DataGridSortDescription DefaultOrder =
        DataGridSortDescription.FromComparer(Comparer<object>.Create(static (x, y) =>
        {
            var a = ((RowHolder<TaskRowKey, TaskRowViewModel>)x).Data;
            var b = ((RowHolder<TaskRowKey, TaskRowViewModel>)y).Data;
            if (a.Deadline is null) return b.Deadline is null ? 0 : 1;
            if (b.Deadline is null) return -1;
            return a.Deadline.Value.CompareTo(b.Deadline.Value);
        }));

    public TasksViewModel(
        HostStore store, IUiClock clock, UiStateStore uiStateStore, DensityPreference density,
        HostControlService control)
    {
        _store = store;
        _clock = clock;
        _uiStateStore = uiStateStore;
        _uiState = uiStateStore.Load();
        _density = density;
        _control = control;
        // Field write, not property: restoring the shared preference must not
        // re-save it through OnIsCompactChanged.
        _isCompact = density.Value;
        RowsView = new DataGridCollectionView(Rows);
        // Install the always-on default order BEFORE the first Rebuild, so the
        // grid (which binds RowsView, not Rows) is never unsorted for a frame.
        RowsView.SortDescriptions.Add(DefaultOrder);
        store.Changed += OnStoreChanged;
        clock.Tick += OnTick;
        density.Changed += OnDensityChanged;
        Rebuild();
    }

    // Base-holder-typed collection: CollectionReconciler.Apply's generic
    // signature requires ObservableCollection<RowHolder<TKey,TRow>> exactly
    // (ObservableCollection<T> is invariant, and TaskRow is a closed subclass
    // for XAML's benefit, not a type substitution). The factory below still
    // creates real TaskRow instances, so DataContext at the view layer is
    // always a TaskRow at runtime.
    public ObservableCollection<RowHolder<TaskRowKey, TaskRowViewModel>> Rows { get; } = [];

    /// <summary>
    /// The grid's ItemsSource: a live view over <see cref="Rows"/> that OWNS
    /// display order (issue #86). It starts with the default deadline order and
    /// a header click swaps in the clicked column's built-in FromPath sort; the
    /// source collection stays in reconcile-friendly order, so a poll that
    /// reorders surviving rows raises no collection event and selection rides
    /// holder identity through the view's Reset/insert paths.
    /// </summary>
    public DataGridCollectionView RowsView { get; }

    /// <summary>
    /// The scoped host's run-mode surface (M3 PR H, DI-4), pushed by ShellViewModel
    /// alongside <see cref="Scope"/>: non-null only when a single host is scoped. The
    /// command bar's "Computing" dropdown binds its items to this VM's
    /// <see cref="HostRailItemViewModel.SetRunModeCommand"/> and its visibility to
    /// <see cref="HasScopedHost"/>. Held here (not reached via a XAML window-ancestor
    /// lookup) so the dropdown's MenuFlyout — a separate popup tree — resolves its
    /// bindings through the inherited view-model DataContext.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScopedHost))]
    private HostRailItemViewModel? _scopedHost;

    /// <summary>Whether the "Computing" command-bar dropdown shows (a single host is scoped).</summary>
    public bool HasScopedHost => ScopedHost is not null;

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

    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private TaskStateKind? _stateFilter;
    [ObservableProperty] private bool _isAllHostsScope;
    [ObservableProperty] private string _countsText = "";
    [ObservableProperty] private string _atRiskText = "";
    [ObservableProperty] private string _pollingText = "";
    [ObservableProperty] private string _updatedText = "";
    [ObservableProperty] private bool _isUpdateStale;
    [ObservableProperty] private bool _showPartialBar;
    [ObservableProperty] private string _partialBarText = "";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private string _loadingText = "";

    /// <summary>Density toggle for the Tasks DataGrid (compact = smaller row height).
    /// Mirrors the shared <see cref="DensityPreference"/> for XAML binding —
    /// restored from it at construction, pushed to it on every local change,
    /// and pulled back from it whenever TransfersViewModel changes it
    /// (Codex round-3 P2, PR #45).</summary>
    [ObservableProperty] private bool _isCompact;

    partial void OnFilterTextChanged(string value) => Rebuild();
    partial void OnStateFilterChanged(TaskStateKind? value) => Rebuild();

    partial void OnIsCompactChanged(bool value) => _density.Set(value);

    // CommunityToolkit's generated setter no-ops when the new value equals the
    // current one, so pulling _density.Value back in here on every Changed
    // cannot re-enter OnIsCompactChanged / _density.Set — no feedback loop.
    private void OnDensityChanged(object? sender, EventArgs e) => IsCompact = _density.Value;

    /// <summary>
    /// The user's explicit show/hide choice for a column ("Project", "Elapsed", ...),
    /// or null if never toggled — null lets ColumnVisibilityPolicy's breakpoints
    /// decide. TasksView reads these into its preference dictionary at DataContext
    /// attach.
    /// </summary>
    public bool? GetColumnPreference(string columnKey) =>
        _uiState.ColumnVisibility.TryGetValue(columnKey, out var visible) ? visible : null;

    /// <summary>Records and persists an explicit column show/hide choice
    /// (TasksView's overflow-menu toggle is the only caller).</summary>
    public void SetColumnPreference(string columnKey, bool visible)
    {
        _uiState = _uiStateStore.Update(s =>
        {
            var visibility = new Dictionary<string, bool>(s.ColumnVisibility) { [columnKey] = visible };
            return s with { ColumnVisibility = visibility };
        });
    }

    // --- M3 control ops (design 2.5; DI-1/DI-3). Flow per command: build the
    //     F# ControlIntent → ConfirmationPolicy.classify → Instant executes,
    //     Confirm consults the dialog seam first. The dialog itself is never
    //     constructed here: the view assigns ConfirmationHandler (headless
    //     tests fake it at this boundary). ---

    /// <summary>Failure surface behind the view's dismissible InfoBar: last
    /// control-op failure only; any op success clears it.</summary>
    public ControlFailureSurface ControlFailure { get; } = new();

    /// <summary>
    /// The dialog seam: shows a confirmation and resolves to the user's choice.
    /// Assigned by TasksView (production: <see cref="ConfirmationDialog.ConfirmAsync(Avalonia.Controls.TopLevel, ConfirmationRequest)"/>);
    /// null (nothing wired yet) fails SAFE — Confirm-class ops decline.
    /// </summary>
    public Func<ConfirmationRequest, Task<bool>>? ConfirmationHandler { get; set; }

    /// <summary>The grid's selected row holder (TwoWay-bound to DataGrid.SelectedItem).
    /// Typed object: the grid hands back the RowHolder-derived TaskRow.</summary>
    [ObservableProperty] private object? _selectedRow;

    /// <summary>Why the control buttons are disabled (DI-3 tooltip), or null when
    /// they are enabled or nothing is selected (a disabled button with no
    /// selection is self-explanatory).</summary>
    [ObservableProperty] private string? _controlDisabledReason;

    private TaskRowViewModel? SelectedTask =>
        (SelectedRow as RowHolder<TaskRowKey, TaskRowViewModel>)?.Data;

    partial void OnSelectedRowChanged(object? value) => RefreshControlState();

    // DI-3: control ops are enabled ONLY while the row's host is Connected —
    // never queued, never fire-and-fail from a knowable precondition.
    private bool CanControlSelectedTask() =>
        SelectedTask is { } task && IsHostConnected(task.HostId);

    private bool IsHostConnected(Guid hostId) =>
        _store.Hosts.FirstOrDefault(h => h.Config.Id == hostId)?.Status.State
            == HostConnectionState.Connected;

    [RelayCommand(CanExecute = nameof(CanControlSelectedTask))]
    private Task SuspendSelectedAsync() =>
        RunTaskOpAsync(AggTaskOp.TaskSuspend, GuiTaskOp.Suspend, Strings.Suspend);

    [RelayCommand(CanExecute = nameof(CanControlSelectedTask))]
    private Task ResumeSelectedAsync() =>
        RunTaskOpAsync(AggTaskOp.TaskResume, GuiTaskOp.Resume, Strings.Resume);

    [RelayCommand(CanExecute = nameof(CanControlSelectedTask))]
    private Task AbortSelectedAsync() =>
        RunTaskOpAsync(AggTaskOp.TaskAbort, GuiTaskOp.Abort, Strings.Abort);

    private async Task RunTaskOpAsync(AggTaskOp intentOp, GuiTaskOp wireOp, string opLabel)
    {
        if (SelectedTask is not { } task)
            return;
        if (ConfirmationPolicy.classify(ControlIntent.NewOfTask(intentOp))
            is ConfirmationClass.Confirm confirm)
        {
            // TaskAbort is the only Confirm-class task op (DI-1 table), so the
            // request wording is the abort wording; a policy change that
            // promotes another task op re-visits this composition.
            var request = new ConfirmationRequest(
                Strings.AbortConfirmTitle,
                string.Format(Strings.AbortConfirmBodyFmt, task.Name, task.Host),
                opLabel,
                confirm.Item);
            if (ConfirmationHandler is not { } confirmationHandler
                || !await confirmationHandler(request))
                return;
        }

        ControlOpResult result =
            await _control.PerformTaskOpAsync(task.HostId, wireOp, task.ProjectUrl, task.Name);
        if (result.Outcome == ControlOpOutcome.Succeeded)
            ControlFailure.Clear();
        else
            ControlFailure.Report(
                string.Format(Strings.ControlOpFailedTitleFmt, opLabel), result.Error ?? "");
        // Success needs no optimistic mutation: the service already nudged the
        // monitor, so the grid converges on the next poll tick (~1 s).
    }

    private void RefreshControlState()
    {
        SuspendSelectedCommand.NotifyCanExecuteChanged();
        ResumeSelectedCommand.NotifyCanExecuteChanged();
        AbortSelectedCommand.NotifyCanExecuteChanged();
        ControlDisabledReason =
            SelectedTask is { } task && !IsHostConnected(task.HostId)
                ? Strings.ControlHostNotConnected
                : null;
    }

    [RelayCommand]
    private void Refresh() => _store.RequestRefresh(Scope.HostId);

    [RelayCommand]
    private void DismissPartial()
    {
        _partialBar.Dismiss();
        ShowPartialBar = false;
    }

    private void OnStoreChanged(object? sender, EventArgs e) => Rebuild();

    // The clock tick only ever moves freshness text/staleness forward, but
    // Rebuild recomputes everything together (no partial invalidation) rather
    // than special-casing a freshness-only path.
    private void OnTick(object? sender, EventArgs e) => Rebuild();

    private bool MatchesFilters(TaskRowViewModel row)
    {
        if (StateFilter is { } kind && row.StateKind != kind)
            return false;
        return FilterText.Length == 0
            || row.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase)
            || row.Project.Contains(FilterText, StringComparison.OrdinalIgnoreCase);
    }

    private void Rebuild()
    {
        var scoped = Scope.IsAllHosts
            ? _store.Hosts
            : _store.Hosts.Where(h => h.Config.Id == Scope.HostId).ToList();
        IsAllHostsScope = Scope.IsAllHosts && _store.Hosts.Count > 1;

        // Per-host facts feed the shared F# aggregation core (ViewSlice.compute)
        // through the single-copy projection: host classification, row merge,
        // coverage/staleness are all decided there so every data-view VM shares
        // one implementation. rowsOf runs only for inScope && isRowSource hosts,
        // so Snapshot is non-null inside it.
        var slice = ViewSliceProjection.Compute(_store.Hosts, Scope,
            h => h.Snapshot!.Tasks
                .Select(t => TaskRowViewModel.From(t, h.Config.Id, h.Config.DisplayName, h.Snapshot!.CcStatus))
                .ToArray());
        var allRows = slice.AllRows;

        // No OrderBy here: display order is view-owned (issue #86) — the default
        // deadline order lives in RowsView's sort description and a header click
        // swaps in the clicked column's. The source collection has no order
        // contract beyond stability.
        var target = allRows
            .Where(MatchesFilters)
            .Select(r => (r.Key, r))
            .ToArray();

        // Align the target so surviving keys keep their current source slots:
        // the diff then emits no Move at all — a source reorder is invisible to
        // the grid (RowsView owns display order) and would only churn the
        // selected row through a remove it cannot survive. New keys append; the
        // view inserts each at its sorted position. Keyed reconcile keeps holder
        // identity (DataGrid selection) and steady-state polls raise no
        // CollectionChanged at all (issue #24).
        var aligned = Reconcile.alignToExisting(Rows.Select(h => h.Key).ToArray(), target);
        var existing = Rows.Select(h => (h.Key, h.Data)).ToArray();
        CollectionReconciler.Apply(Rows, Reconcile.diff(existing, aligned), (key, row) => new TaskRow(key, row));

        // The view does NO live shaping: an in-place Update can change a
        // sort-relevant value (a deadline, or whatever column the user header-
        // sorted by) without the view re-sorting. Refresh only on a genuine
        // order violation so steady-state polls stay zero-event (issue #24).
        CollectionViewOrder.RefreshIfStale(RowsView);

        // Counts/at-risk cover the reachable, UNFILTERED set: a stable summary
        // that the text/state filter shouldn't perturb.
        var running = allRows.Count(r => r.StateKind == TaskStateKind.Running);
        var uploading = allRows.Count(r => r.StateKind == TaskStateKind.Uploading);
        var suspended = allRows.Count(r => r.StateKind == TaskStateKind.Suspended);
        CountsText = string.Format(Strings.CountsFmt, allRows.Length, running, uploading, suspended);

        var atRisk = allRows.Count(r => r.IsDeadlineAtRisk);
        AtRiskText = atRisk > 0 ? string.Format(Strings.AtRiskFmt, atRisk) : "";

        PollingText = string.Format(Strings.PollingFmt, _store.PollingIntervalSeconds);

        // Oldest of the scoped snapshots: the pessimistic "how stale could this
        // be" reading, not the newest arrival.
        UpdatedText = slice.OldestTimestamp is { } oldest ? TimeText.UpdatedAgo(oldest, _clock.Now) : "";

        IsUpdateStale = slice.IsUpdateStale;

        // "tasks below cover {2} hosts" must count the exact set Rows are
        // built from (Connected AND snapshotted; the bar only shows in the
        // All-hosts scope, so CoveredIds spans every host here) — NOT total
        // minus unreachable-tier, which would also count Retrying/Connecting
        // hosts whose tasks are not in the grid (Codex P2). This same covered
        // set also feeds the dismissal fingerprint below (Codex P2 round 2):
        // a dismissed bar must reappear when a covered host drops out, even
        // if the unreachable tier itself never changes. UnreachableIds spans
        // ALL hosts regardless of scope (episode semantics) — that scope
        // independence is ViewSlice's, not recomputed here.
        var unreachableIds = slice.UnreachableIds;
        var coveredIds = slice.CoveredIds;

        ShowPartialBar = _partialBar.Advance(unreachableIds, coveredIds, IsAllHostsScope);

        if (ShowPartialBar)
        {
            PartialBarText = string.Format(
                Strings.PartialFmt, unreachableIds.Count, _store.Hosts.Count, coveredIds.Count);
        }

        // Overlay choice (loading skeleton vs empty message vs neither) is
        // TasksOverlayPolicy's; all-terminal scopes get neither (Codex P2).
        // Task presence is the UNFILTERED set: a filter that hides every row
        // is a filter miss, not an empty task set (Codex P2).
        (IsLoading, IsEmpty) = TasksOverlayPolicy.Decide(
            [.. scoped.Select(h => new TasksOverlayPolicy.HostFacts(
                RailStateProjection.From(h.Status), h.Snapshot is not null))],
            allRows.Length > 0);
        LoadingText = IsLoading
            ? string.Format(Strings.LoadingFromFmt, string.Join(", ", scoped.Select(h => h.Config.DisplayName)))
            : "";

        // Connection states may have changed (Rebuild runs on every store
        // change/tick): re-derive the DI-3 enablement and its tooltip reason.
        RefreshControlState();
    }

    public void Dispose()
    {
        _store.Changed -= OnStoreChanged;
        _clock.Tick -= OnTick;
        _density.Changed -= OnDensityChanged;
    }
}
