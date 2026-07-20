using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lattice.App.Aggregation;
using Lattice.App.Infrastructure;
using Lattice.App.Localization;
using Lattice.Boinc.GuiRpc;
using Lattice.Core;
// ModeLane exists in BOTH the GuiRpc protocol enum and the GuiRpc-free F# policy
// DU (the App.Aggregation module rule keeps them separate); alias both so neither
// is ever named bare and the adapter's mapping point is explicit.
using AggModeLane = Lattice.App.Aggregation.ModeLane;
using GuiModeLane = Lattice.Boinc.GuiRpc.ModeLane;

namespace Lattice.App.ViewModels;

/// <summary>
/// One two-line host entry in the nav rail. Countdown text refreshes on the
/// shared clock tick; everything else refreshes when the store signals change.
/// Also the per-host run-mode surface (M3 PR H, DI-4): the rail context menu and
/// the scoped command-bar dropdown both drive this VM's <see cref="SetRunModeCommand"/>.
/// </summary>
public sealed partial class HostRailItemViewModel : ObservableObject, IDisposable
{
    private readonly HostEntry _entry;
    private readonly IUiClock _clock;
    private readonly HostControlService _control;

    public HostRailItemViewModel(HostEntry entry, IUiClock clock, HostControlService control)
    {
        _entry = entry;
        _clock = clock;
        _control = control;
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

    // --- M3 PR H run-mode surface (DI-4). "Snoozed until hh:mm" is derived from
    //     cc_status's CPU-lane mode_delay by RunModePolicy.temporaryUntil; the chip
    //     shows only while a temporary CPU override (a snooze) is live. ---

    /// <summary>"Snoozed until hh:mm" while a temporary CPU override is active, else empty.</summary>
    [ObservableProperty] private string _snoozedUntilText = "";

    /// <summary>Whether a temporary CPU override (snooze) is currently active — gates the
    /// chip's visibility and the "Resume computing" menu item.</summary>
    [ObservableProperty] private bool _isSnoozed;

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
        // DI-3: run-mode commands are enabled only while Connected; a state flip
        // must re-evaluate the menu items' CanExecute immediately.
        SetRunModeCommand.NotifyCanExecuteChanged();
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
#pragma warning disable CS8524 // No `_` arm on purpose: CS8509 (a new NAMED RailState left unhandled)
        // must stay a build error so this label mapping is revisited. CS8524 is the residual "unnamed
        // enum value" case — an out-of-range cast, unreachable for a well-formed RailState — and is
        // suppressed here; a `_` arm would silence CS8509 too and defeat the guard. Same pattern as
        // RailTierProjection. (The guarded Retrying arm is backed by an unguarded one, so all five
        // named values are covered.)
        StateText = State switch
        {
            RailState.Connected => string.Format(Strings.RailConnectedFmt, _entry.Snapshot?.Tasks.Count ?? 0),
            RailState.Connecting => Strings.RailConnecting,
            RailState.Retrying when _entry.Status.NextAttemptAt is { } next =>
                TimeText.RetryCountdown(next, _clock.Now, _entry.Status.Attempt),
            RailState.Retrying => Strings.RailRetrying,
            RailState.Unreachable => Strings.RailUnreachable,
            RailState.AuthFailed => Strings.RailAuthFailed,
        };
#pragma warning restore CS8524
        RefreshSnooze();
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

    // "Snoozed until" is an absolute wall-clock deadline, so it is derived from the
    // last snapshot only (never per clock tick — an absolute time does not drift):
    // snapshot.Timestamp (the monitor's TimeProvider instant, testable) + the CPU
    // lane's mode_delay. A disconnected host's stale snapshot never shows a snooze.
    private void RefreshSnooze()
    {
        HostSnapshot? snapshot = _entry.Snapshot;
        if (State != RailState.Connected || snapshot is null)
        {
            IsSnoozed = false;
            SnoozedUntilText = "";
            return;
        }
        // Convert the UTC snapshot instant to local so the "hh:mm" reads in the
        // user's wall-clock time; the delay is seconds remaining as of that poll.
        DateTimeOffset localNow = snapshot.Timestamp.ToLocalTime();
        var until = RunModePolicy.temporaryUntil(localNow, snapshot.CcStatus.TaskModeDelaySeconds);
        if (until is not null)
        {
            IsSnoozed = true;
            SnoozedUntilText = string.Format(
                Strings.RailSnoozedUntilFmt, until.Value.ToString("HH:mm", CultureInfo.InvariantCulture));
        }
        else
        {
            IsSnoozed = false;
            SnoozedUntilText = "";
        }
    }

    // DI-3: enabled only while the host is Connected. The token identifies which
    // menu item fired; the mapping token -> ModeIntent below is total.
    private bool CanRunMode(string? token) => IsConnected;

    /// <summary>
    /// The single entry point for every run-mode / snooze menu item (rail context
    /// menu and the scoped command-bar dropdown). <paramref name="token"/> selects
    /// the <see cref="ModeIntent"/>; <see cref="RunModePolicy.toWireArgs"/> pins the
    /// snooze (= temporary CPU Never) and un-snooze (= restore) semantics in one
    /// tested place; the adapter maps to the GuiRpc enums. Run modes are Instant
    /// (no confirmation, design DI-1); a failure surfaces on the row's transient
    /// action-result line (the same channel Test/Remove failures use).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunMode))]
    private async Task SetRunModeAsync(string? token)
    {
        if (token is null)
            return;
        ModeIntent intent = ParseIntent(token);
        Tuple<AggModeLane, RunModePolicy.WireMode, TimeSpan> args = RunModePolicy.toWireArgs(intent);
        ControlOpResult result = await _control.SetModeAsync(
            HostId, ToGuiLane(args.Item1), ToRunMode(args.Item2), args.Item3);
        // Success: the service nudged the monitor, so the snooze chip converges on
        // the next poll tick (~1 s). Clear any stale action result so it does not
        // linger over the fresh state.
        TestResultText = result.Outcome == ControlOpOutcome.Succeeded
            ? null
            : string.Format(Strings.RunModeFailedFmt, result.Error ?? "");
    }

    // Token -> ModeIntent. Total: an unknown token is a XAML wiring bug and throws
    // rather than silently no-opping (every token is exercised in RunModeControlTests).
    private static ModeIntent ParseIntent(string token) => token switch
    {
        "cpu:always" => ModeIntent.NewSetPermanent(AggModeLane.CpuLane, PermMode.ModeAlways),
        "cpu:auto" => ModeIntent.NewSetPermanent(AggModeLane.CpuLane, PermMode.ModeAuto),
        "cpu:never" => ModeIntent.NewSetPermanent(AggModeLane.CpuLane, PermMode.ModeNever),
        "gpu:always" => ModeIntent.NewSetPermanent(AggModeLane.GpuLane, PermMode.ModeAlways),
        "gpu:auto" => ModeIntent.NewSetPermanent(AggModeLane.GpuLane, PermMode.ModeAuto),
        "gpu:never" => ModeIntent.NewSetPermanent(AggModeLane.GpuLane, PermMode.ModeNever),
        "net:always" => ModeIntent.NewSetPermanent(AggModeLane.NetworkLane, PermMode.ModeAlways),
        "net:auto" => ModeIntent.NewSetPermanent(AggModeLane.NetworkLane, PermMode.ModeAuto),
        "net:never" => ModeIntent.NewSetPermanent(AggModeLane.NetworkLane, PermMode.ModeNever),
        "snooze:15" => ModeIntent.NewSnooze(TimeSpan.FromMinutes(15)),
        "snooze:60" => ModeIntent.NewSnooze(TimeSpan.FromHours(1)),
        "snooze:240" => ModeIntent.NewSnooze(TimeSpan.FromHours(4)),
        "resume" => ModeIntent.NewCancelTemporary(AggModeLane.CpuLane),
        _ => throw new ArgumentOutOfRangeException(nameof(token), token, "unknown run-mode menu token"),
    };

    // The App.Aggregation-to-GuiRpc adapter (the single mapping point the module
    // rule forces). F# nullary DU cases expose Is* discriminators; the trailing
    // throw is the total guard for an out-of-range value that a well-formed DU
    // never produces (the C#-over-F#-DU analogue of the CS8524 discipline).
    private static GuiModeLane ToGuiLane(AggModeLane lane) =>
        lane.IsCpuLane ? GuiModeLane.Cpu
        : lane.IsGpuLane ? GuiModeLane.Gpu
        : lane.IsNetworkLane ? GuiModeLane.Network
        : throw new ArgumentOutOfRangeException(nameof(lane));

    private static RunMode ToRunMode(RunModePolicy.WireMode mode) =>
        mode.IsWireAlways ? RunMode.Always
        : mode.IsWireAuto ? RunMode.Auto
        : mode.IsWireNever ? RunMode.Never
        : mode.IsWireRestore ? RunMode.Restore
        : throw new ArgumentOutOfRangeException(nameof(mode));

    public void Dispose() => _clock.Tick -= OnTick;
}
