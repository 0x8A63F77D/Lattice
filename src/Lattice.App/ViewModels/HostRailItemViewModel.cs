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

    public void Refresh()
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
        // The compact rail shows the state icon only, so the tooltip must
        // identify the host in every state (design §Responsive: name/subtext/
        // countdown live here); error states append the underlying error.
        Tooltip = (State is RailState.Retrying or RailState.Unreachable)
                  && _entry.Status.LastError is { Length: > 0 } error
            ? $"{Name} — {StateText}\n{error}"
            : $"{Name} — {StateText}";
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (State == RailState.Retrying)
            Refresh();
    }

    public void Dispose() => _clock.Tick -= OnTick;
}
