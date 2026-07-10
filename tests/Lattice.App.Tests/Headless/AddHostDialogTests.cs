using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using Lattice.App.Infrastructure;
using Lattice.App.Tests.Fakes;
using Lattice.App.ViewModels;
using Lattice.App.Views;
using Lattice.Boinc.GuiRpc;
using Lattice.Core;
using Lattice.Tests;
using Xunit;

namespace Lattice.App.Tests.Headless;

public class AddHostDialogTests
{
    private static (ShellWindow Window, ShellViewModel Shell, HostRegistry Registry) MakeShell(
        Func<IGuiRpcClient>? factory = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}.json");
        var registry = new HostRegistry(new LatticeConfig(5, []), path);
        Func<IGuiRpcClient> f = factory ?? (() => new FakeGuiRpcClient());
        // Manager never started: no sockets, no background threads in headless tests.
        var manager = new HostMonitorManager(registry, f, TimeProvider.System);
        var store = new HostStore(registry, manager, new ImmediateUiDispatcher());
        var shell = new ShellViewModel(registry, store, new ManualUiClock(), f);
        var window = new ShellWindow { DataContext = shell };
        return (window, shell, registry);
    }

    // Headless Show() does not run a full layout pass; a single measure/arrange
    // realizes the tree (precedent: ShellWindowTests.Layout).
    private static void Layout(Window window)
    {
        window.Measure(new Size(window.Width, window.Height));
        window.Arrange(new Rect(0, 0, window.Width, window.Height));
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Double_firing_add_host_opens_exactly_one_dialog()
    {
        var (window, shell, _) = MakeShell();
        window.Show();
        Layout(window);

        // The CTA and the rail's + button both raise AddHostRequested; a fast
        // double-fire must not stack a second dialog in the OverlayLayer.
        shell.RequestAddHostCommand.Execute(null);
        shell.RequestAddHostCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Single(window.GetVisualDescendants().OfType<FAContentDialog>());
        window.Close();
    }

    [AvaloniaFact]
    public async Task Failing_add_keeps_the_dialog_open_with_the_button_re_enabled()
    {
        var (window, shell, registry) = MakeShell(() => new FakeGuiRpcClient
        {
            OnConnect = (_, _) => throw new IOException("Connection refused"),
        });
        window.Show();
        Layout(window);

        shell.RequestAddHostCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var dialog = Assert.IsType<AddHostDialog>(
            Assert.Single(window.GetVisualDescendants().OfType<FAContentDialog>()));
        var vm = Assert.IsType<AddHostViewModel>(dialog.DataContext);
        vm.Address = "192.0.2.1";
        Dispatcher.UIThread.RunJobs();

        // Drive the REAL primary-button path: the template's PrimaryButton click
        // routes through FAContentDialog into our PrimaryButtonClick handler,
        // whose deferral must hold the dialog open when the add fails.
        var primary = window.GetVisualDescendants().OfType<Button>()
            .Single(b => b.Name == "PrimaryButton");
        Assert.True(primary.IsEnabled);
        primary.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        // The failure path never runs the 200 ms close delay (the deferral cancels
        // the close), so the outcome is: error surfaced and the button re-enabled.
        await HeadlessSync.WaitUntilAsync(() => vm.ErrorText is not null && dialog.IsPrimaryButtonEnabled);

        Assert.NotNull(vm.ErrorText);
        Assert.Contains("Connection refused", vm.ErrorText);
        Assert.False(vm.Succeeded);
        Assert.Empty(registry.Hosts);
        // Deferral cancelled the close: dialog still in the overlay, retry possible.
        Assert.Single(window.GetVisualDescendants().OfType<FAContentDialog>());
        Assert.True(vm.CanAddNow);
        Assert.True(dialog.IsPrimaryButtonEnabled);
        window.Close();
    }
}
