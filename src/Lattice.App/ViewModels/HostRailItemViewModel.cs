using CommunityToolkit.Mvvm.ComponentModel;
using Lattice.App.Infrastructure;
using Lattice.App.Localization;

namespace Lattice.App.ViewModels;

/// <summary>
/// One two-line host entry in the nav rail. Countdown text refreshes on the
/// shared clock tick; everything else refreshes when the store signals change.
/// </summary>
public sealed partial class HostRailItemViewModel : ObservableObject, IDisposable
{
    private readonly HostEntry _entry;
    private readonly IUiClock _clock;

    public HostRailItemViewModel(HostEntry entry, IUiClock clock)
    {
        _entry = entry;
        _clock = clock;
        Refresh();
        _clock.Tick += OnTick;
    }

    public Guid HostId => _entry.Config.Id;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private RailState _state;
    [ObservableProperty] private string _stateText = "";
    [ObservableProperty] private string? _tooltip;

    // The row's single generic action-result channel: carries the Test-connection
    // result AND a Remove write-failure error (see plan Task 11). When set it
    // overrides the live StateText in the subtext line; the next store Refresh()
    // clears it so the row reverts to live state.
    [ObservableProperty] private string? _testResultText;

    /// <summary>Subtext line: the transient action result when present, else live state.</summary>
    public string SubtextDisplay => TestResultText ?? StateText;

    partial void OnTestResultTextChanged(string? value)
    {
        OnPropertyChanged(nameof(SubtextDisplay));
        // Fold the action result into the tooltip so the compact rail surfaces it too.
        Tooltip = value is { Length: > 0 } ? $"{Name} — {value}" : BuildTooltip();
    }

    // XAML binds five stacked, statically-typed PathIcons to these bools so the
    // VM stays Avalonia-free and the state brushes stay DynamicResource-live.
    public bool IsConnected => State == RailState.Connected;
    public bool IsConnecting => State == RailState.Connecting;
    public bool IsRetrying => State == RailState.Retrying;
    public bool IsUnreachable => State == RailState.Unreachable;
    public bool IsAuthFailed => State == RailState.AuthFailed;

    partial void OnStateChanged(RailState value)
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsConnecting));
        OnPropertyChanged(nameof(IsRetrying));
        OnPropertyChanged(nameof(IsUnreachable));
        OnPropertyChanged(nameof(IsAuthFailed));
    }

    /// <summary>Store-driven full refresh: re-derives live state AND drops any transient
    /// action result (Test / Remove-failure) carried on the previous cycle, reverting the
    /// subtext to live state. Call only from a store-reconcile path, never from the clock
    /// tick (see <see cref="RefreshLiveState"/>).</summary>
    public void Refresh()
    {
        // A fresh store poll reverts the subtext to live state: drop any transient
        // action result (Test / Remove-failure) carried on the previous cycle.
        TestResultText = null;
        RefreshLiveState();
    }

    /// <summary>Re-derives Name/State/StateText/Tooltip from the live entry without touching
    /// TestResultText, so a Test-connection result or Remove write-failure showing on a
    /// Retrying row survives clock ticks; only a store-driven <see cref="Refresh"/> clears it.</summary>
    private void RefreshLiveState()
    {
        Name = _entry.Config.DisplayName;
        State = RailStateProjection.From(_entry.Status);
        StateText = State switch
        {
            RailState.Connected => string.Format(Strings.RailConnectedFmt, _entry.Snapshot?.Tasks.Count ?? 0),
            RailState.Connecting => Strings.RailConnecting,
            RailState.Retrying when _entry.Status.NextAttemptAt is { } next =>
                TimeText.RetryCountdown(next, _clock.Now, _entry.Status.Attempt),
            RailState.Retrying => Strings.RailRetrying,
            RailState.Unreachable => Strings.RailUnreachable,
            RailState.AuthFailed => Strings.RailAuthFailed,
            _ => "",
        };
        // Tooltip priority mirrors SubtextDisplay (kept in sync by OnTestResultTextChanged):
        // a transient action result wins over live state, so only rebuild the live tooltip
        // when no transient result is currently showing.
        if (TestResultText is not { Length: > 0 })
            Tooltip = BuildTooltip();
        OnPropertyChanged(nameof(SubtextDisplay));
    }

    // The compact rail shows the state icon only, so the tooltip must identify the
    // host in every state (design §Responsive: name/subtext/countdown live here);
    // error states append the underlying error.
    private string BuildTooltip() =>
        (State is RailState.Retrying or RailState.Unreachable)
        && _entry.Status.LastError is { Length: > 0 } error
            ? $"{Name} — {StateText}\n{error}"
            : $"{Name} — {StateText}";

    private void OnTick(object? sender, EventArgs e)
    {
        if (State == RailState.Retrying)
            RefreshLiveState();
    }

    public void Dispose() => _clock.Tick -= OnTick;
}
