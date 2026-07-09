using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Lattice.App.Infrastructure;
using Lattice.Boinc.GuiRpc;
using Lattice.Core;

namespace Lattice.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly HostRegistry _registry;
    private readonly HostStore _store;
    private readonly Func<IGuiRpcClient> _clientFactory;

    public SettingsViewModel(HostRegistry registry, HostStore store, Func<IGuiRpcClient> clientFactory)
    {
        _registry = registry;
        _store = store;
        _clientFactory = clientFactory;
        Reconcile();
    }

    public ObservableCollection<HostSettingsItemViewModel> Hosts { get; } = [];

    public void ExpandHost(Guid hostId)
    {
        foreach (HostSettingsItemViewModel host in Hosts)
            host.IsExpanded = host.HostId == hostId;
    }

    /// <summary>Called by the shell when the store's host list changed.</summary>
    public void Reconcile()
    {
        var byId = Hosts.ToDictionary(h => h.HostId);
        var seen = new HashSet<Guid>();
        for (var i = 0; i < _store.Hosts.Count; i++)
        {
            HostEntry entry = _store.Hosts[i];
            seen.Add(entry.Config.Id);
            if (!byId.TryGetValue(entry.Config.Id, out HostSettingsItemViewModel? item))
                Hosts.Insert(Math.Min(i, Hosts.Count),
                    new HostSettingsItemViewModel(entry, _registry, _clientFactory));
            else
                item.RefreshFromEntry();
        }
        for (var i = Hosts.Count - 1; i >= 0; i--)
            if (!seen.Contains(Hosts[i].HostId))
                Hosts.RemoveAt(i);
    }
}
