using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lattice.Boinc.GuiRpc;
using Lattice.Core;

namespace Lattice.App.ViewModels;

/// <summary>
/// Add-host dialog: validates, test-connects, then registers. The dialog stays
/// open on failure (design 2d: failure renders an InfoBar in the dialog).
/// </summary>
public sealed partial class AddHostViewModel : ObservableObject
{
    private readonly HostRegistry _registry;
    private readonly Func<IGuiRpcClient> _clientFactory;

    public AddHostViewModel(HostRegistry registry, Func<IGuiRpcClient> clientFactory)
    {
        _registry = registry;
        _clientFactory = clientFactory;
    }

    /// <summary>Ceiling for the pre-add connection test — a dead host must not pin the dialog forever.</summary>
    internal TimeSpan TestTimeout { get; set; } = TimeSpan.FromSeconds(10);

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

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private async Task AddAsync()
    {
        IsBusy = true;
        try
        {
            var candidate = new HostConfig(
                Guid.NewGuid(), Name.Trim(), Address.Trim(), int.Parse(PortText), Password);
            using var cts = new CancellationTokenSource(TestTimeout);
            TestConnectionResult result =
                await HostMonitorManager.TestConnectionAsync(candidate, _clientFactory, cts.Token);
            if (result.Success)
            {
                // AddHost persists the host list to disk synchronously; a write
                // failure must land in the dialog's InfoBar, not escape the
                // async void PrimaryButtonClick handler and down the app.
                try
                {
                    _registry.AddHost(candidate);
                    Succeeded = true;
                    ErrorText = null;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    ErrorText = $"Connected, but saving the host list failed: {ex.Message}";
                }
            }
            else
            {
                ErrorText = result.Error;
            }
        }
        catch (OperationCanceledException)
        {
            ErrorText = "Connection timed out.";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
