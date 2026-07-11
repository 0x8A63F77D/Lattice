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

    // Episode semantics live in PartialBarPolicy, the call protocol (current/
    // dismissed fingerprints, scope gate) in PartialBarState; this class only
    // holds the instance.
    private readonly PartialBarState _partialBar = new();

    public ProjectsViewModel(HostStore store, IUiClock clock, UiStateStore uiStateStore)
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
    // (ObservableCollection<T> is invariant, and ProjectRow is a closed subclass
    // for XAML's benefit, not a type substitution). The factory below still
    // creates real ProjectRow instances, so DataContext at the view layer is
    // always a ProjectRow at runtime.
    public ObservableCollection<RowHolder<ProjectRowKey, ProjectRowViewModel>> Rows { get; } = [];

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

    /// <summary>Density toggle for the Projects DataGrid (compact = smaller row height).
    /// Restored from UiStateStore at construction; persisted on every change.</summary>
    [ObservableProperty] private bool _isCompact;

    partial void OnIsCompactChanged(bool value)
    {
        _uiState = _uiState with { CompactDensity = value };
        _uiStateStore.Save(_uiState); // best-effort: a failed save costs only persistence
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

    // The clock tick only ever moves freshness text/staleness forward, but
    // Rebuild recomputes everything together (no partial invalidation) rather
    // than special-casing a freshness-only path.
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

        // Keyed reconcile instead of replace: in-place Data updates keep holder
        // identity (DataGrid selection) and steady-state polls raise no
        // CollectionChanged at all (issue #24). A collapse/expand edits only the
        // affected child rows — the parent holder survives.
        var existing = Rows.Select(h => (h.Key, h.Data)).ToArray();
        CollectionReconciler.Apply(Rows, Reconcile.diff(existing, target),
            (key, row) => new ProjectRow(key, row));

        CountsText = string.Format(Strings.ProjectsCountsFmt, groups.Length, slice.CoveredIds.Count);
        PollingText = string.Format(Strings.PollingFmt, _store.PollingIntervalSeconds);

        // Oldest of the scoped snapshots: the pessimistic "how stale could this
        // be" reading, not the newest arrival.
        UpdatedText = slice.OldestTimestamp is { } oldest ? TimeText.UpdatedAgo(oldest, _clock.Now) : "";
        IsUpdateStale = slice.IsUpdateStale;

        // Partial-results bar: UnreachableIds spans ALL hosts (episode
        // semantics), CoveredIds counts exactly the hosts feeding the grid —
        // both decided in ViewSlice, not recomputed here (Codex P2).
        ShowPartialBar = _partialBar.Advance(slice.UnreachableIds, slice.CoveredIds, Scope.IsAllHosts);
        if (ShowPartialBar)
        {
            PartialBarText = string.Format(
                Strings.PartialFmt, slice.UnreachableIds.Count, _store.Hosts.Count, slice.CoveredIds.Count);
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
    }

    public void Dispose()
    {
        _store.Changed -= OnStoreChanged;
        _clock.Tick -= OnTick;
    }
}
