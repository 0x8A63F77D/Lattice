using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lattice.App.Aggregation;
using Lattice.App.Infrastructure;
using Lattice.App.Localization;

namespace Lattice.App.ViewModels;

/// <summary>
/// Scopes, merges, sorts and counts file transfers across one or all hosts for
/// the Transfers view DataGrid. Reduced stamp of TasksViewModel: same shared
/// aggregation core (ViewSliceProjection, PartialBarState, CollectionReconciler),
/// no text/state filter and no column-visibility persistence — the design has
/// neither for this view. Density is not persisted here directly either: it
/// mirrors the shared <see cref="DensityPreference"/> — a single global
/// preference, not a per-view one (Codex round-3 P2, PR #45). Takes
/// (HostStore, IUiClock, DensityPreference) only — shell-agnostic and
/// independently testable; ShellViewModel pushes <see cref="Scope"/> on change.
/// </summary>
public sealed partial class TransfersViewModel : ObservableObject, IDisposable
{
    private readonly HostStore _store;
    private readonly IUiClock _clock;
    private readonly DensityPreference _density;
    private ScopeSelection _scope = ScopeSelection.AllHosts;

    // Episode semantics live in PartialBarPolicy, the call protocol (current/
    // dismissed fingerprints, scope gate) in PartialBarState; this class only
    // holds the instance.
    private readonly PartialBarState _partialBar = new();

    public TransfersViewModel(HostStore store, IUiClock clock, DensityPreference density)
    {
        _store = store;
        _clock = clock;
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
    // (ObservableCollection<T> is invariant, and TransferRow is a closed
    // subclass for XAML's benefit, not a type substitution). The factory below
    // still creates real TransferRow instances, so DataContext at the view
    // layer is always a TransferRow at runtime.
    public ObservableCollection<RowHolder<TransferRowKey, TransferRowViewModel>> Rows { get; } = [];

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

    /// <summary>Density toggle for the Transfers DataGrid (compact = smaller row height).
    /// Mirrors the shared <see cref="DensityPreference"/> for XAML binding —
    /// restored from it at construction, pushed to it on every local change,
    /// and pulled back from it whenever TasksViewModel changes it
    /// (Codex round-3 P2, PR #45).</summary>
    [ObservableProperty] private bool _isCompact;

    partial void OnIsCompactChanged(bool value) => _density.Set(value);

    // CommunityToolkit's generated setter no-ops when the new value equals the
    // current one, so pulling _density.Value back in here on every Changed
    // cannot re-enter OnIsCompactChanged / _density.Set — no feedback loop.
    private void OnDensityChanged(object? sender, EventArgs e) => IsCompact = _density.Value;

    [RelayCommand]
    private void Refresh() => _store.RequestRefresh(Scope.HostId);

    [RelayCommand]
    private void DismissPartial()
    {
        _partialBar.Dismiss();
        ShowPartialBar = false;
    }

    private void OnStoreChanged(object? sender, EventArgs e) => Rebuild();

    // The clock tick only ever moves freshness text/retry countdowns forward,
    // but Rebuild recomputes everything together (no partial invalidation)
    // rather than special-casing a freshness-only path.
    private void OnTick(object? sender, EventArgs e) => Rebuild();

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
        // so Snapshot is non-null inside it. "now" for the retry countdown is
        // the VM's clock, re-read on every tick — that's what moves the text.
        var slice = ViewSliceProjection.Compute(_store.Hosts, Scope,
            h => h.Snapshot!.Transfers
                .Select(t => TransferRowViewModel.From(t, h.Config.Id, h.Config.DisplayName, _clock.Now))
                .ToArray());
        var allRows = slice.AllRows;

        // No filter for this view (design has none): sort is the only shaping
        // step. Project then Name, both ascending — a recorded plan decision.
        var target = allRows
            .OrderBy(r => r.Project, StringComparer.Ordinal)
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .Select(r => (r.Key, r))
            .ToArray();

        // Keyed reconcile instead of replace: in-place Data updates keep
        // holder identity (DataGrid selection) and steady-state polls raise
        // no CollectionChanged at all (issue #24's fix, shared here).
        var existing = Rows.Select(h => (h.Key, h.Data)).ToArray();
        CollectionReconciler.Apply(Rows, Reconcile.diff(existing, target), (key, row) => new TransferRow(key, row));

        // Counts cover the reachable, UNFILTERED set — there is no filter here,
        // but the same "not perturbed by view-local state" framing applies.
        var uploading = allRows.Count(r => r.Key.IsUpload);
        var downloading = allRows.Length - uploading;
        CountsText = string.Format(CultureInfo.InvariantCulture, Strings.TransfersCountsFmt,
            allRows.Length, uploading, downloading);

        PollingText = string.Format(Strings.PollingFmt, _store.PollingIntervalSeconds);

        // Oldest of the scoped snapshots: the pessimistic "how stale could this
        // be" reading, not the newest arrival.
        UpdatedText = slice.OldestTimestamp is { } oldest ? TimeText.UpdatedAgo(oldest, _clock.Now) : "";

        IsUpdateStale = slice.IsUpdateStale;

        // "transfers below cover {2} hosts" must count the exact set Rows are
        // built from (Connected AND snapshotted; the bar only shows in the
        // All-hosts scope, so CoveredIds spans every host here) — same
        // semantics as the Tasks leg (Codex P2), inherited from the shared
        // ViewSliceProjection/PartialBarState machinery.
        var unreachableIds = slice.UnreachableIds;
        var coveredIds = slice.CoveredIds;

        ShowPartialBar = _partialBar.Advance(unreachableIds, coveredIds, IsAllHostsScope);

        if (ShowPartialBar)
        {
            PartialBarText = string.Format(
                Strings.TransfersPartialFmt, unreachableIds.Count, _store.Hosts.Count, coveredIds.Count);
        }

        // Overlay choice (loading skeleton vs empty message vs neither) is
        // TasksOverlayPolicy's; all-terminal scopes get neither (Codex P2).
        // Transfer presence is the full (unfiltered) row set — there is no
        // filter on this view to create a "filter miss vs empty" distinction.
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
