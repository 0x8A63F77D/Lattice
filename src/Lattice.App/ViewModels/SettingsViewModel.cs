using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Lattice.App.Infrastructure;
using Lattice.App.Localization;
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

    /// <summary>Inline error under the polling expander when persisting the interval fails.</summary>
    [ObservableProperty] private string? _pollingError;

    public int PollingIntervalSeconds
    {
        get => _registry.PollingIntervalSeconds;
        set
        {
            if (value != _registry.PollingIntervalSeconds)
            {
                PollingError = RegistryGuard.TryMutate(() => _registry.SetPollingInterval(value)) is { } error
                    ? string.Format(Strings.SettingsIntervalSaveFailedFmt, error)
                    : null;
                // On failure the registry kept its old value; re-raising snaps the
                // ComboBox back so the UI never shows an interval that isn't live.
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Bubbled from items; the view shows the confirm dialog, then calls Remove.</summary>
    public event EventHandler<HostSettingsItemViewModel>? RemoveRequested;

    /// <summary>Test seam: whether any view is still listening (leak pin — the VM outlives page visits).</summary>
    internal bool HasRemoveSubscribersForTests => RemoveRequested is not null;

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

    /// <summary>Removes the host. Null on success, user-facing failure text otherwise.</summary>
    public string? Remove(Guid hostId) =>
        RegistryGuard.TryMutate(() => _registry.RemoveHost(hostId)) is { } error
            ? string.Format(Strings.SettingsRemoveFailedFmt, error)
            : null;

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
