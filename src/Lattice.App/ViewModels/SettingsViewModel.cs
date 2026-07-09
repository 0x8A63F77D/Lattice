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

    /// <summary>Exposed for the Add-host dialog, which registers into the same registry/factory.</summary>
    public HostRegistry Registry => _registry;
    public Func<IGuiRpcClient> ClientFactory => _clientFactory;

    public static IReadOnlyList<int> AllowedPollingIntervals => LatticeConfig.AllowedPollingIntervals;

    public int PollingIntervalSeconds
    {
        get => _registry.PollingIntervalSeconds;
        set
        {
            if (value != _registry.PollingIntervalSeconds)
            {
                _registry.SetPollingInterval(value);
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Bubbled from items; the view shows the confirm dialog, then calls Remove.</summary>
    public event EventHandler<HostSettingsItemViewModel>? RemoveRequested;

    public void ExpandHost(Guid hostId)
    {
        foreach (HostSettingsItemViewModel host in Hosts)
        {
            host.IsExpanded = host.HostId == hostId;
            // The auth-failed rail linkage focuses this host directly; refresh it
            // from its entry so the expanded card reflects live state (e.g. the
            // auth-error banner) without depending on a prior store reconcile.
            if (host.IsExpanded)
                host.RefreshFromEntry();
        }
    }

    public void Remove(Guid hostId) => _registry.RemoveHost(hostId);

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
            {
                item = new HostSettingsItemViewModel(entry, _registry, _clientFactory);
                item.RemoveRequested += (_, _) => RemoveRequested?.Invoke(this, item);
                Hosts.Insert(Math.Min(i, Hosts.Count), item);
            }
            else
                item.RefreshFromEntry();
        }
        for (var i = Hosts.Count - 1; i >= 0; i--)
            if (!seen.Contains(Hosts[i].HostId))
                Hosts.RemoveAt(i);
    }
}
