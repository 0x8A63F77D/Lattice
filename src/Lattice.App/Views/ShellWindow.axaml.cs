using System.ComponentModel;
using Avalonia.Controls;
using FluentAvalonia.UI.Controls;
using Lattice.App.ViewModels;

namespace Lattice.App.Views;

public partial class ShellWindow : Window
{
    private ShellViewModel? _shell;

    public ShellWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => AttachShell();
    }

    private void AttachShell()
    {
        if (_shell is not null)
            _shell.PropertyChanged -= OnShellPropertyChanged;
        _shell = DataContext as ShellViewModel;
        if (_shell is not null)
        {
            _shell.PropertyChanged += OnShellPropertyChanged;
            SyncNavSelection();
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
    }

    private void OnHostSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_shell is null)
            return;
        if (HostList.SelectedItem is HostRailItemViewModel item)
        {
            _shell.Scope = new ScopeSelection(item.HostId);
            // Design: clicking an auth-failed host navigates to Settings with that
            // host's expander open (the one cross-view linkage).
            if (item.State == RailState.AuthFailed)
                _shell.NavigateToSettings(item.HostId);
        }
        else
        {
            _shell.Scope = ScopeSelection.AllHosts;
        }
    }
}
