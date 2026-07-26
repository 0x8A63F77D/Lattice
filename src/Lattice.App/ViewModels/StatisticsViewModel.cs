using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.Measure;
using Lattice.App.Aggregation;
using Lattice.App.Charting;
using Lattice.App.Infrastructure;
using Lattice.App.Localization;
using Lattice.Boinc.GuiRpc;
using Lattice.Core;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;

namespace Lattice.App.ViewModels;

/// <summary>
/// Drives the Statistics page (design contract, issue #148). Unlike the grid views this is
/// a SINGLE-host surface — cross-host overlay is out of scope ([HARD], §4) — so it charts
/// the scoped host, or the ComboBox-selected host under the "All hosts" scope. All chart-
/// content decisions live in the pure <see cref="StatisticsChart"/> module and the shared
/// <see cref="StatisticsChartBuilder"/>; this class owns only the chrome state (metric
/// switch, legend visibility, overflow cap, empty/stale surfaces) and the GuiRpc → F#
/// projection. Takes (HostStore, IUiClock) only — shell-agnostic; ShellViewModel pushes
/// <see cref="Scope"/> on change and the view pushes <see cref="Theme"/> on a theme switch.
/// </summary>
public sealed partial class StatisticsViewModel : ObservableObject, IDisposable
{
    private readonly HostStore _store;
    private readonly IUiClock _clock;
    private ScopeSelection _scope = ScopeSelection.AllHosts;

    // The page's whole visibility/colour state — which series are on the chart, the palette slot
    // each of them holds, whether the user has overridden the default set, and the host all of
    // that belongs to. Every transition over it is <see cref="StatisticsVisibility.step"/>'s
    // (issue #191): this class holds the state and renders it, and decides nothing. It used to be
    // four inline fields threaded through Rebuild(), which is what produced #170/#171/#175.
    private VisibilityState _visibility = StatisticsVisibility.initial;

    // Last chart-input signature (host, metric, theme, visible set, visible names, statistics ref);
    // the chart is reassigned only when this changes, so idle polls/ticks don't re-run the enter
    // animation.
    private (Guid, CreditMetric, StatisticsChartTheme, string, string, object?) _chartSignature;

    public StatisticsViewModel(HostStore store, IUiClock clock)
    {
        _store = store;
        _clock = clock;
        MetricOptions =
        [
            new StatisticsMetricOption(Strings.StatisticsMetricUserTotal, CreditMetric.UserTotal),
            new StatisticsMetricOption(Strings.StatisticsMetricUserAverage, CreditMetric.UserAverage),
            new StatisticsMetricOption(Strings.StatisticsMetricHostTotal, CreditMetric.HostTotal),
            new StatisticsMetricOption(Strings.StatisticsMetricHostAverage, CreditMetric.HostAverage),
        ];
        _selectedMetric = MetricOptions[0]; // User total (§4 default)
        store.Changed += OnStoreChanged;
        clock.Tick += OnTick;
        Rebuild();
    }

    // ---- chrome collections & chart output -------------------------------

    /// <summary>The four metric-switcher segments (§4), Manager wording and order.</summary>
    public IReadOnlyList<StatisticsMetricOption> MetricOptions { get; }

    /// <summary>
    /// Legend chips for the ≤6 default-visible projects (§4). Reconciled IN PLACE, never rebuilt
    /// (#191) — see <see cref="StatisticsLegendChip"/> for why a Reset here is a defect.
    /// </summary>
    public ObservableCollection<StatisticsLegendChip> Chips { get; } = [];

    /// <summary>Overflow-flyout rows for projects beyond the cap (§4). Reconciled in place (#191).</summary>
    public ObservableCollection<StatisticsOverflowItem> Overflow { get; } = [];

    /// <summary>
    /// Host picker entries, shown only in the "All hosts" scope (§4). Reconciled IN PLACE, never
    /// rebuilt (#175).
    /// </summary>
    public ObservableCollection<StatisticsHostOption> HostOptions { get; } = [];

    // Chart-content wiring for the CartesianChart binding (built by the shared renderer).
    [ObservableProperty] private IEnumerable<ISeries> _series = [];
    [ObservableProperty] private IEnumerable<ICartesianAxis> _xAxes = [];
    [ObservableProperty] private IEnumerable<ICartesianAxis> _yAxes = [];

    // Chart-level pins (§3 [HARD] / §6), surfaced for XAML binding so the page and the
    // snapshot harness share them.
    public TimeSpan AnimationsSpeed => StatisticsChartBuilder.AnimationsSpeed;
    public Func<float, float> EasingFunction => StatisticsChartBuilder.Easing;
    public FindingStrategy FindingStrategy => StatisticsChartBuilder.TooltipFindingStrategy;
    public ZoomAndPanMode ZoomMode => StatisticsChartBuilder.ZoomMode;

    // ---- observable chrome state -----------------------------------------

    [ObservableProperty] private bool _isAllHostsScope;

    /// <summary>
    /// The charted host under the "All hosts" scope, held as the host's OWN identity — not as
    /// one of the <see cref="HostOptions"/> instances (issue #175). The picker binds it through
    /// <c>SelectedValue</c>/<c>SelectedValueBinding</c>, so the ComboBox resolves the selection
    /// against whatever option instances currently exist. That is what makes the selection
    /// survive a rebuilt option list: an item-instance selection is silently dropped by the
    /// control the moment its instance leaves Items (an emptied list under a single-host scope,
    /// or a fresh list after the fleet changes), and because StatisticsHostOption is a record,
    /// the repair write below was then swallowed by the value-equality guard in the
    /// [ObservableProperty] setter itself — no PropertyChanged, blank picker. A Guid has no
    /// instance identity to lose, so that whole failure mode cannot be expressed.
    /// </summary>
    [ObservableProperty] private Guid? _selectedHostId;
    [ObservableProperty] private string _countsText = "";
    [ObservableProperty] private string _pollingText = "";
    [ObservableProperty] private string _updatedText = "";
    [ObservableProperty] private bool _hasChart;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private bool _isStale;
    [ObservableProperty] private string _staleText = "";
    [ObservableProperty] private string _overflowLabel = "";
    [ObservableProperty] private bool _hasOverflow;
    [ObservableProperty] private bool _isAtCap;

    /// <summary>The active metric (§4). Two-way bound to the segmented switcher.</summary>
    [ObservableProperty] private StatisticsMetricOption _selectedMetric;

    /// <summary>The chart theme, pushed by the view on a live theme switch (warning #1).</summary>
    [ObservableProperty] private StatisticsChartTheme _theme = StatisticsChartTheme.Light;

    partial void OnSelectedMetricChanged(StatisticsMetricOption value) => Rebuild();

    partial void OnSelectedHostIdChanged(Guid? value) => Rebuild();

    partial void OnThemeChanged(StatisticsChartTheme value) => Rebuild();

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

    [RelayCommand]
    private void Retry() => _store.RequestRefresh(EffectiveHost()?.Config.Id);

    private void OnStoreChanged(object? sender, EventArgs e) => Rebuild();

    // The 1 s tick ONLY advances the "Updated Ns ago" caption — it must NOT run the full
    // Rebuild, which reassigns the LiveCharts series/axes wholesale and (with the 200ms enter
    // animation) would re-animate an otherwise-idle chart once per second. Chart inputs change
    // only via the store, metric, host, theme, scope and toggle paths, all of which Rebuild
    // (Codex P2, PR #167). The grid views get the same effect for free through their keyed
    // reconciler; this page has no reconciler, so the freshness-only path is explicit.
    private void OnTick(object? sender, EventArgs e) => RefreshFreshness();

    private void RefreshFreshness()
    {
        var snapshot = EffectiveHost()?.Snapshot;
        UpdatedText = snapshot is not null ? TimeText.UpdatedAgo(snapshot.Timestamp, _clock.Now) : "";
    }

    // ---- host resolution -------------------------------------------------

    /// <summary>
    /// The host whose statistics are charted: the scoped host in a single-host scope, else
    /// the picker's selection (defaulting to the first connected host) in "All hosts".
    /// </summary>
    private HostEntry? EffectiveHost()
    {
        if (!Scope.IsAllHosts)
            return _store.Hosts.FirstOrDefault(h => h.Config.Id == Scope.HostId);
        if (SelectedHostId is { } sel)
        {
            var picked = _store.Hosts.FirstOrDefault(h => h.Config.Id == sel);
            if (picked is not null) return picked;
        }
        return FirstConnected() ?? _store.Hosts.FirstOrDefault();
    }

    private HostEntry? FirstConnected() =>
        _store.Hosts.FirstOrDefault(h => RailStateProjection.From(h.Status) == RailState.Connected)
        ?? _store.Hosts.FirstOrDefault(h => h.Snapshot is not null);

    private void SyncHostOptions()
    {
        IsAllHostsScope = Scope.IsAllHosts && _store.Hosts.Count > 1;

        // Reconciled in place through the shared keyed differ, and kept in sync even while the
        // picker is HIDDEN (issue #175). Both halves are load-bearing: Clear()-and-refill — and a
        // Clear() on leaving the All-hosts scope — destroy the option instances the ComboBox
        // resolves its selection against, and the control drops that selection without telling
        // anyone. Surviving hosts keep their instance here (RowHolder swaps Data on a rename), so
        // the picker's selection survives a fleet change and a scope round-trip alike.
        var target = _store.Hosts.Select(h => (h.Config.Id, h.Config.DisplayName)).ToArray();
        var existing = HostOptions.Select(o => (o.Key, o.Data)).ToArray();
        CollectionReconciler.Apply(HostOptions, Reconcile.diff(existing, target),
            (key, row) => new StatisticsHostOption(key, row));

        // Hidden picker: the chart follows the scoped host, so there is no selection to repair.
        if (!IsAllHostsScope)
            return;

        // Default / repair the selection to the effective host so the picker mirrors the chart.
        //
        // INVARIANT (issue #175): after this method, SelectedHostId is the key of an option present
        // in HostOptions, or null. It is a VALUE, so the guard below is exact — nothing here
        // depends on which option instance the control happens to hold, which is why the picker
        // (bound through SelectedValue, not SelectedItem) cannot desynchronise from the chart.
        // Guarded so a steady-state poll (same effective host) writes nothing: the setter reenters
        // Rebuild once per real change.
        var effective = EffectiveHost();
        var match = HostOptions.Any(o => o.Key == effective?.Config.Id) ? effective!.Config.Id : (Guid?)null;
        if (SelectedHostId != match)
            SelectedHostId = match;
    }

    // ---- projection ------------------------------------------------------

    /// <summary>
    /// The charted host's credit histories — the page's only data input, and the same projection
    /// the snapshot harness uses. Empty when there is no host or no cached snapshot.
    /// </summary>
    private static FSharpList<ProjectHistory> HistoriesOf(HostEntry? host) =>
        host?.Snapshot is { } snapshot
            ? ListModule.OfSeq(StatisticsProjection.FromProjects(
                [.. snapshot.Projects.Select(p => p.Project)], snapshot.Statistics))
            : FSharpList<ProjectHistory>.Empty;

    // ---- rebuild ---------------------------------------------------------

    private void Rebuild()
    {
        SyncHostOptions();
        PollingText = string.Format(Strings.PollingFmt, _store.PollingIntervalSeconds);

        var host = EffectiveHost();
        var snapshot = host?.Snapshot;
        var histories = HistoriesOf(host);
        var hasHistory = !histories.IsEmpty;

        // Overlay choice reuses the shared per-host taxonomy: loading = first fetch still
        // plausibly in flight, empty = a Connected host answered with no history (§5).
        var rail = host is null ? RailState.Connecting : RailStateProjection.From(host.Status);
        (IsLoading, IsEmpty) = TasksOverlayPolicy.Decide(
            host is null ? [] : [new TasksOverlayPolicy.HostFacts(rail, snapshot is not null)],
            hasHistory);

        HasChart = hasHistory;

        // Stale banner (§5): an unreachable host keeps rendering its last data with a warning.
        IsStale = hasHistory && rail == RailState.Unreachable && snapshot is not null;
        StaleText = IsStale
            ? string.Format(Strings.StatisticsStaleFmt, snapshot!.Timestamp.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture))
            : "";

        UpdatedText = snapshot is not null ? TimeText.UpdatedAgo(snapshot.Timestamp, _clock.Now) : "";

        // Every visibility/colour transition is the policy's (#191): a fresh host re-derives the
        // ≤6 default, an unchanged host keeps the user's set (minus vanished projects), and the
        // palette slots follow the visible series in both cases. Nothing to chart settles back to
        // the initial state, which is why the branch below only has chrome left to clear.
        _visibility = StatisticsVisibility.step(
            StatisticsVisibility.charted(host?.Config.Id, histories),
            VisibilityEvent.Settle,
            _visibility).State;

        // Chips and the overflow flyout reconcile in place on both paths — an empty partition
        // removes the rows one by one rather than Clear()ing the collections (#191).
        var partition = StatisticsChart.partition(histories);
        SyncChips(partition.Chips);
        SyncOverflow(partition.Overflow);

        if (!hasHistory)
        {
            Series = [];
            XAxes = [];
            YAxes = [];
            _chartSignature = default;
            CountsText = "";
            return;
        }

        // Reassign the LiveCharts series/axes ONLY when a chart INPUT changed — the effective
        // host, metric, theme, visible set, or the statistics history itself (carried forward by
        // reference between the 6h refetches). A steady-state store poll (~5s, updates only RAC)
        // or a freshness tick leaves the signature unchanged, so the plot is not recreated and the
        // 200ms enter animation does not re-run on an idle page (Codex P2, PR #167). The chrome
        // above (chips/overflow RAC) still refreshes in place; only the animated chart is gated.
        // Both string halves are the policy's (#191): the COLOUR ASSIGNMENT (url=slot) subsumes
        // the visible set — a different set is a different assignment, and a series that changed
        // slot must repaint even where the set happens to match — and the visible series' NAMES
        // ride along, so a late-filled project name refreshes the LineSeries label/tooltip even
        // though the history reference is unchanged.
        (Guid, CreditMetric, StatisticsChartTheme, string, string, object?) signature = (
            host!.Config.Id, SelectedMetric.Metric, Theme,
            StatisticsVisibility.colourKey(_visibility),
            StatisticsVisibility.nameKey(histories, _visibility),
            snapshot!.Statistics);
        if (!signature.Equals(_chartSignature))
        {
            var specs = StatisticsChart.seriesFor(SelectedMetric.Metric, _visibility.Colors, histories);
            var visual = StatisticsChartBuilder.Build(ListModule.ToArray(specs), Theme, SelectedMetric.Metric);
            Series = visual.Series;
            XAxes = visual.XAxes;
            YAxes = visual.YAxes;
            _chartSignature = signature;
        }

        CountsText = string.Format(
            CultureInfo.CurrentCulture, Strings.StatisticsCountsFmt,
            histories.Length, StatisticsChart.historyDepthDays(histories));
    }

    // The legend reconciles IN PLACE, keyed by master URL (#191). Clear()-and-refill raises a
    // CollectionChanged Reset, which destroys and rebuilds every container in the bound
    // ItemsControl — for the flyout below, the very checkboxes the user may be clicking. The
    // rendered facts (label, ordinal, palette slot) live in the row's immutable Data, so a
    // re-label or a re-colour swaps that record on the surviving row and only real membership or
    // order changes move rows.
    private void SyncChips(FSharpList<ProjectHistory> chips)
    {
        var target = chips
            .Select(p => (p.MasterUrl, new StatisticsChipData(p.Name, p.Ordinal, SlotFor(p.MasterUrl))))
            .ToArray();
        var existing = Chips.Select(c => (c.Key, c.Data)).ToArray();
        CollectionReconciler.Apply(Chips, Reconcile.diff(existing, target),
            (key, row) => new StatisticsLegendChip(key, row) { Toggled = OnChipToggled });

        // Visibility is holder state (the two-way binding target), not part of the reconciled
        // data, so it is pushed onto every row — new or surviving — silently, so that the sync
        // itself never re-enters the toggle path.
        foreach (var chip in Chips)
            chip.SetVisibleSilently(StatisticsVisibility.isVisible(chip.Key, _visibility));
    }

    private void SyncOverflow(FSharpList<ProjectHistory> overflow)
    {
        HasOverflow = !overflow.IsEmpty;
        OverflowLabel = string.Format(Strings.StatisticsOverflowFmt, overflow.Length);
        IsAtCap = !StatisticsVisibility.canAdd(_visibility);

        var target = overflow
            .Select(p => (p.MasterUrl, new StatisticsOverflowData(p.Name, RacText(p.Rac), CanCheck(p.MasterUrl))))
            .ToArray();
        var existing = Overflow.Select(o => (o.Key, o.Data)).ToArray();
        CollectionReconciler.Apply(Overflow, Reconcile.diff(existing, target),
            (key, row) => new StatisticsOverflowItem(key, row) { Toggled = OnOverflowToggled });

        foreach (var item in Overflow)
            item.SetVisibleSilently(StatisticsVisibility.isVisible(item.Key, _visibility));
    }

    // The palette slot a chip's line holds — and none at all when that series is not on the chart
    // (§2, #171). The row renders the grey "not plotted" swatch for a null, so a hidden chip never
    // carries a colour it might later contradict.
    private int? SlotFor(string master)
    {
        var slot = StatisticsVisibility.slotOf(master, _visibility);
        return FSharpOption<int>.get_IsSome(slot) ? slot.Value : null;
    }

    // A row can be checked if it is already shown or the cap has room (§4).
    private bool CanCheck(string master) => StatisticsVisibility.canCheck(master, _visibility);

    private static string RacText(double rac) =>
        ((long)Math.Round(rac)).ToString("N0", CultureInfo.CurrentCulture);

    private void OnChipToggled(StatisticsLegendChip chip)
    {
        if (!TryApplyToggle(chip.Key, chip.IsVisible))
        {
            chip.SetVisibleSilently(false);
            return;
        }
        Rebuild();
    }

    private void OnOverflowToggled(StatisticsOverflowItem item)
    {
        if (!TryApplyToggle(item.Key, item.IsVisible))
        {
            item.SetVisibleSilently(false);
            return;
        }
        Rebuild();
    }

    // The SINGLE cap-guarded visibility mutation (§4 ≤6), now the policy's decision (#191): a check
    // that would exceed the cap is refused and the caller snaps the control back. Both the chip and
    // the overflow toggle route through here so the cap can never be enforced on one path and
    // forgotten on the other — the overflow flyout disables its rows at six, but a re-checked chip
    // is the same invariant and must not slip past it (Codex P2, PR #167). An applied toggle is
    // followed by the caller's Rebuild, whose settle lands on this very state (the policy's tests
    // pin that round trip as idempotent), so the user sees one transition, not two.
    private bool TryApplyToggle(string master, bool visible)
    {
        var host = EffectiveHost();
        var decision = StatisticsVisibility.step(
            StatisticsVisibility.charted(host?.Config.Id, HistoriesOf(host)),
            StatisticsVisibility.toggle(master, visible),
            _visibility);
        _visibility = decision.State;
        return !decision.Refused;
    }

    public void Dispose()
    {
        _store.Changed -= OnStoreChanged;
        _clock.Tick -= OnTick;
    }
}
