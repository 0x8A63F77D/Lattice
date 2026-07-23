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

    // Visible master URLs and the host they belong to: user toggles persist across the 1 s
    // ticks, but switching the charted host re-derives the top-6 default.
    private readonly HashSet<string> _visible = [];
    // Whether the user has toggled visibility for the current host. Until they do, the page mirrors
    // the live default (all ≤6, else top-6 by RAC); once they do, their set is authoritative.
    private bool _userOverrode;
    private Guid? _visibleHostId;

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

    /// <summary>Legend chips for the ≤6 default-visible projects (§4).</summary>
    public ObservableCollection<StatisticsLegendChip> Chips { get; } = [];

    /// <summary>Overflow-flyout rows for projects beyond the cap (§4).</summary>
    public ObservableCollection<StatisticsOverflowItem> Overflow { get; } = [];

    /// <summary>Host picker entries, shown only in the "All hosts" scope (§4).</summary>
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
    [ObservableProperty] private StatisticsHostOption? _selectedHost;
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

    partial void OnSelectedHostChanged(StatisticsHostOption? value) => Rebuild();

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
        if (SelectedHost is { } sel)
        {
            var picked = _store.Hosts.FirstOrDefault(h => h.Config.Id == sel.HostId);
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
        if (!IsAllHostsScope)
        {
            if (HostOptions.Count > 0) HostOptions.Clear();
            return;
        }

        var desired = _store.Hosts.Select(h => new StatisticsHostOption(h.Config.Id, h.Config.DisplayName)).ToList();
        if (!desired.SequenceEqual(HostOptions))
        {
            HostOptions.Clear();
            foreach (var o in desired) HostOptions.Add(o);
        }

        // Default / repair the selection to the effective host so the picker mirrors the chart.
        var effective = EffectiveHost();
        var match = effective is null ? null : HostOptions.FirstOrDefault(o => o.HostId == effective.Config.Id);
        if (!Equals(SelectedHost, match))
            SelectedHost = match; // setter reenters Rebuild once; guarded by the equality check
    }

    // ---- projection ------------------------------------------------------

    // ---- rebuild ---------------------------------------------------------

    private void Rebuild()
    {
        SyncHostOptions();
        PollingText = string.Format(Strings.PollingFmt, _store.PollingIntervalSeconds);

        var host = EffectiveHost();
        var snapshot = host?.Snapshot;
        List<ProjectHistory> histories = snapshot is null
            ? []
            : StatisticsProjection.FromProjects([.. snapshot.Projects.Select(p => p.Project)], snapshot.Statistics);
        var hasHistory = histories.Count > 0;

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

        if (!hasHistory)
        {
            _visibleHostId = null;
            _visible.Clear();
            _userOverrode = false;
            if (Chips.Count > 0) Chips.Clear();
            if (Overflow.Count > 0) Overflow.Clear();
            HasOverflow = false;
            IsAtCap = false;
            Series = [];
            XAxes = [];
            YAxes = [];
            _chartSignature = default;
            CountsText = "";
            return;
        }

        var hostId = host!.Config.Id;
        var masters = histories.Select(h => h.MasterUrl).ToHashSet();
        if (_visibleHostId != hostId)
        {
            _userOverrode = false; // a fresh host starts on the default set
            _visibleHostId = hostId;
        }

        // Visibility model (Codex P2 family, PR #167): until the user toggles anything, the page
        // always mirrors the LIVE default — all projects when ≤6, else the current top-6 by RAC —
        // so a mid-session refetch that adds a project, or a RAC reorder that changes the top-6,
        // is reflected immediately (no stuck unchecked chip, no stale top-6). The moment the user
        // toggles a chip/overflow row their choice is authoritative: it persists across polls, and
        // only vanished projects are dropped. (The signature gate above still rebuilds the chart
        // only when this SET actually changes, so an idle page never re-animates.)
        if (!_userOverrode)
        {
            _visible.Clear();
            foreach (var url in StatisticsChart.defaultVisible(ListModule.OfSeq(histories)))
                _visible.Add(url);
        }
        else
        {
            _visible.IntersectWith(masters); // persist the user's set; drop vanished projects
        }

        var partition = StatisticsChart.partition(ListModule.OfSeq(histories));
        SyncChips(partition.Chips);
        SyncOverflow(partition.Overflow);

        // Reassign the LiveCharts series/axes ONLY when a chart INPUT changed — the effective
        // host, metric, theme, visible set, or the statistics history itself (carried forward by
        // reference between the 6h refetches). A steady-state store poll (~5s, updates only RAC)
        // or a freshness tick leaves the signature unchanged, so the plot is not recreated and the
        // 200ms enter animation does not re-run on an idle page (Codex P2, PR #167). The chrome
        // above (chips/overflow RAC) still refreshes in place; only the animated chart is gated.
        // The visible series' names ride the signature too, so a late-filled project name
        // refreshes the LineSeries label/tooltip even though the history reference is unchanged.
        var visibleNames = string.Join("", histories
            .Where(h => _visible.Contains(h.MasterUrl))
            .OrderBy(h => h.Ordinal)
            .Select(h => h.Name));
        (Guid, CreditMetric, StatisticsChartTheme, string, string, object?) signature = (hostId,
            SelectedMetric.Metric, Theme,
            string.Join(",", _visible.OrderBy(u => u, StringComparer.Ordinal)),
            visibleNames, snapshot!.Statistics);
        if (!signature.Equals(_chartSignature))
        {
            var specs = StatisticsChart.seriesFor(SelectedMetric.Metric, SetModule.OfSeq(_visible), ListModule.OfSeq(histories));
            var visual = StatisticsChartBuilder.Build(ListModule.ToArray(specs), Theme, SelectedMetric.Metric);
            Series = visual.Series;
            XAxes = visual.XAxes;
            YAxes = visual.YAxes;
            _chartSignature = signature;
        }

        CountsText = string.Format(
            CultureInfo.CurrentCulture, Strings.StatisticsCountsFmt,
            histories.Count, StatisticsChart.historyDepthDays(ListModule.OfSeq(histories)));
    }

    private void SyncChips(FSharpList<ProjectHistory> chips)
    {
        var desired = chips.ToList();
        // Name is part of the shape: a chip's label is immutable, so a project whose blank name
        // BOINC later fills in must trigger a rebuild, not just a visibility sync (Codex P2, #167).
        var sameShape = Chips.Count == desired.Count
            && Chips.Zip(desired).All(pair =>
                pair.First.MasterUrl == pair.Second.MasterUrl && pair.First.Name == pair.Second.Name);

        if (!sameShape)
        {
            Chips.Clear();
            foreach (var p in desired)
            {
                var chip = new StatisticsLegendChip(p.MasterUrl, p.Name, p.Ordinal, StatisticsPalette.Brush(p.Ordinal), _visible.Contains(p.MasterUrl))
                {
                    Toggled = OnChipToggled,
                };
                Chips.Add(chip);
            }
            return;
        }

        // Same projects: only sync visibility (silently, so the sync itself never re-enters).
        foreach (var (chip, p) in Chips.Zip(desired))
            chip.SetVisibleSilently(_visible.Contains(p.MasterUrl));
    }

    private void SyncOverflow(FSharpList<ProjectHistory> overflow)
    {
        var desired = overflow.ToList();
        HasOverflow = desired.Count > 0;
        OverflowLabel = string.Format(Strings.StatisticsOverflowFmt, desired.Count);
        IsAtCap = !StatisticsChart.canAddSeries(_visible.Count);

        var sameShape = Overflow.Count == desired.Count
            && Overflow.Zip(desired).All(pair =>
                pair.First.MasterUrl == pair.Second.MasterUrl && pair.First.Name == pair.Second.Name);

        if (!sameShape)
        {
            Overflow.Clear();
            foreach (var p in desired)
                Overflow.Add(new StatisticsOverflowItem(
                    p.MasterUrl, p.Name, RacText(p.Rac), _visible.Contains(p.MasterUrl), CanCheck(p.MasterUrl))
                {
                    Toggled = OnOverflowToggled,
                });
            return;
        }

        foreach (var (item, p) in Overflow.Zip(desired))
        {
            item.RacText = RacText(p.Rac);
            item.SetVisibleSilently(_visible.Contains(p.MasterUrl));
            item.CanCheck = CanCheck(p.MasterUrl);
        }
    }

    // A row can be checked if it is already shown or the cap has room (§4).
    private bool CanCheck(string master) => _visible.Contains(master) || StatisticsChart.canAddSeries(_visible.Count);

    private static string RacText(double rac) =>
        ((long)Math.Round(rac)).ToString("N0", CultureInfo.CurrentCulture);

    private void OnChipToggled(StatisticsLegendChip chip)
    {
        if (!TryApplyToggle(chip.MasterUrl, chip.IsVisible))
        {
            chip.SetVisibleSilently(false);
            return;
        }
        Rebuild();
    }

    private void OnOverflowToggled(StatisticsOverflowItem item)
    {
        if (!TryApplyToggle(item.MasterUrl, item.IsVisible))
        {
            item.SetVisibleSilently(false);
            return;
        }
        Rebuild();
    }

    // The SINGLE cap-guarded visibility mutation (§4 ≤6): a check that would exceed the cap is
    // refused (the caller snaps the control back). Both the chip and the overflow toggle route
    // through here so the cap can never be enforced on one path and forgotten on the other — the
    // overflow flyout disables its rows at six, but a re-checked chip is the same invariant and
    // must not slip past it (Codex P2, PR #167).
    private bool TryApplyToggle(string master, bool visible)
    {
        if (visible && !CanCheck(master))
            return false;
        if (visible) _visible.Add(master);
        else _visible.Remove(master);
        _userOverrode = true; // the user's set is now authoritative over the live default
        return true;
    }

    public void Dispose()
    {
        _store.Changed -= OnStoreChanged;
        _clock.Tick -= OnTick;
    }
}
