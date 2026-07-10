using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lattice.App.Infrastructure;
using Lattice.Boinc.GuiRpc;
using Lattice.Core;

namespace Lattice.App.ViewModels;

/// <summary>
/// Owns the rail (views + hosts), the global scope, and the current page.
/// UI-thread only.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly HostStore _store;
    private readonly IUiClock _clock;

    public ShellViewModel(
        HostRegistry registry, HostStore store, IUiClock clock, Func<IGuiRpcClient> clientFactory)
    {
        _store = store;
        _clock = clock;
        Settings = new SettingsViewModel(registry, store, clientFactory);
        Views =
        [
            new NavItemViewModel("Tasks", "IconTaskListSquareLtrRegular", "IconTaskListSquareLtrFilled", new PlaceholderViewModel("Tasks")),
            new NavItemViewModel("Projects", "IconGridRegular", "IconGridFilled", new PlaceholderViewModel("Projects")),
            new NavItemViewModel("Transfers", "IconArrowSwapRegular", "IconArrowSwapFilled", new PlaceholderViewModel("Transfers")),
            new NavItemViewModel("Event log", "IconDocumentTextRegular", "IconDocumentTextFilled", new PlaceholderViewModel("Event log")),
        ];
        _selectedView = Views[0];
        _currentPage = Views[0].Page;
        store.Changed += OnStoreChanged;
        ReconcileHosts();
    }

    public IReadOnlyList<NavItemViewModel> Views { get; }
    public ObservableCollection<HostRailItemViewModel> HostItems { get; } = [];
    public SettingsViewModel Settings { get; }

    [ObservableProperty] private NavItemViewModel? _selectedView;
    [ObservableProperty] private object _currentPage;
    [ObservableProperty] private ScopeSelection _scope = ScopeSelection.AllHosts;
    [ObservableProperty] private bool _hasHosts;

    /// <summary>Raised when a view needs to open the Add-host dialog.</summary>
    public event EventHandler? AddHostRequested;

    partial void OnSelectedViewChanged(NavItemViewModel? value)
    {
        if (value is not null)
            CurrentPage = value.Page;
    }

    [RelayCommand]
    private void SelectView(string index)
    {
        if (int.TryParse(index, out var i) && i >= 0 && i < Views.Count)
            SelectedView = Views[i];
    }

    [RelayCommand]
    private void RequestAddHost() => AddHostRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Auth-failed rail linkage and the Settings footer both land here.</summary>
    public void NavigateToSettings(Guid? focusHostId = null)
    {
        SelectedView = null;
        CurrentPage = Settings;
        if (focusHostId is { } id)
            Settings.ExpandHost(id);
    }

    private void OnStoreChanged(object? sender, EventArgs e) => ReconcileHosts();

    private void ReconcileHosts()
    {
        // Keyed reconcile: keep VMs whose host still exists (their Refresh reads
        // the live entry), add new, drop removed. Order matches the registry
        // because hosts are append-only today (no reorder API); revisit the
        // insert position if reordering ever lands.
        var byId = HostItems.ToDictionary(i => i.HostId);
        var seen = new HashSet<Guid>();
        for (var i = 0; i < _store.Hosts.Count; i++)
        {
            HostEntry entry = _store.Hosts[i];
            seen.Add(entry.Config.Id);
            if (!byId.TryGetValue(entry.Config.Id, out HostRailItemViewModel? item))
                HostItems.Insert(Math.Min(i, HostItems.Count), new HostRailItemViewModel(entry, _clock));
            else
                item.Refresh();
        }
        for (var i = HostItems.Count - 1; i >= 0; i--)
            if (!seen.Contains(HostItems[i].HostId))
            {
                HostItems[i].Dispose();
                HostItems.RemoveAt(i);
            }
        HasHosts = HostItems.Count > 0;
        Settings.Reconcile();
    }

    public void Dispose()
    {
        _store.Changed -= OnStoreChanged;
        foreach (HostRailItemViewModel item in HostItems)
            item.Dispose();
    }
}
