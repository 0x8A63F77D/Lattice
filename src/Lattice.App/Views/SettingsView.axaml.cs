using Avalonia.Controls;
using FluentAvalonia.UI.Controls;
using Lattice.App.ViewModels;

namespace Lattice.App.Views;

public partial class SettingsView : UserControl
{
    private SettingsViewModel? _subscribed;
    private bool _removeConfirmInFlight;

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
        // Single-flight: a double-click fires RemoveRequested twice before the
        // first dialog resolves; a second dialog would stack in the OverlayLayer
        // and resolving both would call Remove twice (the second throws).
        if (_removeConfirmInFlight)
            return;
        // ContentDialog needs its owning TopLevel to place its overlay; without a
        // window there is nothing to confirm against, so bail rather than throw.
        if (TopLevel.GetTopLevel(this) is not { } top)
            return;
        _removeConfirmInFlight = true;
        try
        {
            var dialog = new FAContentDialog
            {
                Title = $"Remove {item.DisplayName}?",
                Content = "Lattice stops monitoring this host. The BOINC client on the host is not affected.",
                PrimaryButtonText = "Remove",
                CloseButtonText = "Cancel",
                DefaultButton = FAContentDialogButton.Close,
            };
            FAContentDialogResult result = await dialog.ShowAsync(top);
            // Tolerate removal races: if the host vanished while the dialog was
            // open (e.g. removed elsewhere), a stale confirmation must not call
            // Remove on a gone host — HostRegistry.RemoveHost throws on unknown ids.
            if (result == FAContentDialogResult.Primary
                && DataContext is SettingsViewModel vm
                && vm.Hosts.Any(h => h.HostId == item.HostId))
                vm.Remove(item.HostId);
        }
        finally
        {
            _removeConfirmInFlight = false;
        }
    }
}
