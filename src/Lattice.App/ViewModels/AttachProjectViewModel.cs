using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lattice.App.Infrastructure;
using Lattice.App.Localization;
using Lattice.Core;

namespace Lattice.App.ViewModels;

/// <summary>One selectable target host in the attach dialog's picker (DI-3:
/// Connected hosts only). Display text is the host's configured name.</summary>
public sealed record AttachHostOption(Guid Id, string DisplayName)
{
    public override string ToString() => DisplayName;
}

/// <summary>
/// The attach-flow seam the view model drives — exactly
/// <see cref="AttachFlowRunner.RunAsync"/>'s shape. Production passes the real
/// runner's method group; headless tests pass a scripted lambda (design 2.5 /
/// the runner + lane are proven Core-side in #131).
/// </summary>
public delegate Task<AttachFlowResult> AttachFlowRun(
    Guid hostId, AttachMachine.AttachRequest request,
    IProgress<AttachMachine.Stage>? progress, CancellationToken ct);

/// <summary>
/// Drives the project-attach dialog (M3 PR I): a project URL + credentials
/// (email &amp; password | account key, matching <see cref="AttachMachine.Credentials"/>)
/// against a picked Connected host, rendering the flow's phase reports, its
/// verbatim failure text (display-only, never branched on), and — on the
/// daemon accepting the attach — closing so the normal read path shows reality.
/// </summary>
/// <remarks>
/// The view model owns NO flow logic: poll cadence, timeout and retry all live
/// in <see cref="AttachFlowRunner"/> / <see cref="AttachMachine"/>. It only
/// collects fields, calls the seam once, and renders the terminal result.
/// Success wording is "attached", never "verified": the daemon does not check
/// the authenticator at attach time (design 2.3) — a bad account key surfaces
/// later in the event log. The password lives only as a bound field for the
/// flow's duration; it is passed straight into the request's credentials and
/// never logged, persisted, or echoed into failure text.
/// </remarks>
public sealed partial class AttachProjectViewModel : ObservableObject
{
    private readonly AttachFlowRun _run;
    private readonly IUiDispatcher _ui;
    private CancellationTokenSource? _cts;

    public AttachProjectViewModel(
        AttachFlowRun run, IReadOnlyList<AttachHostOption> hosts, Guid? lockedHostId, IUiDispatcher ui)
    {
        _run = run;
        _ui = ui;
        Hosts = hosts;
        // A scoped single host locks the picker to itself (DI-3); All-hosts scope
        // leaves it unlocked and unselected, so the user must pick before submit.
        if (lockedHostId is { } id)
        {
            IsHostPickerLocked = true;
            _selectedHost = hosts.FirstOrDefault(h => h.Id == id);
        }
        else if (hosts.Count == 1)
        {
            // All-hosts scope with a single connectable host: pre-select it (still
            // an open picker) so the sole option is not left unhelpfully unpicked.
            _selectedHost = hosts[0];
        }
    }

    /// <summary>Connected hosts the project can be attached on (DI-3).</summary>
    public IReadOnlyList<AttachHostOption> Hosts { get; }

    /// <summary>True when a single host was scoped: the picker shows only it and is disabled.</summary>
    public bool IsHostPickerLocked { get; }

    public string DialogTitle => Strings.AttachDialogTitle;
    public string PrimaryButtonText => Strings.AttachPrimaryButton;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AttachCommand))]
    private AttachHostOption? _selectedHost;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AttachCommand))]
    private string _projectUrl = "";

    /// <summary>false = email &amp; password (drives the lookup leg); true = raw account key (skips to attach).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AttachCommand))]
    private bool _useAccountKey;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AttachCommand))]
    private string _email = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AttachCommand))]
    private string _password = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AttachCommand))]
    private string _accountKey = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AttachCommand))]
    [NotifyPropertyChangedFor(nameof(CanAddNow))]
    private bool _isBusy;

    /// <summary>Whether the progress area (spinner + <see cref="ProgressText"/>) is shown.</summary>
    [ObservableProperty] private bool _isInProgress;
    [ObservableProperty] private string _progressText = "";

    /// <summary>The failure area: verbatim daemon/project text (display-only).</summary>
    [ObservableProperty] private bool _hasFailure;
    [ObservableProperty] private string _failureText = "";

    /// <summary>Mirror of the command's CanExecute for FAContentDialog's
    /// IsPrimaryButtonEnabled (a StyledProperty that cannot bind a command's
    /// CanExecute — precedent AddHostViewModel.CanAddNow).</summary>
    public bool CanAddNow => CanSubmit();

    /// <summary>The daemon's attach messages on success (greeting text); kept for
    /// the record even though a successful attach closes the dialog.</summary>
    public IReadOnlyList<string> SuccessMessages { get; private set; } = [];

    /// <summary>The last terminal outcome, for tests/diagnostics.</summary>
    public AttachFlowOutcome? LastOutcome { get; private set; }

    /// <summary>Raised when the attach succeeded and the dialog should close.</summary>
    public event EventHandler? CloseRequested;

    private bool CanSubmit() =>
        !IsBusy
        && SelectedHost is not null
        && !string.IsNullOrWhiteSpace(ProjectUrl)
        && CredentialsFilled();

    private bool CredentialsFilled() =>
        UseAccountKey
            ? !string.IsNullOrWhiteSpace(AccountKey)
            : !string.IsNullOrWhiteSpace(Email) && Password.Length > 0;

    // Keep the FA primary-button mirror honest whenever a gating field changes.
    partial void OnSelectedHostChanged(AttachHostOption? value) => OnPropertyChanged(nameof(CanAddNow));
    partial void OnProjectUrlChanged(string value) => OnPropertyChanged(nameof(CanAddNow));
    partial void OnUseAccountKeyChanged(bool value) => OnPropertyChanged(nameof(CanAddNow));
    partial void OnEmailChanged(string value) => OnPropertyChanged(nameof(CanAddNow));
    partial void OnPasswordChanged(string value) => OnPropertyChanged(nameof(CanAddNow));
    partial void OnAccountKeyChanged(string value) => OnPropertyChanged(nameof(CanAddNow));

    [RelayCommand(CanExecute = nameof(CanSubmit), AllowConcurrentExecutions = false)]
    private async Task AttachAsync()
    {
        if (SelectedHost is not { } host)
            return;

        AttachMachine.AttachRequest request = BuildRequest();
        // Dispose the prior attempt's source (it has fully completed — IsBusy /
        // AllowConcurrentExecutions gate overlap), never the running one, so
        // there is no dispose-during-Cancel reentrancy.
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        IsBusy = true;
        HasFailure = false;
        FailureText = "";
        IsInProgress = true;
        ProgressText = Strings.AttachStarting;
        try
        {
            // The seam reports Stage from the RPC thread; StageProgress marshals
            // each report onto the UI dispatcher before touching bound state.
            AttachFlowResult result =
                await _run(host.Id, request, new StageProgress(this), _cts.Token);
            ApplyResult(result);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // Builds the machine's request from the current fields. The project name is
    // left empty (display-only; the daemon accepts empty — design 2.3). Email is
    // trimmed but NOT lowercased: the GuiRpc layer lowercases before hashing
    // (PR B), and lowercasing here would diverge the displayed value from intent.
    private AttachMachine.AttachRequest BuildRequest()
    {
        AttachMachine.Credentials credentials = UseAccountKey
            ? AttachMachine.Credentials.NewAuthenticatorKey(AccountKey.Trim())
            : AttachMachine.Credentials.NewEmailPassword(Email.Trim(), Password);
        return new AttachMachine.AttachRequest(ProjectUrl.Trim(), "", credentials);
    }

    /// <summary>How a terminal <see cref="AttachFlowOutcome"/> presents in the dialog.</summary>
    private enum ResultPresentation { Success, Silent, Failure }

    private void ApplyResult(AttachFlowResult result)
    {
        LastOutcome = result.Outcome;
        IsInProgress = false;
        // Map every named outcome explicitly (no catch-all on the domain enum, per
        // the C# enum-switch canon): CS8524 is the residual unnamed-value case;
        // CS8509 stays live, so a NEW AttachFlowOutcome forces a decision here
        // rather than silently collapsing into the failure UI.
#pragma warning disable CS8524
        var presentation = result.Outcome switch
        {
            AttachFlowOutcome.Attached => ResultPresentation.Success,
            AttachFlowOutcome.Canceled => ResultPresentation.Silent,
            AttachFlowOutcome.LookupFailed => ResultPresentation.Failure,
            AttachFlowOutcome.AttachFailed => ResultPresentation.Failure,
            AttachFlowOutcome.TimedOut => ResultPresentation.Failure,
            AttachFlowOutcome.Faulted => ResultPresentation.Failure,
        };
#pragma warning restore CS8524
        switch (presentation)
        {
            case ResultPresentation.Success:
                SuccessMessages = result.Messages;
                CloseRequested?.Invoke(this, EventArgs.Empty);
                break;
            case ResultPresentation.Silent:
                // The user closed the dialog; nothing to surface (the dialog is
                // already tearing down).
                break;
            case ResultPresentation.Failure:
                // Verbatim daemon/project text: the daemon's own <message> lines
                // when present (e.g. "Already attached to project"), else the
                // flow's display error. Never reworded, never branched on.
                HasFailure = true;
                FailureText = result.Messages.Count > 0
                    ? string.Join("\n", result.Messages)
                    : result.Error ?? Strings.AttachFailedGeneric;
                break;
        }
    }

    private void OnStage(AttachMachine.Stage stage)
    {
        IsInProgress = true;
        ProgressText = stage.IsLookupStage ? Strings.AttachProgressLookup : Strings.AttachProgressAttach;
    }

    /// <summary>Aborts a running flow via its token (dialog close/cancel), then
    /// disposes it. Idempotent and safe when no flow is running. Cancel runs
    /// first and fully returns before Dispose, so a synchronous cancellation
    /// continuation never disposes the source mid-Cancel.</summary>
    public void Cancel()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    // Marshals the runner's stage reports (raised on the RPC thread) onto the UI
    // dispatcher before mutating bound state — the ViewModel's only cross-thread seam.
    private sealed class StageProgress(AttachProjectViewModel owner) : IProgress<AttachMachine.Stage>
    {
        public void Report(AttachMachine.Stage stage) => owner._ui.Post(() => owner.OnStage(stage));
    }
}
