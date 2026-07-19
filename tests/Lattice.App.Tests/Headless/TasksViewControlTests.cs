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
        // DI-1(c) applies to the menu too (Codex P3, PR #135): a Separator must
        // sit between Resume and Abort, same as the command bar.
        var items = flyout.Items.ToList();
        int resumeAt = items.FindIndex(i => i is MenuItem m && Equals(m.Header, Strings.Resume));
        int abortAt = items.FindIndex(i => i is MenuItem m && Equals(m.Header, Strings.Abort));
        Assert.Contains(items.Skip(resumeAt + 1).Take(abortAt - resumeAt - 1), i => i is Separator);

        fx.Dispose();
    }

    [AvaloniaFact]
    public void Recreated_view_replaces_a_stale_view_installed_handler_but_never_a_fake()
    {
        // The shell recreates a TasksView per navigation over ONE long-lived
        // TasksViewModel. A handler installed by an earlier view resolves its
        // TopLevel off a detached control and silently declines every
        // Confirm-class op (Codex P2, PR #135): re-wiring must replace any
        // VIEW-installed handler (newest view wins) while a test-installed
        // fake stays authoritative.
        var fx = new HostGraphFixture();
        var vm = new TasksViewModel(fx.Store, fx.Clock, fx.UiState, fx.Density, fx.Control);
        var window = fx.Host(new TasksView { DataContext = vm });
        window.Show();
        fx.Layout();
        var installedByFirstView = vm.ConfirmationHandler;
        Assert.NotNull(installedByFirstView);

        window.Content = new TasksView { DataContext = vm };
        fx.Layout();
        Assert.NotNull(vm.ConfirmationHandler);
        Assert.NotSame(installedByFirstView, vm.ConfirmationHandler);

        Func<ConfirmationRequest, Task<bool>> fake = _ => Task.FromResult(true);
        vm.ConfirmationHandler = fake;
        window.Content = new TasksView { DataContext = vm };
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
