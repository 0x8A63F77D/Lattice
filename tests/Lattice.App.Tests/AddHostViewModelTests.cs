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
}
