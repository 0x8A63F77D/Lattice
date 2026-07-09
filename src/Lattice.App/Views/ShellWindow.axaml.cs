using Avalonia.Controls;
using FluentAvalonia.UI.Controls;
using Lattice.App.ViewModels;

namespace Lattice.App.Views;

public partial class ShellWindow : Window
{
    public ShellWindow() => InitializeComponent();

    private ShellViewModel? Shell => DataContext as ShellViewModel;

    private void OnNavSelectionChanged(object? sender, FANavigationViewSelectionChangedEventArgs e)
    {
        if (Shell is null || e.SelectedItem is not FANavigationViewItem { Tag: string tag })
            return;
        if (tag == "settings")
            Shell.NavigateToSettings();
        else
            Shell.SelectViewCommand.Execute(tag);
    }

    private void OnHostSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Shell is null)
            return;
        if (HostList.SelectedItem is HostRailItemViewModel item)
        {
            Shell.Scope = new ScopeSelection(item.HostId);
            // Design: clicking an auth-failed host navigates to Settings with that
            // host's expander open (the one cross-view linkage).
            if (item.State == RailState.AuthFailed)
                Shell.NavigateToSettings(item.HostId);
        }
        else
        {
            Shell.Scope = ScopeSelection.AllHosts;
        }
    }
}
