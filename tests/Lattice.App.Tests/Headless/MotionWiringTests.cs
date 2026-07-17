using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Lattice.App.Infrastructure;
using Lattice.App.Tests.Fakes;
using Lattice.App.ViewModels;
using Lattice.App.Views;
using Lattice.Core;
using Lattice.Tests;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Lattice.App.Tests.Headless;

// Machine (CORRECTNESS) gate for Wave-3 PR C1 motion (issue #32, design card 1h /
// shell-design spec §11). These pin the WIRING/BEHAVIOUR that a machine can read —
// a transition is present with the specified duration, and a data change is never
// gated on the animation (value-first). The visual FEEL (timing/curve) is
// owner-eyeball-gated per the wave's merge-gate policy and is NOT asserted here.
public class MotionWiringTests
{
    // ---- View switch (ShellWindow content host, 150 ms) ------------------------------------

    private static (ShellWindow Window, ShellViewModel Shell, HostRegistry Registry) MakeShell()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}.json");
        var registry = new HostRegistry(new LatticeConfig(5, []), path);
        // Manager never started: no sockets, no background threads in headless tests.
        var manager = new HostMonitorManager(registry, () => new FakeGuiRpcClient(), new FakeTimeProvider());
        var store = new HostStore(registry, manager, new ImmediateUiDispatcher());
        var uiState = new UiStateStore(Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}-ui.json"));
        var shell = new ShellViewModel(registry, store, new ManualUiClock(), uiState, () => new FakeGuiRpcClient());
        var window = new ShellWindow { DataContext = shell };
        return (window, shell, registry);
    }

    [AvaloniaFact]
    public void View_host_uses_the_fade_slide_page_transition_at_150ms()
    {
        var (window, _, registry) = MakeShell();
        window.Show();
        registry.AddHost(TestData.MakeHostConfig());
        HeadlessLayout.Layout(window);

        var transition = Assert.IsType<FadeSlidePageTransition>(window.ViewHost.PageTransition);
        Assert.Equal(TimeSpan.FromMilliseconds(150), transition.Duration);
        window.Close();
    }

    // Value-first: the new page's view-model is assigned to the content host synchronously on a
    // view switch — it is never held back for the transition to finish (the animation clock is
    // never advanced here).
    [AvaloniaFact]
    public void View_switch_binds_the_new_page_before_the_transition()
    {
        var (window, shell, registry) = MakeShell();
        window.Show();
        registry.AddHost(TestData.MakeHostConfig());
        HeadlessLayout.Layout(window);

        shell.SelectViewCommand.Execute("2"); // Transfers

        Assert.Same(shell.Transfers, shell.CurrentPage);
        Assert.Same(shell.CurrentPage, window.ViewHost.Content);
        window.Close();
    }

    // ---- Progress-bar width motion (Tasks + Transfers, 200 ms) -----------------------------

    private static void AssertWidthTransition(Border fill)
    {
        Assert.NotNull(fill.Transitions);
        var width = fill.Transitions!.OfType<DoubleTransition>()
            .Single(t => t.Property == Layoutable.WidthProperty);
        Assert.Equal(TimeSpan.FromMilliseconds(200), width.Duration);
    }

    private static Border ProgressFill(Visual root) =>
        root.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("progressFill"));

    [AvaloniaFact]
    public void Tasks_progress_fill_animates_its_width_over_200ms()
    {
        var fx = new HostGraphFixture();
        var vm = new TasksViewModel(fx.Store, fx.Clock, fx.UiState, fx.Density);
        var view = new TasksView { DataContext = vm };
        var window = fx.Host(view);
        window.Show();

        vm.Rows.Add(MakeTaskRow(0.5));
        fx.Layout();

        AssertWidthTransition(ProgressFill(window));
        fx.Dispose();
    }

    [AvaloniaFact]
    public void Transfers_progress_fill_animates_its_width_over_200ms()
    {
        var fx = new HostGraphFixture();
        var vm = new TransfersViewModel(fx.Store, fx.Clock, fx.Density);
        var view = new TransfersView { DataContext = vm };
        var window = fx.Host(view);
        window.Show();

        vm.Rows.Add(MakeTransferRow(0.5, "1.0 / 2.0 MB"));
        fx.Layout();

        AssertWidthTransition(ProgressFill(window));
        fx.Dispose();
    }

    // Value-first: a progress change reaches the bound cell in the same layout pass, never
    // waiting on the (cosmetic) width animation. The numeric read-out is not transitioned, so
    // its synchronous update proves data is not gated behind the motion.
    [AvaloniaFact]
    public void Transfers_progress_value_change_updates_the_bound_text_synchronously()
    {
        var fx = new HostGraphFixture();
        var vm = new TransfersViewModel(fx.Store, fx.Clock, fx.Density);
        var view = new TransfersView { DataContext = vm };
        var window = fx.Host(view);
        window.Show();

        var holder = MakeTransferRow(0.5, "1.0 / 2.0 MB");
        vm.Rows.Add(holder);
        fx.Layout();
        _ = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "1.0 / 2.0 MB");

        holder.Data = holder.Data with { Fraction = 0.9, ProgressText = "1.8 / 2.0 MB" };
        fx.Layout(); // no animation clock advanced — only the transition would need one

        _ = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "1.8 / 2.0 MB");
        fx.Dispose();
    }

    private static TaskRow MakeTaskRow(double fraction)
    {
        var row = new TaskRowViewModel(
            Project: "p", Application: "a", Name: "t", Fraction: fraction, PercentText: $"{fraction:P0}",
            ElapsedText: "1m", RemainingText: "5m", DeadlineText: "—",
            Deadline: DateTimeOffset.UtcNow.AddHours(1), StateKind: TaskStateKind.Running, StateText: "Running",
            IsDeadlineAtRisk: false, IsSuspended: false, HostId: Guid.NewGuid(), Host: "host-a");
        return new TaskRow(row.Key, row);
    }

    private static TransferRow MakeTransferRow(double fraction, string progressText)
    {
        var id = Guid.NewGuid();
        var row = new TransferRowViewModel(
            Key: new TransferRowKey(id, "https://project.example/", "file.dat", false),
            Name: "file.dat", Project: "p", DirectionText: "↓",
            ProgressText: progressText, Fraction: fraction, SpeedText: "1.0 MB/s",
            UiState: TransferUiState.Active, StatusText: "Active", HostId: id, Host: "host-a");
        return new TransferRow(row.Key, row);
    }
}
