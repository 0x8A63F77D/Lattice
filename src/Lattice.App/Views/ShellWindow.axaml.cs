using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Reactive;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
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
    private bool _revertingRailSelection;
    private IDisposable? _navBoundsSubscription;

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
        await OpenEditHostDialog(id, focusPassword: false, authError: false);

    private async Task OpenEditHostDialog(Guid id, bool focusPassword, bool authError)
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
            || shell.FindHostRow(id) is not { } row)
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

    private void OnHostSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_shell is null || _revertingRailSelection)
            return;

        if (HostList.SelectedItem is GroupHeaderRailItemViewModel header)
        {
            // Toggling expands/collapses the group, which rebuilds RailEntries (Clear + re-add).
            // Mutating the ItemsSource synchronously inside SelectionChanged corrupts the ListBox
            // selection model — when a collapse removes the previously-selected host row, the
            // selection processing indexes a now-stale ItemsSourceView (ArgumentOutOfRange in
            // ItemsSourceView.GetAt). Defer the toggle + selection revert out of this event's
            // callstack so the rebuild happens against a settled selection.
            var previous = e.RemovedItems.Count > 0 ? e.RemovedItems[0] : null;
            Dispatcher.UIThread.Post(() =>
            {
                if (_shell is null)
                    return;
                header.ToggleCommand.Execute(null);   // expand/collapse; RebuildRail refreshes rows
                // A header is not a scope; restore the prior selection without recursing.
                _revertingRailSelection = true;
                try { HostList.SelectedItem = previous; }
                finally { _revertingRailSelection = false; }
            });
            return;
        }

        // Scope itself tracks SelectedRailEntry through the XAML TwoWay binding;
        // this handler only owns the one remaining cross-view linkage: clicking an
        // auth-failed host jumps to Settings with that host's expander open. The
        // All-hosts sentinel never navigates.
        // NOTE (Task 12): swap this for OpenEditHostDialog(item.HostId, focusPassword: true, authError: true).
        if (HostList.SelectedItem is HostRailItemViewModel { State: RailState.AuthFailed } item)
            _shell.NavigateToSettings(item.HostId);
    }
}
