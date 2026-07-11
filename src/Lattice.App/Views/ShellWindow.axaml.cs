using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;
using Lattice.App.ViewModels;

namespace Lattice.App.Views;

public partial class ShellWindow : Window
{
    private ShellViewModel? _shell;
    private bool _addHostInFlight;

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
        }
        _shell = DataContext as ShellViewModel;
        if (_shell is not null)
        {
            _shell.PropertyChanged += OnShellPropertyChanged;
            _shell.AddHostRequested += OnAddHostRequested;
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
        if (_shell is null)
            return;
        // Scope itself tracks SelectedRailEntry through the XAML TwoWay binding;
        // this handler only owns the one remaining cross-view linkage: clicking an
        // auth-failed host jumps to Settings with that host's expander open. The
        // All-hosts sentinel never navigates.
        if (HostList.SelectedItem is HostRailItemViewModel { State: RailState.AuthFailed } item)
            _shell.NavigateToSettings(item.HostId);
    }
}
