using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lattice.App.Aggregation;
using Lattice.App.Infrastructure;
using Lattice.App.Localization;
using Lattice.Core;

namespace Lattice.App.ViewModels;

/// <summary>
/// The Event-log view: folds per-host message batches (HostStore.MessagesReceived)
/// through the pure MessageLog model — identity-keyed set ingest, so reconnect
/// replays dedup here and nothing upstream needs a replace marker — then
/// filters and reconciles into the grid. Unlike the snapshot views, rows
/// rebuild on message arrival and scope/filter changes, NOT on every store
/// Changed (which only prunes/updates counts here).
/// </summary>
public sealed partial class EventLogViewModel : ObservableObject, IDisposable
{
    /// <summary>Design 2c: retain the last 5,000 messages per host.</summary>
    internal const int RetentionPerHost = 5000;

    private readonly HostStore _store;
    private MessageLog<EventLogRowViewModel> _log = MessageLog.empty<EventLogRowViewModel>(RetentionPerHost);
    private ScopeSelection _scope = ScopeSelection.AllHosts;

    public EventLogViewModel(HostStore store)
    {
        _store = store;
        store.MessagesReceived += OnMessagesReceived;
        store.Changed += OnStoreChanged;
        Rebuild();
    }

    public ObservableCollection<RowHolder<MessageKey, EventLogRowViewModel>> Rows { get; } = [];

    public ScopeSelection Scope
    {
        get => _scope;
        set
        {
            if (_scope.Equals(value)) return;
            _scope = value;
            Rebuild();
        }
    }

    [ObservableProperty] private bool _isAllHostsScope = true;
    [ObservableProperty] private bool _showInfo = true;
    [ObservableProperty] private bool _showWarning = true;
    [ObservableProperty] private bool _showError = true;
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private bool _isFollowing = true;
    [ObservableProperty] private string _countsText = "";
    [ObservableProperty] private bool _isEmpty;

    /// <summary>Unread warning+error count for the nav InfoBadge. Counts only
    /// while the view is not active; activation clears it.</summary>
    [ObservableProperty] private int _unreadCount;

    /// <summary>Set by ShellViewModel when this page becomes / stops being CurrentPage.</summary>
    public bool IsViewActive
    {
        get => _isViewActive;
        set
        {
            _isViewActive = value;
            if (value) UnreadCount = 0;
        }
    }
    private bool _isViewActive;

    partial void OnShowInfoChanged(bool value) => Rebuild();
    partial void OnShowWarningChanged(bool value) => Rebuild();
    partial void OnShowErrorChanged(bool value) => Rebuild();
    partial void OnFilterTextChanged(string value) => Rebuild();

    [RelayCommand]
    private void ResumeFollowing() => IsFollowing = true;

    private void OnMessagesReceived(object? sender, MessagesAddedEventArgs e)
    {
        var host = _store.Hosts.FirstOrDefault(h => h.Config.Id == e.HostId);
        var hostName = host?.Config.DisplayName ?? "";
        var batch = e.Messages
            .Select(m => new LogEntry<EventLogRowViewModel>(
                EventLogRowViewModel.KeyOf(m, e.HostId),
                EventLogRowViewModel.From(m, e.HostId, hostName)))
            .ToArray();

        // F# tuple return: Item1 = new log, Item2 = the actually-new entries.
        var result = MessageLog.ingest(e.HostId, batch, _log);
        _log = result.Item1;
        var added = result.Item2;
        if (added.Length == 0) return;

        if (!IsViewActive)
            UnreadCount += added.Count(a => a.Message.Priority is EventLogPriority.Warning or EventLogPriority.Error);
        Rebuild();
    }

    private void OnStoreChanged(object? sender, EventArgs e)
    {
        // Host set changes only: prune removed hosts, refresh counts. Message
        // arrival is the row-driving event, not store Changed.
        var live = new HashSet<Guid>(_store.Hosts.Select(h => h.Config.Id));
        var pruned = MessageLog.prune(live, _log);
        if (!pruned.Equals(_log))
        {
            _log = pruned;
            Rebuild();
        }
        else
        {
            UpdateCounts(Rows.Count);
        }
    }

    private bool Matches(EventLogRowViewModel row)
    {
        var priorityOn = row.Priority switch
        {
            EventLogPriority.Info => ShowInfo,
            EventLogPriority.Warning => ShowWarning,
            EventLogPriority.Error => ShowError,
            _ => true,
        };
        return priorityOn
            && (FilterText.Length == 0
                || row.Body.Contains(FilterText, StringComparison.OrdinalIgnoreCase)
                || row.Project.Contains(FilterText, StringComparison.OrdinalIgnoreCase));
    }

    private void Rebuild()
    {
        IsAllHostsScope = Scope.IsAllHosts;

        var target = MessageLog.merged(_log)
            .Where(e => Scope.IsAllHosts || e.Key.HostId == Scope.HostId)
            .Where(e => Matches(e.Message))
            .Select(e => (e.Key, e.Message))
            .ToArray();

        var existing = Rows.Select(h => (h.Key, h.Data)).ToArray();
        CollectionReconciler.Apply(Rows, Reconcile.diff(existing, target),
            (key, row) => new EventLogRow(key, row));

        UpdateCounts(target.Length);
        IsEmpty = target.Length == 0;
    }

    private void UpdateCounts(int visible)
    {
        var reachable = _store.Hosts.Count(
            h => RailStateProjection.From(h.Status) == RailState.Connected);
        CountsText = string.Format(Strings.EventLogCountsFmt, visible, reachable);
    }

    public void Dispose()
    {
        _store.MessagesReceived -= OnMessagesReceived;
        _store.Changed -= OnStoreChanged;
    }
}
