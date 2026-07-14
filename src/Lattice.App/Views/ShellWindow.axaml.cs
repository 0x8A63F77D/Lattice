using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Reactive;
using FluentAvalonia.UI.Controls;
using Lattice.App.Aggregation;
using Lattice.App.Infrastructure;
using Lattice.App.Localization;
using Lattice.App.ViewModels;
using Lattice.Core;

namespace Lattice.App.Views;

public partial class ShellWindow : Window
{
    private ShellViewModel? _shell;
    private bool _addHostInFlight;
    private bool _editHostInFlight;
    private bool _removeConfirmInFlight;
    // Same-row reentrancy guard only: two Tests on the SAME host racing would both
    // write row.TestResultText. Different rows testing concurrently is fine, so this
    // is per-host rather than the single bool Edit/Remove use.
    private readonly HashSet<Guid> _testHostsInFlight = [];
    private IDisposable? _navBoundsSubscription;
    private IDisposable? _navPaneSubscription;

    // Test seam (InternalsVisibleTo): the header-tap group toggle is deferred (see OnHostRailTapped).
    // Behind IUiDispatcher so a test can swap in a QueueUiDispatcher and drain the deferral EXPLICITLY,
    // deterministically interleaving a rail rebuild between the tap and the toggle (the tap↔poll race,
    // R5 P2) — real pointer input auto-pumps the UI dispatcher, collapsing that window. Production posts
    // onto the real UI thread (identical to the former inline Dispatcher.UIThread.Post).
    internal IUiDispatcher RailDeferralDispatcher { get; set; } = AvaloniaUiDispatcher.Instance;

    // Regular/Filled StreamGeometry resources are Application-level singletons
    // (Icons.axaml), so a single TryFindResource per key is enough to cache the
    // reference for the life of the window.
    private readonly Dictionary<string, Geometry> _iconGeometryCache = new();

    public ShellWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => AttachShell();
    }

    private void AttachShell()
    {
        if (_shell is not null)
        {
            _shell.PropertyChanged -= OnShellPropertyChanged;
            _shell.AddHostRequested -= OnAddHostRequested;
            _shell.EditHostRequested -= OnEditHostRequested;
            _shell.TestHostRequested -= OnTestHostRequested;
            _shell.RemoveHostRequested -= OnRemoveHostRequested;
            _navBoundsSubscription?.Dispose();
            _navBoundsSubscription = null;
            _navPaneSubscription?.Dispose();
            _navPaneSubscription = null;
        }
        _shell = DataContext as ShellViewModel;
        if (_shell is not null)
        {
            _shell.PropertyChanged += OnShellPropertyChanged;
            _shell.AddHostRequested += OnAddHostRequested;
            _shell.EditHostRequested += OnEditHostRequested;
            _shell.TestHostRequested += OnTestHostRequested;
            _shell.RemoveHostRequested += OnRemoveHostRequested;
            // Feed the measured pane height so the core re-evaluates the flat↔grouped fit
            // boundary on every layout/resize (design 3a; decisions §3). The shell derives
            // AvailableHeight = paneContentHeight − ReservedRailChrome from this.
            // AnonymousObserver is Avalonia's Rx-free observer wrapper (the lambda .Subscribe
            // overload lives in System.Reactive, which this app does not reference).
            _navBoundsSubscription = Nav.GetObservable(Avalonia.Visual.BoundsProperty)
                .Subscribe(new AnonymousObserver<Rect>(b => _shell?.SetRailViewportHeight(b.Height)));
            // Compact (48px) pane hides all row text; a Healthy group collapsed by default would then
            // show NO healthy host icons and no tappable header (decisions §5). Feed the pane-collapse
            // state so the VM force-expands Healthy for the compute (icons only) while the pane is
            // compact. GetObservable pushes the current value on subscribe, seeding the initial state.
            _navPaneSubscription = Nav.GetObservable(FANavigationView.IsPaneOpenProperty)
                .Subscribe(new AnonymousObserver<bool>(open => _shell?.SetRailPaneCompact(!open)));
            SyncNavSelection();
            // First render must not depend on the SelectionChanged side channel:
            // whether the Nav.SelectedItem assignment above raises it synchronously
            // is undocumented FluentAvalonia behavior, so set the icons explicitly.
            UpdateMenuIcons();
        }
    }

    private async void OnAddHostRequested(object? sender, EventArgs e)
    {
        // Single-flight: the CTA and the rail's + button both raise AddHostRequested;
        // a double-fire before the first dialog resolves would stack a second overlay.
        if (_addHostInFlight || _shell is not { } shell)
            return;
        _addHostInFlight = true;
        try
        {
            var vm = new AddHostViewModel(shell.Settings.Registry, shell.Settings.ClientFactory);
            var dialog = new AddHostDialog { DataContext = vm };
            if (TopLevel.GetTopLevel(this) is { } top)
                await dialog.ShowAsync(top);
        }
        finally
        {
            _addHostInFlight = false;
        }
    }

    private async void OnEditHostRequested(object? sender, Guid id) =>
        await OpenEditHostDialog(id, authError: false);

    private async Task OpenEditHostDialog(Guid id, bool authError)
    {
        if (_editHostInFlight || _shell is not { } shell || shell.FindHostConfig(id) is not { } cfg)
            return;
        _editHostInFlight = true;
        try
        {
            var vm = AddHostViewModel.ForEdit(shell.Registry, shell.ClientFactory, cfg, authError);
            var dialog = new AddHostDialog { DataContext = vm };
            if (TopLevel.GetTopLevel(this) is { } top)
                await dialog.ShowAsync(top);
        }
        finally { _editHostInFlight = false; }
    }

    private async void OnTestHostRequested(object? sender, Guid id)
    {
        if (_shell is not { } shell || shell.FindHostConfig(id) is not { } cfg
            || shell.FindHostRow(id) is not { } row
            || !_testHostsInFlight.Add(id))
            return;
        row.TestResultText = Strings.SettingsTestConnectionBusy;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var r = await HostMonitorManager.TestConnectionAsync(cfg, shell.ClientFactory, cts.Token);
            row.TestResultText = r.Success
                ? string.Format(Strings.SettingsTestConnectionSuccess, r.Version!.Major, r.Version.Minor, r.Version.Release)
                : r.Error;
        }
        catch (OperationCanceledException) { row.TestResultText = Strings.SettingsTestConnectionTimeout; }
        finally { _testHostsInFlight.Remove(id); }
    }

    private async void OnRemoveHostRequested(object? sender, Guid id)
    {
        if (_removeConfirmInFlight || _shell is not { } shell
            || shell.FindHostConfig(id) is not { } cfg
            || TopLevel.GetTopLevel(this) is not { } top)
            return;
        _removeConfirmInFlight = true;
        try
        {
            var dialog = new FAContentDialog
            {
                Title = string.Format(Strings.HostRemoveConfirmTitleFmt, cfg.DisplayName),
                Content = Strings.HostRemoveConfirmBody,
                PrimaryButtonText = Strings.HostRemoveConfirmPrimary,
                CloseButtonText = Strings.HostRemoveConfirmCancel,
                DefaultButton = FAContentDialogButton.Close,
            };
            if (await dialog.ShowAsync(top) == FAContentDialogResult.Primary
                && shell.FindHostConfig(id) is not null
                // Persistence can fail (unwritable config.json); the old Settings card
                // surfaced that error and kept the host rather than closing silently.
                && RegistryGuard.TryMutate(() => shell.Registry.RemoveHost(id)) is { } error
                && shell.FindHostRow(id) is { } row)
                row.TestResultText = string.Format(Strings.HostRemoveFailedFmt, error);
        }
        finally { _removeConfirmInFlight = false; }
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.SelectedView) or nameof(ShellViewModel.CurrentPage))
            SyncNavSelection();
    }

    /// <summary>
    /// VM → view sync: programmatic navigation (Ctrl+D1..4, auth-failed → Settings)
    /// changes SelectedView/CurrentPage without touching the NavigationView, so the
    /// rail highlight must follow the VM here. The reverse edge (assignment fires
    /// SelectionChanged → OnNavSelectionChanged → command) terminates because the
    /// VM's SetProperty equality guard suppresses the redundant PropertyChanged.
    /// </summary>
    private void SyncNavSelection()
    {
        if (_shell is null)
            return;
        FANavigationViewItem? target =
            ReferenceEquals(_shell.CurrentPage, _shell.Settings) ? NavSettings
            : _shell.SelectedView is { } view ? MenuItemFor(view)
            : null;
        if (!ReferenceEquals(Nav.SelectedItem, target))
            Nav.SelectedItem = target;
    }

    private FANavigationViewItem? MenuItemFor(NavItemViewModel view)
    {
        for (var i = 0; i < _shell!.Views.Count; i++)
            if (ReferenceEquals(_shell.Views[i], view))
                return i switch
                {
                    0 => NavTasks,
                    1 => NavProjects,
                    2 => NavTransfers,
                    3 => NavEventLog,
                    _ => null,
                };
        return null;
    }

    private void OnNavSelectionChanged(object? sender, FANavigationViewSelectionChangedEventArgs e)
    {
        if (_shell is null || e.SelectedItem is not FANavigationViewItem { Tag: string tag })
            return;
        if (tag == "settings")
            _shell.NavigateToSettings();
        else
            _shell.SelectViewCommand.Execute(tag);
        UpdateMenuIcons();
    }

    // Recomputes every rail item's icon from scratch (rather than tracking the
    // previously-selected item) so a deselect is just "not selected" on the next
    // pass — no separate restore-to-Regular path to keep in sync.
    private void UpdateMenuIcons()
    {
        if (_shell is null)
            return;
        for (var i = 0; i < _shell.Views.Count; i++)
        {
            NavItemViewModel view = _shell.Views[i];
            if (MenuItemFor(view) is { IconSource: FAPathIconSource icon })
            {
                bool selected = ReferenceEquals(_shell.SelectedView, view);
                icon.Data = ResolveIconGeometry(selected ? view.IconFilledKey : view.IconKey);
            }
        }
    }

    private Geometry ResolveIconGeometry(string key)
    {
        if (_iconGeometryCache.TryGetValue(key, out Geometry? geometry))
            return geometry;
        // Fail fast on a miss, and never cache one (a cached null would blank the
        // icon forever with no diagnostic). The keys live as raw strings in
        // ShellViewModel's Views list with zero compile-time checking — same
        // philosophy as TasksView's header-lookup invariant: a typo throws the
        // first time any headless test constructs the shell, not silently at 2am.
        if (!this.TryFindResource(key, out object? value) || value is not Geometry found)
            throw new InvalidOperationException($"Nav icon resource '{key}' not found or not a Geometry.");
        _iconGeometryCache[key] = found;
        return found;
    }

    // The click/tap gesture is the SINGLE source of truth for rail scope selection and group-header
    // toggling — NOT SelectionChanged, which misses clicks that do not change the selection (the sole
    // preselected host, or re-clicking the already-scoped host) and forced a manual selection-revert
    // that clobbered the derived highlight (Codex round-4 P2). Tapped is Avalonia's click gesture
    // (press+release on the same element); DataContext inherits down the row template, so the tapped
    // child carries its row's view model. An empty-area tap hits a control whose DataContext is the
    // ShellViewModel — none of the cases — and no-ops. Highlight is owned by ScopeMachine.highlightOf
    // via RebuildRail / ReassertRailHighlight, never restored by hand here.
    private void OnHostRailTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (_shell is null)
            return;
        switch ((e.Source as Control)?.DataContext)
        {
            case HostRailItemViewModel host:
                // The sole explicit host-scope gesture (decisions §7): persists ScopeHostId even when
                // the selection did not change. An auth-failed host additionally opens the Edit dialog
                // with the password error (round-1). One gesture ⇒ no double scope event, no double
                // dialog-open (_editHostInFlight backstops re-entrancy).
                _shell.SelectHostScope(host.HostId);
                if (host.State == RailState.AuthFailed)
                    OpenAuthFailedEditDialog(host.HostId);
                break;
            case AllHostsRailItemViewModel:
                _shell.SelectAllHostsScope();
                break;
            case GroupHeaderRailItemViewModel header:
                // A header is not a scope. Toggle its group (collapsible Healthy only), then re-assert
                // the derived highlight so the header — transiently selected by this click, and NOT
                // rebuilt by the no-op Attention toggle — is never left highlighted. Deferred so the
                // toggle's ItemsSource rebuild does not run inside the ListBox's click/selection
                // processing (a synchronous Clear there indexes a stale ItemsSourceView → crash, Task 8).
                //
                // Capture the tier VALUE and its collapsibility NOW, and route the deferred toggle
                // through the shell by tier — NEVER execute the captured (transient) header VM after the
                // deferral. A routine background poll/status refresh can RebuildRail in the gap, which
                // detaches the old header VMs' ToggleRequested and clears the rows; executing the stale
                // VM's ToggleCommand would then raise to no subscriber and silently DROP the user's
                // expand/collapse click (Codex R5 P2). ToggleGroup resolves against the CURRENT rows.
                RailTier tier = header.Tier;
                bool collapsible = header.IsCollapsible;   // Attention is not collapsible → no-op
                RailDeferralDispatcher.Post(() =>
                {
                    if (_shell is null)
                        return;
                    if (collapsible)
                        _shell.ToggleGroup(tier);
                    _shell.ReassertRailHighlight();
                });
                break;
        }
    }

    // Peer of OnHostRailTapped for NON-pointer selection changes (keyboard arrow nav, UI
    // automation / screen readers assigning SelectedItem). The tap gesture cannot see these,
    // so without this edge the highlight would move while scope stayed put (Codex P2). The VM's
    // ReconcileRailSelection ignores echoes of its own scope-derived highlight (R5 stays structural)
    // and only commits scope for a genuine user move — pointer taps still also route through
    // OnHostRailTapped, harmlessly idempotent.
    private void OnHostRailSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_shell is null || sender is not ListBox list)
            return;
        _shell.ReconcileRailSelection(list.SelectedItem);
    }

    // async void (not fire-and-forget `_ = ...`) so exceptions surface to the
    // dispatcher's unhandled-exception path the same way the other event handlers
    // in this file do, instead of being silently swallowed by a discarded Task.
    private async void OpenAuthFailedEditDialog(Guid id) =>
        await OpenEditHostDialog(id, authError: true);
}
