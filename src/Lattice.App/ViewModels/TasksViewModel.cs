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

    // The dismissed partial-bar fingerprint and the one computed on the last
    // Rebuild. Episode semantics (when a dismissal holds, when it is
    // forgotten) live in PartialBarPolicy; this class only stores the state
    // and applies the scope gate.
    private PartialBarPolicy.Fingerprint _dismissedFingerprint = PartialBarPolicy.EmptyFingerprint;
    private PartialBarPolicy.Fingerprint _currentFingerprint = PartialBarPolicy.EmptyFingerprint;

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
    [ObservableProperty] private string _loadingText = "";

    /// <summary>Density toggle for the Tasks DataGrid (compact = smaller row height).
    /// Persistence via UiStateStore is wired in a later task; this is view-facing
    /// state only for now.</summary>
    [ObservableProperty] private bool _isCompact;

    partial void OnFilterTextChanged(string value) => Rebuild();
    partial void OnStateFilterChanged(TaskStateKind? value) => Rebuild();

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

        // Replace the collection only when contents actually changed (rows are
        // value-equal records, so SequenceEqual is a semantic comparison): the
        // 1s tick also lands here, and an unconditional Clear()+Add would fire
        // Reset + N Adds every second and reset a bound DataGrid's selection
        // onto brand-new row instances.
        if (!rows.SequenceEqual(Rows))
        {
            Rows.Clear();
            foreach (var row in rows) Rows.Add(row);
        }

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

        var unreachableIds = _store.Hosts
            .Where(h => RailStateProjection.From(h.Status) is RailState.Unreachable or RailState.AuthFailed)
            .Select(h => h.Config.Id)
            .ToHashSet();

        // "tasks below cover {2} hosts" must count the exact set Rows are
        // built from (Connected AND snapshotted; the bar only shows in the
        // All-hosts scope, so `reachable` spans every host here) — NOT total
        // minus unreachable-tier, which would also count Retrying/Connecting
        // hosts whose tasks are not in the grid (Codex P2). This same covered
        // set also feeds the dismissal fingerprint below (Codex P2 round 2):
        // a dismissed bar must reappear when a covered host drops out, even
        // if the unreachable tier itself never changes.
        var coveredIds = reachable
            .Where(h => h.Snapshot is not null)
            .Select(h => h.Config.Id)
            .ToHashSet();

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
            allRows.Count > 0);
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
