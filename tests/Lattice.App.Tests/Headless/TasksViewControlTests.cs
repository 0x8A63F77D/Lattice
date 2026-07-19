using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using Lattice.App.Infrastructure;
using Lattice.App.Localization;
using Lattice.App.ViewModels;
using Lattice.App.Views;
using Xunit;

namespace Lattice.App.Tests.Headless;

/// <summary>
/// M3 PR F view-layer gates for the Tasks control surface: command/tooltip
/// wiring on the command-bar buttons, the DI-1(c) misclick separation of
/// Abort, the row context menu, and the failure InfoBar's zero-layout-when-
/// closed geometry (the issue-#107 8px-strip regression pin).
/// </summary>
public class TasksViewControlTests
{
    private static (HostGraphFixture Fx, Window Window, TasksView View, TasksViewModel Vm) MakeView()
    {
        var fx = new HostGraphFixture();
        var vm = new TasksViewModel(fx.Store, fx.Clock, fx.UiState, fx.Density, fx.Control);
        var view = new TasksView { DataContext = vm };
        var window = fx.Host(view);
        return (fx, window, view, vm);
    }

    private static Button Named(TasksView view, string name) =>
        view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == name);

    [AvaloniaFact]
    public void Buttons_bind_commands_and_show_tooltips_while_disabled()
    {
        var (fx, window, view, vm) = MakeView();
        window.Show();
        fx.Layout();

        foreach (var name in new[] { "SuspendButton", "ResumeButton", "AbortButton" })
        {
            var button = Named(view, name);
            // No selection → DI-3 disables. Command-driven disablement surfaces
            // as IsEffectivelyEnabled (Avalonia keeps the local IsEnabled true);
            // the tooltip must be configured to show on the DISABLED button
            // (that is its whole purpose).
            Assert.False(button.IsEffectivelyEnabled);
            Assert.True(ToolTip.GetShowOnDisabled(button));
        }

        // The tooltip text rides ControlDisabledReason; structural binding check.
        vm.ControlDisabledReason = Strings.ControlHostNotConnected;
        fx.Layout();
        Assert.Equal(Strings.ControlHostNotConnected, ToolTip.GetTip(Named(view, "AbortButton")));

        fx.Dispose();
    }

    [AvaloniaFact]
    public void Abort_is_separated_from_suspend_and_resume()
    {
        var (fx, window, view, _) = MakeView();
        window.Show();
        fx.Layout();

        // DI-1(c): a Separator must sit between the reversible pair and Abort.
        var panel = (StackPanel)Named(view, "AbortButton").Parent!;
        var children = panel.Children.ToList();
        int resume = children.IndexOf(Named(view, "ResumeButton"));
        int abort = children.IndexOf(Named(view, "AbortButton"));
        Assert.True(resume >= 0 && abort > resume);
        Assert.Contains(children.Skip(resume + 1).Take(abort - resume - 1), c => c is Separator);

        fx.Dispose();
    }

    [AvaloniaFact]
    public void Row_context_menu_offers_the_three_ops()
    {
        var (fx, window, view, _) = MakeView();
        window.Show();
        fx.Layout();

        var flyout = Assert.IsType<MenuFlyout>(view.Grid.ContextFlyout);
        var headers = flyout.Items.OfType<MenuItem>().Select(i => i.Header).ToArray();
        Assert.Equal([Strings.Suspend, Strings.Resume, Strings.Abort], headers);

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

        // Closed: invisible AND occupying zero layout space (the #107 pin — a
        // closed InfoBar must not reserve even its margin).
        Assert.False(bar.IsVisible);
        Assert.Equal(0, bar.Bounds.Height);

        vm.ControlFailure.Report("Suspend failed", "host unreachable");
        fx.Layout();
        Assert.True(bar.IsVisible);
        Assert.True(bar.Bounds.Height > 0);
        // Overlay placement: opening the bar must not move the grid.
        Assert.Equal(gridBoundsClosed, view.Grid.Bounds);

        vm.ControlFailure.Clear();
        fx.Layout();
        Assert.False(bar.IsVisible);
        Assert.Equal(gridBoundsClosed, view.Grid.Bounds);

        fx.Dispose();
    }
}
