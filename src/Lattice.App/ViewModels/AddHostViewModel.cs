using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lattice.App.Infrastructure;
using Lattice.App.Localization;
using Lattice.Boinc.GuiRpc;
using Lattice.Core;

namespace Lattice.App.ViewModels;

/// <summary>Which flow <see cref="AddHostViewModel"/> is driving: a new host or an existing one.</summary>
public enum HostDialogMode { Add, Edit }

/// <summary>
/// Add/Edit-host dialog. Add mode validates, test-connects, then registers; the
/// dialog stays open on failure (design 2d: failure renders an InfoBar in the
/// dialog). Edit mode (design 3b) prefills from an existing host, retitles for
/// editing, and saves locally without a connection test — mirroring the old
/// Settings card, which persisted rename/address/password changes to an
/// unreachable host. Edit mode also exposes an advisory Test-connection command
/// and an openable password-error state (auth-failed deep link).
/// </summary>
public sealed partial class AddHostViewModel : ObservableObject
{
    private readonly HostRegistry _registry;
    private readonly Func<IGuiRpcClient> _clientFactory;
    private readonly Guid _editId;       // Guid.Empty in Add mode

    public AddHostViewModel(HostRegistry registry, Func<IGuiRpcClient> clientFactory)
        : this(registry, clientFactory, Guid.Empty, HostDialogMode.Add)
    {
    }

    private AddHostViewModel(HostRegistry registry, Func<IGuiRpcClient> clientFactory, Guid editId, HostDialogMode mode)
    {
        _registry = registry;
        _clientFactory = clientFactory;
        _editId = editId;
        Mode = mode;
    }

    /// <summary>Edit an existing host: prefilled, retitled, Save→UpdateHost.</summary>
    public static AddHostViewModel ForEdit(HostRegistry registry, Func<IGuiRpcClient> clientFactory,
        HostConfig host, bool authError) =>
        new(registry, clientFactory, host.Id, HostDialogMode.Edit)
        {
            Name = host.Name,
            Address = host.Address,
            PortText = host.Port.ToString(),
            Password = host.Password,
            HasPasswordError = authError,
            PasswordErrorText = authError ? string.Format(Strings.EditHostPasswordError, host.DisplayName) : null,
        };

    /// <summary>Ceiling for the pre-add connection test — a dead host must not pin the dialog forever.</summary>
    internal TimeSpan TestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public HostDialogMode Mode { get; private init; } = HostDialogMode.Add;

    public string DialogTitle => Mode == HostDialogMode.Edit ? Strings.EditHostDialogTitle : Strings.AddHostDialogTitle;
    public string PrimaryButtonText => Mode == HostDialogMode.Edit ? Strings.EditHostPrimaryButton : Strings.AddHostPrimaryButton;
    public bool ShowTestButton => Mode == HostDialogMode.Edit;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private string _name = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private string _address = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private string _portText = "31416";

    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string? _errorText;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasPasswordError;
    [ObservableProperty] private string? _passwordErrorText;
    [ObservableProperty] private string? _testResultText;

    /// <summary>
    /// Mirror of <see cref="CanAdd"/> as a plain bindable bool. FAContentDialog's
    /// IsPrimaryButtonEnabled is a StyledProperty; it cannot bind to a command's
    /// CanExecute, so the primary button reads this instead. Kept in lockstep with
    /// CanAdd wherever AddCommand's CanExecute is re-evaluated.
    /// </summary>
    [ObservableProperty] private bool _canAddNow;

    public bool Succeeded { get; private set; }

    private bool CanAdd() =>
        !string.IsNullOrWhiteSpace(Address)
        && int.TryParse(PortText, out var port) && port is >= 1 and <= 65535
        && !IsBusy;

    partial void OnAddressChanged(string value) => CanAddNow = CanAdd();
    partial void OnPortTextChanged(string value) => CanAddNow = CanAdd();
    partial void OnIsBusyChanged(bool value) => CanAddNow = CanAdd();

    // The danger border lifts once the user starts correcting the password.
    partial void OnPasswordChanged(string value)
    {
        HasPasswordError = false;
        PasswordErrorText = null;
    }

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private async Task AddAsync()
    {
        IsBusy = true;
        try
        {
            HostConfig candidate = Candidate();   // CanAdd already gated address/port
            if (Mode == HostDialogMode.Edit)
            {
                // Save is local-only: never blocked by an unreachable host.
                if (Register(candidate, edit: true) is { } error) ErrorText = error;
                else { Succeeded = true; ErrorText = null; }
                return;
            }
            using var cts = new CancellationTokenSource(TestTimeout);
            TestConnectionResult result =
                await HostMonitorManager.TestConnectionAsync(candidate, _clientFactory, cts.Token);
            if (result.Success)
            {
                if (Register(candidate, edit: false) is { } error) ErrorText = error;
                else { Succeeded = true; ErrorText = null; }
            }
            else ErrorText = result.Error;
        }
        catch (OperationCanceledException) { ErrorText = Strings.AddHostTimeoutError; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        var candidate = new HostConfig(_editId == Guid.Empty ? Guid.NewGuid() : _editId,
            Name.Trim(), Address.Trim(), int.TryParse(PortText, out var p) ? p : 0, Password);
        TestResultText = Strings.SettingsTestConnectionBusy;
        try
        {
            using var cts = new CancellationTokenSource(TestTimeout);
            var r = await HostMonitorManager.TestConnectionAsync(candidate, _clientFactory, cts.Token);
            TestResultText = r.Success
                ? string.Format(Strings.SettingsTestConnectionSuccess, r.Version!.Major, r.Version.Minor, r.Version.Release)
                : r.Error;
        }
        catch (OperationCanceledException) { TestResultText = Strings.SettingsTestConnectionTimeout; }
    }

    /// <summary>Field values as a HostConfig; keeps the edited host's id (new id in Add).</summary>
    private HostConfig Candidate() =>
        new(_editId == Guid.Empty ? Guid.NewGuid() : _editId,
            Name.Trim(), Address.Trim(), int.Parse(PortText), Password);

    private string? Register(HostConfig cfg, bool edit) =>
        RegistryGuard.TryMutate(() => { if (edit) _registry.UpdateHost(cfg); else _registry.AddHost(cfg); }) is { } err
            ? string.Format(edit ? Strings.EditHostSaveFailedFmt : Strings.AddHostSaveFailedFmt, err)
            : null;
}
