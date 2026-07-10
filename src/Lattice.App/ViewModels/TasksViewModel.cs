using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lattice.App.Infrastructure;
using Lattice.App.Localization;

namespace Lattice.App.ViewModels;

/// <summary>
/// Scopes, merges, filters, sorts and counts tasks across one or all hosts for
/// the Tasks view DataGrid. Takes (HostStore, IUiClock) only — shell-agnostic
/// and independently testable; ShellViewModel pushes <see cref="Scope"/> on
/// change (design rule: selecting a host scopes every view).
/// </summary>
public sealed partial class TasksViewModel : ObservableObject, IDisposable
{
    private readonly HostStore _store;
    private readonly IUiClock _clock;
    private ScopeSelection _scope = ScopeSelection.AllHosts;

    // The dismissed partial-bar id-set and the set computed on the last Rebuild.
    // Dismiss snapshots the latter into the former; the bar reappears only when
    // a later Rebuild's set no longer equals what was dismissed (§ plan Task 7).
    private HashSet<Guid> _dismissedUnreachable = [];
    private HashSet<Guid> _unreachableIds = [];

    public TasksViewModel(HostStore store, IUiClock clock)
    {
        _store = store;
        _clock = clock;
        store.Changed += OnStoreChanged;
        clock.Tick += OnTick;
        Rebuild();
    }

    public ObservableCollection<TaskRowViewModel> Rows { get; } = [];

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

    partial void OnFilterTextChanged(string value) => Rebuild();
    partial void OnStateFilterChanged(TaskStateKind? value) => Rebuild();

    [RelayCommand]
    private void Refresh() => _store.RequestRefresh(Scope.HostId);

    [RelayCommand]
    private void DismissPartial()
    {
        _dismissedUnreachable = _unreachableIds;
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

        var reachable = scoped.Where(h =>
            RailStateProjection.From(h.Status) == RailState.Connected).ToList();

        var allRows = reachable
            .Where(h => h.Snapshot is not null)
            .SelectMany(h => h.Snapshot!.Tasks.Select(t =>
                TaskRowViewModel.From(t, h.Config.DisplayName)))
            .ToList();

        var rows = allRows
            .Where(MatchesFilters)
            .OrderBy(r => r.Deadline is null)
            .ThenBy(r => r.Deadline)
            .ToList();

        Rows.Clear();
        foreach (var row in rows) Rows.Add(row);

        // Counts/at-risk cover the reachable, UNFILTERED set: a stable summary
        // that the text/state filter shouldn't perturb.
        var running = allRows.Count(r => r.StateKind == TaskStateKind.Running);
        var uploading = allRows.Count(r => r.StateKind == TaskStateKind.Uploading);
        var suspended = allRows.Count(r => r.StateKind == TaskStateKind.Suspended);
        CountsText = string.Format(Strings.CountsFmt, allRows.Count, running, uploading, suspended);

        var atRisk = allRows.Count(r => r.IsDeadlineAtRisk);
        AtRiskText = atRisk > 0 ? string.Format(Strings.AtRiskFmt, atRisk) : "";

        PollingText = string.Format(Strings.PollingFmt, _store.PollingIntervalSeconds);

        var timestamps = reachable
            .Where(h => h.Snapshot is not null)
            .Select(h => h.Snapshot!.Timestamp)
            .ToList();
        // Oldest of the scoped snapshots: the pessimistic "how stale could this
        // be" reading, not the newest arrival.
        UpdatedText = timestamps.Count > 0 ? TimeText.UpdatedAgo(timestamps.Min(), _clock.Now) : "";

        IsUpdateStale = scoped.Any(h =>
            RailStateProjection.From(h.Status) is RailState.Retrying or RailState.Unreachable);

        _unreachableIds = _store.Hosts
            .Where(h => RailStateProjection.From(h.Status) is RailState.Unreachable or RailState.AuthFailed)
            .Select(h => h.Config.Id)
            .ToHashSet();

        ShowPartialBar = Scope.IsAllHosts
            && _unreachableIds.Count > 0
            && !_unreachableIds.SetEquals(_dismissedUnreachable);

        if (ShowPartialBar)
        {
            var covered = _store.Hosts.Count - _unreachableIds.Count;
            PartialBarText = string.Format(
                Strings.PartialFmt, _unreachableIds.Count, _store.Hosts.Count, covered);
        }

        IsLoading = scoped.Count > 0 && scoped.All(h => h.Snapshot is null);
        IsEmpty = !IsLoading && Rows.Count == 0;
    }

    public void Dispose()
    {
        _store.Changed -= OnStoreChanged;
        _clock.Tick -= OnTick;
    }
}
