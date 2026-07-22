using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lattice.App.Aggregation;
using Lattice.App.Infrastructure;
using Lattice.App.Localization;
using Lattice.Boinc.GuiRpc;
using Lattice.Core;
using ScopeState = Lattice.App.Aggregation.Scope;

namespace Lattice.App.ViewModels;

/// <summary>
/// Owns the rail (views + hosts), the global scope, and the current page.
/// UI-thread only.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private const double RailRowHeight = 40.0;         // LatticeHostItemHeight — flat/single host + All-hosts rows AND the fit-math row unit
    private const double GroupedHostRowHeight = 36.0;   // LatticeRowHeight — denser host rows inside status groups (design 3a)
    // The view feeds the FULL Nav height (Nav.Bounds.Height) into SetRailViewportHeight, so this
    // constant absorbs ALL non-list vertical chrome: the 4 nav menu items (~160), the Hosts header
    // (~28), the Settings footer item (~40) and paddings/separators. Card 3a's authoritative "~290
    // fixed rail chrome" anchor (design/m2), pinned here by Task 8's headless geometry tests
    // (12 hosts @ 700px window ⇒ Grouped; 2 hosts @ 800px ⇒ Flat). NB: decisions §3 wrote "≈150"
    // assuming the fed height already excluded the menu-items region — the cards win (§ preamble),
    // and feeding the full Nav height makes ~290 the correct value.
    private const double ReservedRailChrome = 290.0;

    private readonly HostStore _store;
    private readonly IUiClock _clock;
    private readonly UiStateStore _uiState;
    private readonly ThemePreference _theme;
    private readonly HostControlService _control;
    private readonly AllHostsRailItemViewModel _allHosts = new();
    private readonly Dictionary<Guid, HostRailItemViewModel> _hostRowVms = [];
    private double _railViewportHeight;
    private RailGroupingMode _grouping;
    private bool _healthyExpanded;
    // Compact (48px) pane state, fed by the view from FANavigationView.IsPaneOpen. When the pane
    // is compact the rail must render EVERY host as an individual state-icon (decisions §5), so the
    // compute force-expands the Healthy tier (see BuildRailInput) — the persisted RailHealthyExpanded
    // is deliberately untouched, so re-opening the pane restores the saved collapse state.
    private bool _paneCompact;

    [ObservableProperty] private bool _showRailToggle;

    /// <summary>Height every host row binds to: 40 px flat/single, 36 px inside a status group
    /// (design 3a). Group-header rows are a fixed 28 px in XAML; the "All hosts" row stays 40 px.
    /// This is RENDERING only — the RailLayoutPolicy fit test still counts 40 px flat rows.</summary>
    [ObservableProperty] private double _hostRowHeight = RailRowHeight;

    public ShellViewModel(
        HostRegistry registry, HostStore store, IUiClock clock, UiStateStore uiState,
        Func<IGuiRpcClient> clientFactory, Action? restartApp = null)
    {
        _store = store;
        _clock = clock;
        _uiState = uiState;
        var ui = uiState.Load();
        _grouping = ui.RailGrouping;
        _healthyExpanded = ui.RailHealthyExpanded;
        _theme = new ThemePreference(uiState);
        // Language is persist-only here (applied at startup by the composition root before
        // any UI is built, #147); the Settings picker just records the choice + shows a
        // restart hint. Single owner, same shape as ThemePreference.
        var language = new LanguagePreference(uiState);
        Settings = new SettingsViewModel(registry, clientFactory, _theme, language, uiState, restartApp);
        // ONE DensityPreference, shared: the single owner of the global density
        // preference, so a toggle in either view reaches the other in-session
        // (Codex round-3 P2, PR #45). Projects has no density toggle (design 2a
        // fixes its row heights), so it does not take the shared preference.
        var density = new DensityPreference(uiState);
        // The control lane (M3): one service over the SAME manager the store
        // listens to, so an op success nudges exactly the monitors whose
        // snapshots feed the grids. Constructed here like the other VM-layer
        // collaborators (DensityPreference, ThemePreference).
        _control = new HostControlService(registry, store.Manager, clientFactory);
        // The attach flow (M3 PR I) rides the SAME control lane so a lookup→attach
        // holds the host's lane for its whole duration; TimeProvider.System drives
        // its real 1 s poll cadence (tests use the runner's own fake-time seam).
        var attachRunner = new AttachFlowRunner(_control, store.Manager, TimeProvider.System);
        Tasks = new TasksViewModel(store, clock, uiState, density, _control);
        Projects = new ProjectsViewModel(store, clock, _control, attachRunner.RunAsync, AvaloniaUiDispatcher.Instance);
        Transfers = new TransfersViewModel(store, clock, density);
        EventLog = new EventLogViewModel(store);
        Views =
        [
            new NavItemViewModel(Strings.NavTasks, "IconTaskListSquareLtrRegular", "IconTaskListSquareLtrFilled", Tasks),
            new NavItemViewModel(Strings.NavProjects, "IconGridRegular", "IconGridFilled", Projects),
            new NavItemViewModel(Strings.NavTransfers, "IconArrowSwapRegular", "IconArrowSwapFilled", Transfers),
            new NavItemViewModel(Strings.NavEventLog, "IconDocumentTextRegular", "IconDocumentTextFilled", EventLog),
        ];
        _selectedView = Views[0];
        _currentPage = Views[0].Page;
        Tasks.Rows.CollectionChanged += OnTasksRowsChanged;
        _tasksCount = Tasks.Rows.Count;
        Transfers.Rows.CollectionChanged += OnTransfersRowsChanged;
        _transfersCount = Transfers.Rows.Count;
        EventLog.PropertyChanged += OnEventLogPropertyChanged;
        // Restore the persisted host scope via ScopeMachine (README:80/108). The core owns the
        // known/unknown-id decision — no inline `store.Hosts.Any(...)` fallback here.
        var knownHostIds = store.Hosts.Select(h => h.Config.Id).ToArray();
        ApplyScopeDecision(ScopeMachine.step(ScopeState.AllHosts,
            ScopeMachine.restoreEvent(ui.ScopeHostId, knownHostIds)));

        store.Changed += OnStoreChanged;
        ReconcileHosts();
    }

    public IReadOnlyList<NavItemViewModel> Views { get; }
    public ObservableCollection<object> RailEntries { get; } = [];
    public SettingsViewModel Settings { get; }

    /// <summary>Applies the persisted theme to the running Application. The composition root
    /// calls this once at startup, on the UI thread — <see cref="ThemePreference"/> construction
    /// must not touch UI-thread-affine global state (#101), so the initial apply is explicit.</summary>
    public void ApplyInitialTheme() => _theme.ApplyInitial();

    /// <summary>The Tasks page VM (Views[0].Page); exposed directly so the shell
    /// can push scope changes into it and mirror its row count for the nav badge.</summary>
    public TasksViewModel Tasks { get; }

    /// <summary>The Projects page VM (Views[1].Page); exposed so the shell can push
    /// scope changes into it (same rail-scope contract as <see cref="Tasks"/>).</summary>
    public ProjectsViewModel Projects { get; }

    /// <summary>The Transfers page VM (Views[2].Page); exposed directly so the shell
    /// can push scope changes into it.</summary>
    public TransfersViewModel Transfers { get; }

    /// <summary>The Event-log page (Views[3].Page); the shell pushes scope into it,
    /// toggles its active flag as it becomes / leaves CurrentPage (which zeroes the
    /// unread badge on activation), and mirrors its unread count for the nav badge.</summary>
    public EventLogViewModel EventLog { get; }

    [ObservableProperty] private NavItemViewModel? _selectedView;
    [ObservableProperty] private object _currentPage;
    [ObservableProperty] private ScopeSelection _scope = ScopeSelection.AllHosts;
    [ObservableProperty] private bool _hasHosts;

    /// <summary>
    /// The run-mode surface (M3 PR H, DI-4): the rail row VM of the single scoped
    /// host, or null in All-hosts scope. The Tasks command bar's "Computing"
    /// dropdown binds its items to this VM's <see cref="HostRailItemViewModel.SetRunModeCommand"/>;
    /// <see cref="HasScopedHost"/> gates the dropdown's visibility — absent in
    /// All-hosts scope (no fleet-wide run modes in M3, DI-4).
    /// </summary>
    [ObservableProperty] private HostRailItemViewModel? _scopedHostRow;
    [ObservableProperty] private bool _hasScopedHost;

    private object? _selectedRailEntry;

    /// <summary>The highlighted rail row, OWNED by <see cref="ResolveHighlight"/>
    /// (<see cref="ScopeMachine.highlightOf"/>) — never a scope trigger. Explicit scope selection
    /// rides the click/tap gesture instead (<c>ShellWindow.OnHostRailTapped</c> →
    /// <see cref="SelectHostScope"/> / <see cref="SelectAllHostsScope"/>), so a rebuild that
    /// re-derives the same highlight fabricates no scope event (R5), AND a click that does not
    /// change the ListBox selection (a sole preselected host, a re-clicked scoped host) still
    /// scopes. Notifies on EVERY assignment so the ListBox re-selects the derived highlight even
    /// when a rebuild's Clear transiently reset the binding to the same reference.</summary>
    public object? SelectedRailEntry
    {
        get => _selectedRailEntry;
        set
        {
            _selectedRailEntry = value;
            OnPropertyChanged(nameof(SelectedRailEntry));
        }
    }

    /// <summary>Mirrors <see cref="TasksViewModel.Rows"/>.Count; drives the Tasks nav item's inline count badge.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTasksCount))]
    private int _tasksCount;

    public bool HasTasksCount => TasksCount > 0;

    /// <summary>Mirrors <see cref="TransfersViewModel.Rows"/>.Count; drives the Transfers nav item's inline count badge.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTransfersCount))]
    private int _transfersCount;

    public bool HasTransfersCount => TransfersCount > 0;

    /// <summary>Mirrors <see cref="EventLogViewModel.UnreadCount"/>; drives the Event-log
    /// nav item's InfoBadge (unread warning+error count while the page is not active).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEventLogUnread))]
    private int _eventLogUnread;

    public bool HasEventLogUnread => EventLogUnread > 0;

    /// <summary>A user click on a host row — the SOLE explicit host-scope gesture (decisions §7).
    /// Persists ScopeHostId via <c>ScopeMachine.ExplicitSelect</c> even when the click did NOT change
    /// the ListBox selection (a sole preselected host, or re-clicking the already-scoped host) —
    /// exactly the case the former SelectionChanged trigger missed. Idempotent: re-clicking the
    /// scoped host re-writes the same id.</summary>
    public void SelectHostScope(Guid hostId) =>
        ApplyScopeDecision(ScopeMachine.step(ToScope(Scope),
            ScopeEvent.NewExplicitSelect(ScopeCarrier.NewHostCarrier(hostId))));

    /// <summary>A user click on the All-hosts sentinel: scope to All hosts and clear the persisted
    /// id (<c>ScopeMachine.ExplicitSelect AllHostsCarrier</c>).</summary>
    public void SelectAllHostsScope() =>
        ApplyScopeDecision(ScopeMachine.step(ToScope(Scope),
            ScopeEvent.NewExplicitSelect(ScopeCarrier.AllHostsCarrier)));

    /// <summary>Re-assert the derived highlight (<see cref="ScopeMachine.highlightOf"/>) onto the
    /// current rows WITHOUT rebuilding them. A header tap transiently selects the header row through
    /// the ListBox binding, and the non-collapsible Attention header's toggle is a no-op (no rebuild),
    /// so the shell re-derives the highlight here to guarantee a group header is never left highlighted
    /// (a header is not a scope).</summary>
    public void ReassertRailHighlight() => SelectedRailEntry = ResolveHighlight();

    /// <summary>Funnel a ListBox selection change into the scope model. The click/tap gesture
    /// (<c>ShellWindow.OnHostRailTapped</c>) covers pointer input including no-op re-clicks; this is
    /// its PEER edge for selection changes that are NOT pointer taps — keyboard arrow navigation and
    /// UI-automation/screen-reader assignments to <see cref="SelectedRailEntry"/> via the two-way
    /// binding. Without it, a non-pointer selection move repaints the highlight but leaves scope on the
    /// old host, so Tasks/Projects/Transfers/EventLog stay filtered to a row the rail no longer shows
    /// as selected (Codex P2).
    ///
    /// Echo guard (load-bearing — do not drop): <see cref="RebuildRail"/> and
    /// <see cref="ReassertRailHighlight"/> write <see cref="SelectedRailEntry"/> from the scope-derived
    /// highlight, which round-trips back through the two-way binding as a selection change. Such an echo
    /// equals the current <see cref="ResolveHighlight"/> by reference (rows are stable singletons), so it
    /// drives NO scope — only a selection that DIFFERS from the derived highlight is a genuine user move.
    /// The teeth are the SingleHost case: there the highlight is the sole host row while Scope stays
    /// AllHosts (decisions §7 — no auto-pin), so without this guard a routine rebuild's echo would call
    /// <see cref="SelectHostScope"/> and silently pin+persist the lone host. It also keeps R5 structural.
    /// A header/null is never a scope; it re-asserts the derived highlight, snapping the selection off
    /// the header. The auth-failed → Edit deep link stays a pointer affordance (OnHostRailTapped), not a
    /// side effect of mere keyboard navigation.</summary>
    public void ReconcileRailSelection(object? selected)
    {
        if (ReferenceEquals(selected, ResolveHighlight()))
            return;                                  // echo of our own highlight derivation — no scope side effect
        switch (selected)
        {
            case HostRailItemViewModel host: SelectHostScope(host.HostId); break;
            case AllHostsRailItemViewModel: SelectAllHostsScope(); break;
            default: ReassertRailHighlight(); break; // header or null: never a scope; snap the highlight back
        }
    }

    // Design rule: selecting a host scopes every view. Each graduated (non-
    // Placeholder) page gets the same partial-method push; Placeholders don't
    // scope until they graduate.
    partial void OnScopeChanged(ScopeSelection value)
    {
        Tasks.Scope = value;
        Projects.Scope = value;
        Transfers.Scope = value;
        EventLog.Scope = value;
        RefreshScopedHost();
    }

    /// <summary>Re-derives the scoped-host run-mode surface (DI-4). Called on scope
    /// change and after a host reconcile (the scoped row VM may have just been
    /// created / removed). The Tasks command-bar dropdown reaches the surface through
    /// <see cref="TasksViewModel.ScopedHost"/>, pushed here alongside the shell's own
    /// <see cref="ScopedHostRow"/> so both stay in lockstep.</summary>
    private void RefreshScopedHost()
    {
        HostRailItemViewModel? row = Scope.IsAllHosts
            ? null
            : (_hostRowVms.TryGetValue(Scope.HostId!.Value, out HostRailItemViewModel? vm) ? vm : null);
        ScopedHostRow = row;
        HasScopedHost = row is not null;
        Tasks.ScopedHost = row;
    }

    private void OnTasksRowsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        TasksCount = Tasks.Rows.Count;

    private void OnTransfersRowsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        TransfersCount = Transfers.Rows.Count;

    private void OnEventLogPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EventLogViewModel.UnreadCount))
            EventLogUnread = EventLog.UnreadCount;
    }

    /// <summary>Raised when a view needs to open the Add-host dialog.</summary>
    public event EventHandler? AddHostRequested;

    partial void OnSelectedViewChanged(NavItemViewModel? value)
    {
        if (value is not null)
            CurrentPage = value.Page;
    }

    // The Event log counts unread warnings/errors only while it is NOT the visible
    // page; becoming CurrentPage activates it (zeroing the badge), leaving it (any
    // path, including NavigateToSettings) resumes counting. Every CurrentPage change
    // funnels through here, so the flag stays truthful without per-call plumbing.
    partial void OnCurrentPageChanged(object value) =>
        EventLog.IsViewActive = ReferenceEquals(value, EventLog);

    [RelayCommand]
    private void SelectView(string index)
    {
        if (int.TryParse(index, out var i) && i >= 0 && i < Views.Count)
            SelectedView = Views[i];
    }

    [RelayCommand]
    private void RequestAddHost() => AddHostRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Raised by the rail-row menu; the window owns the dialog/test/confirm work.</summary>
    public event EventHandler<Guid>? EditHostRequested;
    public event EventHandler<Guid>? TestHostRequested;
    public event EventHandler<Guid>? RemoveHostRequested;

    [RelayCommand] private void EditHost(Guid id) => EditHostRequested?.Invoke(this, id);
    [RelayCommand] private void TestHost(Guid id) => TestHostRequested?.Invoke(this, id);
    [RelayCommand] private void RemoveHost(Guid id) => RemoveHostRequested?.Invoke(this, id);

    public HostConfig? FindHostConfig(Guid id) =>
        _store.Hosts.FirstOrDefault(h => h.Config.Id == id)?.Config;
    public HostRailItemViewModel? FindHostRow(Guid id) =>
        _hostRowVms.TryGetValue(id, out var vm) ? vm : null;
    public HostRegistry Registry => Settings.Registry;
    public Func<IGuiRpcClient> ClientFactory => Settings.ClientFactory;

    public void NavigateToSettings()
    {
        SelectedView = null;
        CurrentPage = Settings;
    }

    private void OnStoreChanged(object? sender, EventArgs e) => ReconcileHosts();

    private void ReconcileHosts()
    {
        // Keep the host-VM map in sync with the registry (append-only order).
        var seen = new HashSet<Guid>();
        for (var i = 0; i < _store.Hosts.Count; i++)
        {
            HostEntry entry = _store.Hosts[i];
            seen.Add(entry.Config.Id);
            if (!_hostRowVms.TryGetValue(entry.Config.Id, out HostRailItemViewModel? vm))
                _hostRowVms[entry.Config.Id] = new HostRailItemViewModel(entry, _clock, _control);
            else
                vm.Refresh();
        }
        foreach (Guid gone in _hostRowVms.Keys.Where(k => !seen.Contains(k)).ToArray())
        {
            _hostRowVms[gone].Dispose();
            _hostRowVms.Remove(gone);
            // Host removal is a ScopeEvent. If the removed host was the scoped one, ScopeMachine
            // falls back to All hosts + clears the persisted id (R11); otherwise it is a pure no-op
            // (same scope, no persist). RebuildRail below then re-derives the highlight.
            ApplyScopeDecision(ScopeMachine.step(ToScope(Scope), ScopeEvent.NewHostRemoved(gone)));
        }
        var connected = _store.Hosts.Count(h => RailStateProjection.From(h.Status) == RailState.Connected);
        _allHosts.Update(connected, _store.Hosts.Count);
        HasHosts = _store.Hosts.Count > 0;
        RebuildRail();
        // The scoped host's row VM may have just been created (first reconcile after
        // scope restore) or removed; re-derive the run-mode surface off the fresh map.
        RefreshScopedHost();
    }

    /// <summary>The view feeds the measured footer height; the core re-evaluates the
    /// flat↔grouped fit boundary (design 3a: "window resize re-evaluates").</summary>
    public void SetRailViewportHeight(double availableHeight)
    {
        if (Math.Abs(availableHeight - _railViewportHeight) < 0.5) return;
        _railViewportHeight = availableHeight;
        RebuildRail();
    }

    /// <summary>The view reports the NavigationView pane-collapse state (compact = pane closed).
    /// Compact renders every host as an individual state-icon (decisions §5): the Healthy tier is
    /// force-expanded for the compute only (BuildRailInput), never persisted. Re-opening the pane
    /// re-derives from the saved RailHealthyExpanded, so a compact session cannot flip the saved
    /// preference. The rail rebuilds because compact must ADD the otherwise-collapsed host rows.</summary>
    public void SetRailPaneCompact(bool compact)
    {
        if (_paneCompact == compact) return;
        _paneCompact = compact;
        RebuildRail();
    }

    [RelayCommand]
    private void ToggleRailGrouping()
    {
        // The next override is pure decision logic (opposite layout, with the Auto-return
        // rule so the toggle can hide once it fits again) — the core owns it; the VM must
        // NOT re-derive fit/override logic here (this is where the toggle-Auto gap lived).
        RailOverride next = RailLayoutPolicy.toggleOverride(MapOverride(_grouping), BuildRailInput());
        _grouping =
            next.IsForceFlat ? RailGroupingMode.Flat
            : next.IsForceGrouped ? RailGroupingMode.Grouped
            : RailGroupingMode.Auto;
        _uiState.Update(s => s with { RailGrouping = _grouping });
        RebuildRail();
    }

    /// <summary>The shell's measured/persisted inputs as the core's record — the single
    /// construction point shared by <see cref="RebuildRail"/> and the toggle.</summary>
    private RailLayoutInput BuildRailInput()
    {
        var hosts = _store.Hosts
            .Select(e => new RailHost(e.Config.Id,
                RailTierProjection.From(RailStateProjection.From(e.Status))))
            .ToArray();
        var available = Math.Max(0.0, _railViewportHeight - ReservedRailChrome);
        // Compact force-expands Healthy for the compute so every host renders as an icon (§5); the
        // persisted _healthyExpanded is left untouched (SetRailPaneCompact) so re-open restores it.
        var healthyExpanded = _healthyExpanded || _paneCompact;
        return new RailLayoutInput(hosts, available, RailRowHeight, MapOverride(_grouping), healthyExpanded);
    }

    /// <summary>Toggle a rail status group's collapse state, keyed on the tier VALUE — the resilient
    /// entry point the header tap routes through (<c>ShellWindow.OnHostRailTapped</c>). A background
    /// poll/status refresh can <see cref="RebuildRail"/> between a header tap and its deferred callback,
    /// detaching the tapped header VM's <see cref="GroupHeaderRailItemViewModel.ToggleRequested"/> and
    /// clearing the rows; routing the toggle through the shell by tier (never the transient VM instance)
    /// survives that race, whereas executing the stale VM's command would raise to no subscriber and
    /// silently DROP the user's toggle (Codex R5 P2). Healthy is the only collapsible tier (Attention is
    /// pinned open, so a non-Healthy tier is a no-op — no flip, no rebuild).</summary>
    public void ToggleGroup(RailTier tier)
    {
        if (!tier.Equals(RailTier.Healthy))
            return;
        _healthyExpanded = !_healthyExpanded;
        _uiState.Update(s => s with { RailHealthyExpanded = _healthyExpanded });
        RebuildRail();
    }

    // The GroupHeaderRailItemViewModel.ToggleCommand affordance (raised via ToggleRequested; used by
    // view-model tests to expand/collapse a group programmatically) funnels into the same tier-keyed
    // shell logic. The production tap path calls ToggleGroup directly (by captured tier value), so it
    // does not depend on this subscription surviving an intervening rebuild.
    private void OnGroupToggleRequested(object? sender, RailTier tier) => ToggleGroup(tier);

#pragma warning disable CS8524 // No `_` arm on purpose: CS8509 (a new NAMED RailGroupingMode
    // left unhandled) must stay a build error so this mapping is revisited. CS8524 is the residual
    // "unnamed enum value" case — an out-of-range cast like (RailGroupingMode)999, unreachable for
    // a well-formed value — and is suppressed here; a `_` arm would silence CS8509 too and defeat
    // the guard. Same pattern as RailTierProjection.
    private static RailOverride MapOverride(RailGroupingMode mode) =>
        mode switch
        {
            RailGroupingMode.Auto => RailOverride.Auto,
            RailGroupingMode.Flat => RailOverride.ForceFlat,
            RailGroupingMode.Grouped => RailOverride.ForceGrouped,
        };
#pragma warning restore CS8524

    // --- scope translation boundary: the shell's ScopeSelection <-> the core's Scope, and the
    //     single place a ScopeDecision is applied. This is the ENTIRE scope logic in the shell. ---
    private static ScopeState ToScope(ScopeSelection s) =>
        s.IsAllHosts ? ScopeState.AllHosts : ScopeState.NewHost(s.HostId!.Value);

    private static ScopeSelection ToSelection(ScopeState s) =>
        s is ScopeState.Host h ? new ScopeSelection(h.Item) : ScopeSelection.AllHosts;

    /// <summary>Apply a ScopeMachine decision: set the scope (fires OnScopeChanged → view-push)
    /// and run its persistence action. The ONLY place the shell writes Scope or ScopeHostId.</summary>
    private void ApplyScopeDecision(ScopeDecision decision)
    {
        Scope = ToSelection(decision.Scope);
        if (decision.Persist is PersistAction.PersistExplicit pe)
            _uiState.Update(s => s with { ScopeHostId = pe.Item is null ? (Guid?)null : pe.Item.Value });
        else if (decision.Persist.IsClearPersisted)
            _uiState.Update(s => s with { ScopeHostId = null });
        // PersistAction.NoPersistChange → leave persistence untouched.
    }

    private void RebuildRail()
    {
        RailLayoutInput input = BuildRailInput();
        RailLayout layout = RailLayoutPolicy.compute(input);
        ShowRailToggle = layout.ShowToggle;
        HostRowHeight = layout.Mode.IsGrouped ? GroupedHostRowHeight : RailRowHeight;

        // RebuildRail constructs NO ScopeEvent, so it cannot mutate or persist Scope (R5). It
        // rematerializes the rows and derives the highlight from ScopeMachine.highlightOf.
        // SelectedRailEntry is a pure highlight with no scope side effect (explicit scope rides the
        // click gesture → SelectHostScope / SelectAllHostsScope), so no _rebuilding guard is needed.
        foreach (var g in RailEntries.OfType<GroupHeaderRailItemViewModel>())
            g.ToggleRequested -= OnGroupToggleRequested;
        RailEntries.Clear();
        foreach (RailRow row in layout.Rows)
            RailEntries.Add(MaterializeRow(row));

        SelectedRailEntry = ResolveHighlight();
    }

    /// <summary>Highlight = the pure ScopeMachine.highlightOf(scope, soleHost, visibleHostIds) —
    /// never a scope mutation. SingleHost highlights the sole host row even though Scope stays
    /// All hosts; a scoped host hidden in a collapsed group yields no highlight (Scope still holds
    /// it); otherwise the All-hosts sentinel.</summary>
    private object? ResolveHighlight()
    {
        var hostRows = RailEntries.OfType<HostRailItemViewModel>().ToArray();
        var visibleHostIds = hostRows.Select(h => h.HostId).ToArray();
        // SingleHost presentation ⇔ a lone host row with no All-hosts sentinel: RailLayoutPolicy
        // emits [HostRow] for exactly one host and AllHostsRow-led rows for ≥2, so this reads the
        // mode straight off the materialized rows (avoids threading the RailLayout in for callers
        // like ReassertRailHighlight that have no fresh layout). The sole row is highlighted
        // regardless of scope (Scope stays AllHosts — data-identical for one host).
        Guid? soleHost = hostRows.Length == 1 && !RailEntries.OfType<AllHostsRailItemViewModel>().Any()
            ? hostRows[0].HostId
            : (Guid?)null;
        RailHighlight highlight = ScopeMachine.highlightOf(ToScope(Scope), soleHost, visibleHostIds);
        if (highlight is RailHighlight.HighlightHostRow hr)
            return _hostRowVms.TryGetValue(hr.Item, out var vm) ? vm : null;
        return highlight.IsHighlightAllHostsRow ? _allHosts : null;   // else NoHighlight (hidden scoped host)
    }

    private object MaterializeRow(RailRow row)
    {
        if (row.IsAllHostsRow) return _allHosts;
        if (row is RailRow.HostRow hr) return _hostRowVms[hr.Item];
        var gh = (RailRow.GroupHeaderRow)row;
        var vm = new GroupHeaderRailItemViewModel(gh.tier, gh.count, gh.expanded);
        vm.ToggleRequested += OnGroupToggleRequested;
        return vm;
    }

    public void Dispose()
    {
        EditHostRequested = null;
        TestHostRequested = null;
        RemoveHostRequested = null;
        _store.Changed -= OnStoreChanged;
        Tasks.Rows.CollectionChanged -= OnTasksRowsChanged;
        Transfers.Rows.CollectionChanged -= OnTransfersRowsChanged;
        EventLog.PropertyChanged -= OnEventLogPropertyChanged;
        Tasks.Dispose();
        Projects.Dispose();
        Transfers.Dispose();
        EventLog.Dispose();
        foreach (var g in RailEntries.OfType<GroupHeaderRailItemViewModel>())
            g.ToggleRequested -= OnGroupToggleRequested;
        foreach (HostRailItemViewModel item in _hostRowVms.Values)
            item.Dispose();
    }
}
