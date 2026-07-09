using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lattice.App.Infrastructure;
using Lattice.Boinc.GuiRpc;
using Lattice.Core;

namespace Lattice.App.ViewModels;

/// <summary>One host's Settings expander: editable fields + test/save/remove.</summary>
public sealed partial class HostSettingsItemViewModel : ObservableObject
{
    private readonly HostEntry _entry;
    private readonly HostRegistry _registry;
    private readonly Func<IGuiRpcClient> _clientFactory;

    public HostSettingsItemViewModel(HostEntry entry, HostRegistry registry, Func<IGuiRpcClient> clientFactory)
    {
        _entry = entry;
        _registry = registry;
        _clientFactory = clientFactory;
        _name = entry.Config.Name;
        _address = entry.Config.Address;
        _portText = entry.Config.Port.ToString();
        _password = entry.Config.Password;
        RefreshFromEntry();
    }

    public Guid HostId => _entry.Config.Id;

    /// <summary>Ceiling for a connection test — a dead host must not pin "Testing…" forever.</summary>
    internal TimeSpan TestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _name;
    [ObservableProperty] private string _address;
    [ObservableProperty] private string _portText;
    [ObservableProperty] private string _password;
    [ObservableProperty] private string? _validationError;
    [ObservableProperty] private string? _testResultText;
    [ObservableProperty] private bool _hasAuthError;
    [ObservableProperty] private string? _authErrorText;

    /// <summary>Raised by RemoveCommand; the view confirms via dialog, then calls SettingsViewModel.Remove.</summary>
    public event EventHandler? RemoveRequested;

    public void RefreshFromEntry()
    {
        DisplayName = _entry.Config.DisplayName;
        var stateWord = RailStateProjection.From(_entry.Status) switch
        {
            RailState.Connected => "Connected",
            RailState.Connecting => "Connecting…",
            RailState.Retrying => "Retrying",
            RailState.Unreachable => "Unreachable",
            RailState.AuthFailed => "Wrong password",
            _ => "",
        };
        StatusText = $"{_entry.Config.Address}:{_entry.Config.Port} · {stateWord}";
        HasAuthError = _entry.Status.State == HostConnectionState.AuthFailed;
        AuthErrorText = HasAuthError
            ? $"The host refused this password. Check the gui_rpc_auth.cfg on {_entry.Config.DisplayName}."
            : null;
    }

    private HostConfig? BuildCandidate()
    {
        if (string.IsNullOrWhiteSpace(Address))
        {
            ValidationError = "Address is required.";
            return null;
        }
        if (!int.TryParse(PortText, out var port) || port is < 1 or > 65535)
        {
            ValidationError = "Port must be a number between 1 and 65535.";
            return null;
        }
        ValidationError = null;
        return _entry.Config with { Name = (Name ?? "").Trim(), Address = Address.Trim(), Port = port, Password = Password };
    }

    [RelayCommand]
    private void Save()
    {
        if (BuildCandidate() is { } candidate)
            _registry.UpdateHost(candidate);
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        if (BuildCandidate() is not { } candidate)
        {
            TestResultText = null;
            return;
        }
        TestResultText = "Testing…";
        try
        {
            using var cts = new CancellationTokenSource(TestTimeout);
            TestConnectionResult result =
                await HostMonitorManager.TestConnectionAsync(candidate, _clientFactory, cts.Token);
            TestResultText = result.Success
                ? $"Connected — BOINC {result.Version!.Major}.{result.Version.Minor}.{result.Version.Release}"
                : result.Error;
        }
        catch (OperationCanceledException)
        {
            TestResultText = "Connection timed out.";
        }
    }

    [RelayCommand]
    private void Remove() => RemoveRequested?.Invoke(this, EventArgs.Empty);
}
