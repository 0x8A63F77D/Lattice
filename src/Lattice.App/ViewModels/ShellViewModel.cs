using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lattice.App.Infrastructure;
using Lattice.App.Localization;
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
    private readonly AllHostsRailItemViewModel _allHosts = new();

    public ShellViewModel(
        HostRegistry registry, HostStore store, IUiClock clock, Func<IGuiRpcClient> clientFactory)
    {
        _store = store;
        _clock = clock;
        Settings = new SettingsViewModel(registry, store, clientFactory);
        Tasks = new TasksViewModel(store, clock);
        Views =
        [
            new NavItemViewModel(Strings.NavTasks, "IconTaskListSquareLtrRegular", "IconTaskListSquareLtrFilled", Tasks),
            new NavItemViewModel(Strings.NavProjects, "IconGridRegular", "IconGridFilled", new PlaceholderViewModel(Strings.NavProjects)),
            new NavItemViewModel(Strings.NavTransfers, "IconArrowSwapRegular", "IconArrowSwapFilled", new PlaceholderViewModel(Strings.NavTransfers)),
            new NavItemViewModel(Strings.NavEventLog, "IconDocumentTextRegular", "IconDocumentTextFilled", new PlaceholderViewModel(Strings.NavEventLog)),
        ];
        _selectedView = Views[0];
        _currentPage = Views[0].Page;
        Tasks.Rows.CollectionChanged += OnTasksRowsChanged;
        _tasksCount = Tasks.Rows.Count;
        // The All-hosts sentinel always leads the rail; host entries follow it
        // (entry i+1 <-> _store.Hosts[i]) via ReconcileHosts.
        RailEntries.Add(_allHosts);
        SelectedRailEntry = _allHosts;
        store.Changed += OnStoreChanged;
        ReconcileHosts();
    }

    public IReadOnlyList<NavItemViewModel> Views { get; }
    public ObservableCollection<object> RailEntries { get; } = [];
    public SettingsViewModel Settings { get; }

    /// <summary>The Tasks page VM (Views[0].Page); exposed directly so the shell
    /// can push scope changes into it and mirror its row count for the nav badge.</summary>
    public TasksViewModel Tasks { get; }

    [ObservableProperty] private NavItemViewModel? _selectedView;
    [ObservableProperty] private object _currentPage;
    [ObservableProperty] private ScopeSelection _scope = ScopeSelection.AllHosts;
    [ObservableProperty] private bool _hasHosts;
    [ObservableProperty] private object? _selectedRailEntry;

    /// <summary>Mirrors <see cref="TasksViewModel.Rows"/>.Count; drives the Tasks nav item's inline count badge.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTasksCount))]
    private int _tasksCount;

    public bool HasTasksCount => TasksCount > 0;

    partial void OnSelectedRailEntryChanged(object? value) =>
        Scope = value is HostRailItemViewModel h ? new ScopeSelection(h.HostId) : ScopeSelection.AllHosts;

    // Design rule: selecting a host scopes every view. Tasks is the only real
    // (non-Placeholder) page today, so it is the only one wired here; later
    // views will get the same partial-method push when they graduate.
    partial void OnScopeChanged(ScopeSelection value) => Tasks.Scope = value;

    private void OnTasksRowsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        TasksCount = Tasks.Rows.Count;

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
        // insert position if reordering ever lands. Index 0 is always the
        // All-hosts sentinel, so host entry i lives at RailEntries[i + 1].
        var byId = RailEntries.OfType<HostRailItemViewModel>().ToDictionary(i => i.HostId);
        var seen = new HashSet<Guid>();
        for (var i = 0; i < _store.Hosts.Count; i++)
        {
            HostEntry entry = _store.Hosts[i];
            seen.Add(entry.Config.Id);
            if (!byId.TryGetValue(entry.Config.Id, out HostRailItemViewModel? item))
                RailEntries.Insert(Math.Min(i + 1, RailEntries.Count), new HostRailItemViewModel(entry, _clock));
            else
                item.Refresh();
        }
        for (var i = RailEntries.Count - 1; i >= 1; i--)
            if (RailEntries[i] is HostRailItemViewModel item && !seen.Contains(item.HostId))
            {
                // The scoped host vanished (e.g. removed) — fall back to All hosts
                // rather than leaving Scope pointed at a dead id.
                if (ReferenceEquals(SelectedRailEntry, item))
                    SelectedRailEntry = _allHosts;
                item.Dispose();
                RailEntries.RemoveAt(i);
            }
        var connected = _store.Hosts.Count(h => RailStateProjection.From(h.Status) == RailState.Connected);
        _allHosts.Update(connected, _store.Hosts.Count);
        HasHosts = _store.Hosts.Count > 0;
        Settings.Reconcile();
    }

    public void Dispose()
    {
        _store.Changed -= OnStoreChanged;
        Tasks.Rows.CollectionChanged -= OnTasksRowsChanged;
        Tasks.Dispose();
        foreach (HostRailItemViewModel item in RailEntries.OfType<HostRailItemViewModel>())
            item.Dispose();
    }
}
