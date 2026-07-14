using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lattice.App.Aggregation;
using Lattice.App.Infrastructure;
using Lattice.App.Localization;

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

    public TasksViewModel(HostStore store, IUiClock clock, UiStateStore uiStateStore, DensityPreference density)
    {
        _store = store;
        _clock = clock;
        _uiStateStore = uiStateStore;
        _uiState = uiStateStore.Load();
        _density = density;
        // Field write, not property: restoring the shared preference must not
        // re-save it through OnIsCompactChanged.
        _isCompact = density.Value;
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
            h => h.Snapshot!.Tasks.Select(t => TaskRowViewModel.From(t, h.Config.Id, h.Config.DisplayName)).ToArray());
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
    }

    public void Dispose()
    {
        _store.Changed -= OnStoreChanged;
        _clock.Tick -= OnTick;
        _density.Changed -= OnDensityChanged;
    }
}
