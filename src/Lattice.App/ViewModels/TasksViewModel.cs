using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lattice.App.Aggregation;
using Lattice.App.Infrastructure;
using Lattice.App.Localization;

namespace Lattice.App.ViewModels;

/// <summary>
/// Scopes, merges, filters, sorts and counts tasks across one or all hosts for
/// the Tasks view DataGrid. Takes (HostStore, IUiClock, UiStateStore) only —
/// shell-agnostic and independently testable; ShellViewModel pushes
/// <see cref="Scope"/> on change (design rule: selecting a host scopes every view).
/// </summary>
public sealed partial class TasksViewModel : ObservableObject, IDisposable
{
    private readonly HostStore _store;
    private readonly IUiClock _clock;
    private readonly UiStateStore _uiStateStore;
    private ScopeSelection _scope = ScopeSelection.AllHosts;

    // The persisted UI-preference record, loaded once at construction. This VM
    // owns the load-mutate-save cycle for the fields it consumes (CompactDensity,
    // ColumnVisibility); other fields (ColumnWidths) ride along untouched so a
    // save here never clobbers state owned by a future consumer.
    private UiState _uiState;

    // The dismissed partial-bar fingerprint and the one computed on the last
    // Rebuild. Episode semantics (when a dismissal holds, when it is
    // forgotten) live in PartialBarPolicy; this class only stores the state
    // and applies the scope gate.
    private PartialBarPolicy.Fingerprint _dismissedFingerprint = PartialBarPolicy.EmptyFingerprint;
    private PartialBarPolicy.Fingerprint _currentFingerprint = PartialBarPolicy.EmptyFingerprint;

    public TasksViewModel(HostStore store, IUiClock clock, UiStateStore uiStateStore)
    {
        _store = store;
        _clock = clock;
        _uiStateStore = uiStateStore;
        _uiState = uiStateStore.Load();
        // Field write, not property: restoring the persisted choice must not
        // re-save it through OnIsCompactChanged.
        _isCompact = _uiState.CompactDensity;
        store.Changed += OnStoreChanged;
        clock.Tick += OnTick;
        Rebuild();
    }

    // Base-holder-typed collection: CollectionReconciler.Apply's generic
    // signature requires ObservableCollection<RowHolder<TKey,TRow>> exactly
    // (ObservableCollection<T> is invariant, and TaskRow is a closed subclass
    // for XAML's benefit, not a type substitution). The factory below still
    // creates real TaskRow instances, so DataContext at the view layer is
    // always a TaskRow at runtime.
    public ObservableCollection<RowHolder<TaskRowKey, TaskRowViewModel>> Rows { get; } = [];

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
    /// Restored from UiStateStore at construction; persisted on every change.</summary>
    [ObservableProperty] private bool _isCompact;

    partial void OnFilterTextChanged(string value) => Rebuild();
    partial void OnStateFilterChanged(TaskStateKind? value) => Rebuild();

    partial void OnIsCompactChanged(bool value)
    {
        _uiState = _uiState with { CompactDensity = value };
        _uiStateStore.Save(_uiState); // best-effort: a failed save costs only persistence
    }

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
        var visibility = new Dictionary<string, bool>(_uiState.ColumnVisibility) { [columnKey] = visible };
        _uiState = _uiState with { ColumnVisibility = visibility };
        _uiStateStore.Save(_uiState); // best-effort, as above
    }

    [RelayCommand]
    private void Refresh() => _store.RequestRefresh(Scope.HostId);

    [RelayCommand]
    private void DismissPartial()
    {
        _dismissedFingerprint = PartialBarPolicy.Dismiss(_currentFingerprint);
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
        IsAllHostsScope = Scope.IsAllHosts;

        // Per-host facts feed the shared F# aggregation core (ViewSlice.compute):
        // host classification, row merge, coverage/staleness are all decided
        // there so every data-view VM shares one implementation. Flags, not
        // RailState — Lattice.App.Aggregation stays free of UI-layer types.
        var facts = _store.Hosts.Select(h =>
        {
            var rail = RailStateProjection.From(h.Status);
            var inScope = Scope.IsAllHosts || h.Config.Id == Scope.HostId;
            var isRowSource = rail == RailState.Connected && h.Snapshot is not null;
            // Positional construction: the F# record's compiler-generated
            // constructor exposes camelCase parameter names (id, inScope, ...)
            // that don't match the PascalCase field names C# named-argument
            // syntax would need, so positional is the reliable path here.
            return new HostFacts<TaskRowViewModel>(
                h.Config.Id,
                inScope,
                isRowSource,
                rail is RailState.Unreachable or RailState.AuthFailed,
                rail is RailState.Retrying or RailState.Unreachable,
                isRowSource ? h.Snapshot!.Timestamp : null,
                isRowSource
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

        // Keyed reconcile instead of replace: in-place Data updates keep
        // holder identity (DataGrid selection) and steady-state polls raise
        // no CollectionChanged at all — the SequenceEqual full-replace guard
        // this superseded still fired Reset + N Adds on every 1s tick even
        // when nothing changed (issue #24).
        var existing = Rows.Select(h => (h.Key, h.Data)).ToArray();
        CollectionReconciler.Apply(Rows, Reconcile.diff(existing, target), (key, row) => new TaskRow(key, row));

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

        // Episode semantics (dismissal forgotten on full recovery; any
        // fingerprint change re-reports) are PartialBarPolicy's; the
        // All-hosts scope gate is the one piece that stays here.
        _currentFingerprint = new PartialBarPolicy.Fingerprint(unreachableIds, coveredIds);
        (PartialBarPolicy.Fingerprint dismissed, bool visible) =
            PartialBarPolicy.Advance(_dismissedFingerprint, _currentFingerprint);
        _dismissedFingerprint = dismissed;
        ShowPartialBar = Scope.IsAllHosts && visible;

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
    }

    public void Dispose()
    {
        _store.Changed -= OnStoreChanged;
        _clock.Tick -= OnTick;
    }
}
