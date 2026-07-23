using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using Lattice.App.Localization;
using Lattice.App.ViewModels;
using Lattice.App.Views;
using Xunit;

namespace Lattice.App.Tests.Headless;

/// <summary>
/// M3 PR G view-layer gates for the Projects control surface: the command-bar
/// buttons' commands + tooltips, the DI-1(c) misclick separation of Detach, the
/// row context menu's exact item sequence (the raw-Separator landmine, PR #135),
/// the failure InfoBar's zero-layout-when-closed geometry, and the newest-view-
/// wins confirmation-handler wiring.
/// </summary>
public class ProjectsViewControlTests
{
    private static (HostGraphFixture Fx, Window Window, ProjectsView View, ProjectsViewModel Vm) MakeView()
    {
        var fx = new HostGraphFixture();
        var vm = new ProjectsViewModel(fx.Store, fx.Clock, fx.Control);
        var view = new ProjectsView { DataContext = vm };
        var window = fx.Host(view);
        return (fx, window, view, vm);
    }

    private static Button Named(ProjectsView view, string name) =>
        view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == name);

    [AvaloniaFact]
    public void Buttons_bind_commands_and_show_tooltips_while_disabled()
    {
        var (fx, window, view, vm) = MakeView();
        window.Show();
        fx.Layout();

        foreach (var name in new[] { "UpdateButton", "SuspendButton", "ResumeButton", "DetachButton" })
        {
            var button = Named(view, name);
            Assert.False(button.IsEffectivelyEnabled); // no selection → DI-3 disables
            Assert.True(ToolTip.GetShowOnDisabled(button));
        }

        vm.ControlDisabledReason = Strings.ControlHostNotConnected;
        fx.Layout();
        Assert.Equal(Strings.ControlHostNotConnected, ToolTip.GetTip(Named(view, "DetachButton")));

        fx.Dispose();
    }

    [AvaloniaFact]
    public void Detach_is_separated_from_the_reversible_ops()
    {
        var (fx, window, view, _) = MakeView();
        window.Show();
        fx.Layout();

        // DI-1(c): a Separator must sit between the reversible ops and Detach.
        var panel = (StackPanel)Named(view, "DetachButton").Parent!;
        var children = panel.Children.ToList();
        int resume = children.IndexOf(Named(view, "ResumeButton"));
        int detach = children.IndexOf(Named(view, "DetachButton"));
        Assert.True(resume >= 0 && detach > resume);
        Assert.Contains(children.Skip(resume + 1).Take(detach - resume - 1), c => c is Separator);

        fx.Dispose();
    }

    [AvaloniaFact]
    public void Row_context_menu_lists_the_four_ops_with_detach_separated()
    {
        var (fx, window, view, _) = MakeView();
        window.Show();
        fx.Layout();

        var flyout = Assert.IsType<MenuFlyout>(view.Grid.ContextFlyout);
        // Detach's divider is a raw <Separator/> — the first-class MenuFlyout
        // separator (#155; the earlier "raw Separator no-ops" claim was re-tested and
        // is false on FA 3.0.1). Pin the exact item sequence, separator included, by type.
        var kinds = flyout.Items.Select(i => i is MenuItem mi ? mi.Header : "<sep>").ToArray();
        Assert.Equal([Strings.ProjectsUpdate, Strings.Suspend, Strings.Resume, "<sep>", Strings.Detach], kinds);
        Assert.IsType<Separator>(flyout.Items[3]);

        fx.Dispose();
    }

    [AvaloniaFact]
    public void Recreated_view_replaces_a_stale_view_installed_handler_but_never_a_fake()
    {
        var fx = new HostGraphFixture();
        var vm = new ProjectsViewModel(fx.Store, fx.Clock, fx.Control);
        var window = fx.Host(new ProjectsView { DataContext = vm });
        window.Show();
        fx.Layout();
        var installedByFirstView = vm.ConfirmationHandler;
        Assert.NotNull(installedByFirstView);

        window.Content = new ProjectsView { DataContext = vm };
        fx.Layout();
        Assert.NotNull(vm.ConfirmationHandler);
        Assert.NotSame(installedByFirstView, vm.ConfirmationHandler);

        Func<ConfirmationRequest, Task<bool>> fake = _ => Task.FromResult(true);
        vm.ConfirmationHandler = fake;
        window.Content = new ProjectsView { DataContext = vm };
        fx.Layout();
        Assert.Same(fake, vm.ConfirmationHandler);

        fx.Dispose();
    }

    [AvaloniaFact]
    public void Failure_bar_reserves_zero_space_closed_and_never_displaces_the_grid()
    {
        var (fx, window, view, vm) = MakeView();
        window.Show();
        fx.Layout();

        var bar = view.GetVisualDescendants().OfType<FAInfoBar>()
            .Single(b => b.Name == "ControlFailureBar");
        var gridBoundsClosed = view.Grid.Bounds;

        Assert.False(bar.IsVisible);
        Assert.Equal(0, bar.Bounds.Height);

        vm.ControlFailure.Report("Detach failed", "host unreachable");
        fx.Layout();
        Assert.True(bar.IsVisible);
        Assert.True(bar.Bounds.Height > 0);
        Assert.Equal(gridBoundsClosed, view.Grid.Bounds);

        vm.ControlFailure.Clear();
        fx.Layout();
        Assert.False(bar.IsVisible);
        Assert.Equal(gridBoundsClosed, view.Grid.Bounds);

        fx.Dispose();
    }
}
