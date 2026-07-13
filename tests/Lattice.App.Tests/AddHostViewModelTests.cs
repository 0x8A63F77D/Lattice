using Lattice.App.Localization;
using Lattice.App.ViewModels;
using Lattice.Core;
using Lattice.Tests;
using Xunit;

namespace Lattice.App.Tests;

public class AddHostViewModelTests
{
    private static (AddHostViewModel Vm, HostRegistry Registry) Make(Func<FakeGuiRpcClient>? factory = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}.json");
        var registry = new HostRegistry(new LatticeConfig(5, []), path);
        return (new AddHostViewModel(registry, factory ?? (() => new FakeGuiRpcClient())), registry);
    }

    [Fact]
    public void Add_is_disabled_until_address_is_present_and_port_valid()
    {
        var (vm, _) = Make();
        Assert.False(vm.AddCommand.CanExecute(null));

        vm.Address = "localhost";
        Assert.True(vm.AddCommand.CanExecute(null));

        vm.PortText = "junk";
        Assert.False(vm.AddCommand.CanExecute(null));
    }

    [Fact]
    public async Task Successful_test_adds_the_host_and_sets_succeeded()
    {
        var (vm, registry) = Make();
        vm.Name = "office-pc";
        vm.Address = "localhost";
        vm.Password = "pw";

        await vm.AddCommand.ExecuteAsync(null);

        Assert.True(vm.Succeeded);
        var host = Assert.Single(registry.Hosts);
        Assert.Equal("office-pc", host.Name);
        Assert.Equal(31416, host.Port);
    }

    [Fact]
    public async Task Failed_test_sets_error_and_adds_nothing()
    {
        var (vm, registry) = Make(() => new FakeGuiRpcClient
        {
            OnConnect = (_, _) => throw new IOException("Connection refused"),
        });
        vm.Address = "192.0.2.1";

        await vm.AddCommand.ExecuteAsync(null);

        Assert.False(vm.Succeeded);
        Assert.Contains("Connection refused", vm.ErrorText);
        Assert.Empty(registry.Hosts);
    }

    [Fact]
    public async Task Refused_password_error_never_echoes_the_password()
    {
        var (vm, registry) = Make(() => new FakeGuiRpcClient
        {
            OnAuthorize = _ => Task.FromResult(false),
        });
        vm.Address = "localhost";
        vm.Password = "sekrit-pw";

        await vm.AddCommand.ExecuteAsync(null);

        Assert.False(vm.Succeeded);
        Assert.NotNull(vm.ErrorText);
        Assert.DoesNotContain("sekrit-pw", vm.ErrorText);
        Assert.Empty(registry.Hosts);
    }

    [Fact]
    public async Task Persistence_failure_after_successful_test_lands_in_the_dialog()
    {
        // The config path is a directory, so AddHost's persist step throws.
        var path = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}.json");
        Directory.CreateDirectory(path);
        var registry = new HostRegistry(new LatticeConfig(5, []), path);
        var vm = new AddHostViewModel(registry, () => new FakeGuiRpcClient());
        vm.Address = "localhost";
        try
        {
            await vm.AddCommand.ExecuteAsync(null);

            Assert.False(vm.Succeeded);
            Assert.StartsWith(string.Format(Strings.AddHostSaveFailedFmt, ""), vm.ErrorText);
            Assert.Empty(registry.Hosts);
        }
        finally
        {
            Directory.Delete(path);
        }
    }

    [Fact]
    public async Task Add_times_out_rather_than_hanging()
    {
        var (vm, registry) = Make(() => new FakeGuiRpcClient
        {
            OnConnect = (_, _) => Task.Delay(Timeout.Infinite),
        });
        vm.Address = "localhost";
        vm.TestTimeout = TimeSpan.FromMilliseconds(50);

        await vm.AddCommand.ExecuteAsync(null);

        Assert.False(vm.Succeeded);
        Assert.Equal("Connection timed out.", vm.ErrorText);
        Assert.Empty(registry.Hosts);
    }

    private static string NewPath() => Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}.json");

    [Fact]
    public void Edit_mode_prefills_from_the_host_and_titles_for_editing()
    {
        var registry = new HostRegistry(new LatticeConfig(5, []), NewPath());
        var cfg = TestData.MakeHostConfig(name: "mini-01", address: "192.168.1.40");
        registry.AddHost(cfg);
        var vm = AddHostViewModel.ForEdit(registry, () => new FakeGuiRpcClient(), cfg, authError: false);

        Assert.Equal(HostDialogMode.Edit, vm.Mode);
        Assert.Equal("mini-01", vm.Name);
        Assert.Equal("192.168.1.40", vm.Address);
        Assert.Equal(Strings.EditHostDialogTitle, vm.DialogTitle);
        Assert.Equal(Strings.EditHostPrimaryButton, vm.PrimaryButtonText);
        Assert.True(vm.ShowTestButton);
    }

    [Fact]
    public async Task Edit_mode_save_updates_the_host_in_the_registry()
    {
        var registry = new HostRegistry(new LatticeConfig(5, []), NewPath());
        var cfg = TestData.MakeHostConfig(name: "mini-01");
        registry.AddHost(cfg);
        var vm = AddHostViewModel.ForEdit(registry, () => new FakeGuiRpcClient(), cfg, authError: false);
        vm.Name = "mini-01-renamed";

        await vm.AddCommand.ExecuteAsync(null);   // edit-save persists without a connection test

        Assert.True(vm.Succeeded);
        Assert.Equal("mini-01-renamed", registry.Hosts.Single(h => h.Id == cfg.Id).Name);
    }

    [Fact]
    public async Task Edit_mode_saves_local_changes_even_when_the_host_is_unreachable()
    {
        var registry = new HostRegistry(new LatticeConfig(5, []), NewPath());
        var cfg = TestData.MakeHostConfig(name: "mini-01");
        registry.AddHost(cfg);
        // Connection FAILS — Save must still persist (unlike Add, which needs a live host).
        var vm = AddHostViewModel.ForEdit(registry,
            () => new FakeGuiRpcClient { OnConnect = (_, _) => throw new IOException("refused") },
            cfg, authError: false);
        vm.Address = "192.168.1.99";

        await vm.AddCommand.ExecuteAsync(null);

        Assert.True(vm.Succeeded);
        Assert.Null(vm.ErrorText);
        Assert.Equal("192.168.1.99", registry.Hosts.Single(h => h.Id == cfg.Id).Address);
    }

    [Fact]
    public void Auth_failed_edit_opens_with_the_password_error_shown()
    {
        var registry = new HostRegistry(new LatticeConfig(5, []), NewPath());
        var cfg = TestData.MakeHostConfig(name: "mini-01");
        registry.AddHost(cfg);
        var vm = AddHostViewModel.ForEdit(registry, () => new FakeGuiRpcClient(), cfg, authError: true);

        Assert.True(vm.HasPasswordError);
        Assert.Equal(string.Format(Strings.EditHostPasswordError, "mini-01"), vm.PasswordErrorText);
    }

    [Fact]
    public async Task Test_connection_success_shows_result_inline_without_closing_the_dialog()
    {
        var registry = new HostRegistry(new LatticeConfig(5, []), NewPath());
        var cfg = TestData.MakeHostConfig(name: "mini-01");
        registry.AddHost(cfg);
        var vm = AddHostViewModel.ForEdit(registry, () => new FakeGuiRpcClient(), cfg, authError: false);

        await vm.TestConnectionCommand.ExecuteAsync(null);

        Assert.Equal(string.Format(Strings.SettingsTestConnectionSuccess, 8, 2, 0), vm.TestResultText);
        Assert.False(vm.Succeeded);
        Assert.Null(vm.ErrorText);
        Assert.Equal("mini-01", registry.Hosts.Single(h => h.Id == cfg.Id).Name);
    }

    [Fact]
    public async Task Test_connection_failure_shows_result_inline_without_closing_the_dialog()
    {
        var registry = new HostRegistry(new LatticeConfig(5, []), NewPath());
        var cfg = TestData.MakeHostConfig(name: "mini-01");
        registry.AddHost(cfg);
        var vm = AddHostViewModel.ForEdit(registry,
            () => new FakeGuiRpcClient { OnConnect = (_, _) => throw new IOException("Connection refused") },
            cfg, authError: false);

        await vm.TestConnectionCommand.ExecuteAsync(null);

        Assert.NotNull(vm.TestResultText);
        Assert.Contains("Connection refused", vm.TestResultText);
        Assert.False(vm.Succeeded);
        Assert.Null(vm.ErrorText);
        Assert.Equal("mini-01", registry.Hosts.Single(h => h.Id == cfg.Id).Name);
    }
}
