using CommunityToolkit.Mvvm.ComponentModel;
using Lattice.App.Infrastructure;
using Lattice.Boinc.GuiRpc;
using Lattice.Core;

namespace Lattice.App.ViewModels;

public sealed partial class HostSettingsItemViewModel : ObservableObject
{
    private readonly HostEntry _entry;
    private readonly HostRegistry _registry;
    private readonly Func<IGuiRpcClient> _clientFactory;

    public HostSettingsItemViewModel(HostEntry entry, HostRegistry registry, Func<IGuiRpcClient> clientFactory)
    {
        _entry = entry;
        _registry = registry;
        _clientFactory = clientFactory;
        RefreshFromEntry();
    }

    public Guid HostId => _entry.Config.Id;

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private string _displayName = "";

    public void RefreshFromEntry() => DisplayName = _entry.Config.DisplayName;
}
