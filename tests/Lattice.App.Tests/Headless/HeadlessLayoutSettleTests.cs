using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Lattice.App.Infrastructure;
using Lattice.App.Tests.Fakes;
using Lattice.App.ViewModels;
using Lattice.App.Views;
using Lattice.Core;
using Lattice.Tests;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using static Lattice.Tests.HeadlessLayout;

namespace Lattice.App.Tests.Headless;

/// <summary>
/// Regression pin for the cold-first-run flake: <c>Layout(window)</c> must return a SETTLED window,
/// never a frame sampled out of an in-flight animation.
///
/// Before the fix, <c>Layout</c> was a manual measure/arrange plus one <c>RunJobs()</c>. Two things
/// made that a wall-clock coin flip: a real layout pass only runs when Avalonia's headless 60 fps
/// render <c>DispatcherTimer</c> is due, and Avalonia's <c>SplitView</c> slides the nav pane open
/// with a <c>DoubleTransition</c> driven by the real animation clock. The same unchanged shell
/// produced rail widths of 0, 22, 24, 141, 184, 193, 206, 222, 224, 246, 250, 251 and 254 across
/// consecutive builds — and at width 0 the rail's item containers are not realized at all, which is
/// how it surfaced: "Rail row has no realized container" out of <c>RailInput.ClickRow</c>, plus
/// rail-mode assertions reading a half-open pane.
///
/// Falsification: revert HeadlessLayout to measure/arrange + a single RunJobs() and
/// <see cref="Layout_settles_identical_shells_to_identical_geometry"/> goes RED immediately — the
/// widths disagree across builds.
/// </summary>
public class HeadlessLayoutSettleTests
{
    private static (ShellWindow Window, ShellViewModel Shell, HostRegistry Registry) MakeShell()
    {
        var uiState = new UiStateStore(Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}-ui.json"));
        var path = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}.json");
        var registry = new HostRegistry(new LatticeConfig(5, []), path);
        var manager = new HostMonitorManager(registry, () => new FakeGuiRpcClient(), new FakeTimeProvider());
        var store = new HostStore(registry, manager, new ImmediateUiDispatcher());
        var shell = new ShellViewModel(registry, store, new ManualUiClock(), uiState, () => new FakeGuiRpcClient());
        return (new ShellWindow { DataContext = shell, Width = 1280, Height = 800 }, shell, registry);
    }

    // Identical inputs must give identical geometry. Windows are all constructed first so the
    // Show + Layout + read of each one lands inside a single 60 fps frame, i.e. the regime where a
    // due render tick cannot be relied on to have settled anything.
    [AvaloniaFact]
    public void Layout_settles_identical_shells_to_identical_geometry()
    {
        const int builds = 8;
        var shells = Enumerable.Range(0, builds).Select(_ => MakeShell()).ToList();
        var geometry = new List<string>(builds);

        foreach ((ShellWindow window, ShellViewModel shell, HostRegistry registry) in shells)
        {
            registry.AddHost(TestData.MakeHostConfig(name: "a"));
            registry.AddHost(TestData.MakeHostConfig(name: "b"));
            window.Show();
            shell.SetRailViewportHeight(1000.0);   // Flat: sentinel + both host rows
            Layout(window);

            geometry.Add($"{window.HostList.Bounds}");
            window.Close();
        }

        Assert.Single(geometry.Distinct());
    }

    // The geometric consequence the flake actually tripped over: every rail entry has a realized,
    // non-degenerate container, so RailInput.ClickRow can resolve one and hit-test it.
    [AvaloniaFact]
    public void Layout_realizes_a_container_for_every_rail_entry()
    {
        const int builds = 8;
        var shells = Enumerable.Range(0, builds).Select(_ => MakeShell()).ToList();

        foreach ((ShellWindow window, ShellViewModel shell, HostRegistry registry) in shells)
        {
            registry.AddHost(TestData.MakeHostConfig(name: "a"));
            registry.AddHost(TestData.MakeHostConfig(name: "b"));
            window.Show();
            shell.SetRailViewportHeight(1000.0);
            Layout(window);

            Assert.All(shell.RailEntries, entry =>
            {
                Control? container = window.HostList.ContainerFromItem(entry);
                Assert.NotNull(container);
                Assert.True(container.Bounds.Width > 0 && container.Bounds.Height > 0,
                    $"{entry.GetType().Name} container is degenerate ({container.Bounds}) — the nav "
                    + "pane was measured mid-animation.");
            });
            window.Close();
        }
    }

    // Layout DETACHES transitions for the settle and puts them back; a test that asserts a
    // transition is wired (MotionWiringTests, ProgressFillBehaviorTests, ProjectsViewTests) runs
    // after a Layout, so losing them would be a silent false green there.
    [AvaloniaFact]
    public void Layout_restores_the_transitions_it_detached()
    {
        (ShellWindow window, ShellViewModel shell, HostRegistry registry) = MakeShell();
        registry.AddHost(TestData.MakeHostConfig(name: "a"));
        window.Show();
        Layout(window);

        Assert.Contains(
            window.GetSelfAndVisualDescendants().OfType<Avalonia.Animation.Animatable>(),
            a => a.Transitions is { Count: > 0 });
        window.Close();
    }
}
