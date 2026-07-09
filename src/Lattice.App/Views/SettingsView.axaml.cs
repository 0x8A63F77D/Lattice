using Avalonia.Controls;
using FluentAvalonia.UI.Controls;
using Lattice.App.ViewModels;

namespace Lattice.App.Views;

public partial class SettingsView : UserControl
{
    private SettingsViewModel? _subscribed;

    public SettingsView() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        // Guard against double-subscription: DataContext can change more than once
        // (or back to the same VM), so detach the previously wired VM first.
        if (_subscribed is not null)
            _subscribed.RemoveRequested -= OnRemoveRequested;
        _subscribed = DataContext as SettingsViewModel;
        if (_subscribed is not null)
            _subscribed.RemoveRequested += OnRemoveRequested;
    }

    private async void OnRemoveRequested(object? sender, HostSettingsItemViewModel item)
    {
        // ContentDialog needs its owning TopLevel to place its overlay; without a
        // window there is nothing to confirm against, so bail rather than throw.
        if (TopLevel.GetTopLevel(this) is not { } top)
            return;
        var dialog = new FAContentDialog
        {
            Title = $"Remove {item.DisplayName}?",
            Content = "Lattice stops monitoring this host. The BOINC client on the host is not affected.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = FAContentDialogButton.Close,
        };
        FAContentDialogResult result = await dialog.ShowAsync(top);
        if (result == FAContentDialogResult.Primary && DataContext is SettingsViewModel vm)
            vm.Remove(item.HostId);
    }
}
