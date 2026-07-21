using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using FluentAvalonia.UI.Controls;
using Lattice.App.Infrastructure;
using Lattice.App.Localization;
using Lattice.App.Tests.Fakes;
using Lattice.App.ViewModels;
using Lattice.App.Views;
using Lattice.Boinc.GuiRpc;
using Lattice.Core;
using Lattice.Tests;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using static Lattice.Tests.HeadlessLayout;

namespace Lattice.App.Tests.Headless;

/// <summary>
/// M3 PR H (DI-4): the real XAML menu tree for the run-mode surface — the rail
/// host-row context menu and the Tasks command-bar "Computing" dropdown. Pins the
/// structure (lanes × modes, snooze durations, resume) and the exact CommandParameter
/// tokens, and proves the leaf command binding resolves ACROSS the flyout popup
/// boundary (the command-bar dropdown's items inherit their DataContext from the
/// button, a path compiled bindings cannot prove at build). A dead binding or a
/// mistyped token here means the menu silently no-ops in the app.
/// </summary>
public class RunModeMenuTests
{
    private static (ShellWindow Window, ShellViewModel Shell, HostRegistry Registry) MakeShell()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}.json");
        var registry = new HostRegistry(new LatticeConfig(5, []), path);
        var manager = new HostMonitorManager(registry, () => new FakeGuiRpcClient(), new FakeTimeProvider());
        var store = new HostStore(registry, manager, new ImmediateUiDispatcher());
        var uiState = new UiStateStore(Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}-ui.json"));
        var shell = new ShellViewModel(registry, store, new ManualUiClock(), uiState, () => new FakeGuiRpcClient());
        var window = new ShellWindow { DataContext = shell, Width = 1280, Height = 800 };
        return (window, shell, registry);
    }

    // Asserts the run-mode / snooze subtree hanging off one MenuFlyout: three lane
    // submenus each with Always/Auto/Never, a Snooze submenu with the three durations,
    // and every leaf's CommandParameter token. Structure is read from the logical
    // Items collections (the XAML-defined children), so no submenu need be opened.
    private static void AssertRunModeSubtree(IReadOnlyList<MenuItem> topLevel)
    {
        MenuItem runModes = topLevel.Single(mi => Equals(mi.Header, Strings.HostMenuRunModes));
        var lanes = runModes.Items.OfType<MenuItem>().ToList();
        Assert.Equal(
            new object?[] { Strings.HostMenuLaneCpu, Strings.HostMenuLaneGpu, Strings.HostMenuLaneNetwork },
            lanes.Select(l => l.Header));

        (MenuItem lane, string prefix)[] laneTokens =
            [(lanes[0], "cpu"), (lanes[1], "gpu"), (lanes[2], "net")];
        foreach (var (lane, prefix) in laneTokens)
        {
            var modes = lane.Items.OfType<MenuItem>().ToList();
            Assert.Equal(
                new object?[] { Strings.RunModeAlways, Strings.RunModeAuto, Strings.RunModeNever },
                modes.Select(m => m.Header));
            Assert.Equal($"{prefix}:always", modes[0].CommandParameter);
            Assert.Equal($"{prefix}:auto", modes[1].CommandParameter);
            Assert.Equal($"{prefix}:never", modes[2].CommandParameter);
        }

        MenuItem snooze = topLevel.Single(mi => Equals(mi.Header, Strings.HostMenuSnooze));
        var durations = snooze.Items.OfType<MenuItem>().ToList();
        Assert.Equal(
            new object?[] { Strings.SnoozeFifteenMinutes, Strings.SnoozeOneHour, Strings.SnoozeFourHours },
            durations.Select(d => d.Header));
        Assert.Equal("snooze:15", durations[0].CommandParameter);
        Assert.Equal("snooze:60", durations[1].CommandParameter);
        Assert.Equal("snooze:240", durations[2].CommandParameter);

        MenuItem resume = topLevel.Single(mi => Equals(mi.Header, Strings.HostMenuResume));
        Assert.Equal("resume", resume.CommandParameter);
    }

    [AvaloniaFact]
    public void Rail_context_menu_carries_the_run_mode_surface_with_correct_tokens()
    {
        var (window, _, registry) = MakeShell();
        window.Show();
        registry.AddHost(TestData.MakeHostConfig(name: "mini-01"));
        Layout(window);

        var hostRow = window.HostList.GetVisualDescendants().OfType<ListBoxItem>()
            .Single(li => li.DataContext is HostRailItemViewModel);
        var root = hostRow.GetVisualDescendants().OfType<DockPanel>().First();
        var flyout = Assert.IsType<MenuFlyout>(root.ContextFlyout);
        flyout.ShowAt(root);
        Dispatcher.UIThread.RunJobs();

        var topLevel = window.GetVisualDescendants().OfType<MenuItem>().ToList();
        AssertRunModeSubtree(topLevel);
        // The leaf binds {Binding SetRunModeCommand} on the row VM (the flyout's
        // inherited DataContext) — resolved, or the rail menu is dead in the app.
        MenuItem never = topLevel.Single(mi => Equals(mi.Header, Strings.HostMenuRunModes))
            .Items.OfType<MenuItem>().First()      // CPU
            .Items.OfType<MenuItem>().Last();      // Never
        Assert.NotNull(never.Command);
        flyout.Hide();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Command_bar_dropdown_appears_when_scoped_and_drives_the_op()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}.json");
        var registry = new HostRegistry(new LatticeConfig(5, []), path);
        var fakes = new Dictionary<string, FakeGuiRpcClient>();
        var manager = new HostMonitorManager(registry, () => new RoutingGuiRpcClient(fakes), new FakeTimeProvider());
        var store = new HostStore(registry, manager, new ImmediateUiDispatcher());
        var uiState = new UiStateStore(Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}-ui.json"));
        var shell = new ShellViewModel(registry, store, new ManualUiClock(), uiState,
            () => new RoutingGuiRpcClient(fakes));
        var window = new ShellWindow { DataContext = shell, Width = 1280, Height = 800 };
        window.Show();
        var fake = new FakeGuiRpcClient();
        fakes["host-a"] = fake;
        var cfg = TestData.MakeHostConfig(name: "host-a", address: "host-a");
        registry.AddHost(cfg);
        Layout(window);

        // Scope to the single host: the "Computing" dropdown becomes visible.
        shell.SelectHostScope(cfg.Id);
        Layout(window);
        var button = Assert.Single(window.GetVisualDescendants().OfType<Button>()
            .Where(b => Equals(b.Content, Strings.ComputingMenu)));
        Assert.True(button.IsVisible);

        var flyout = Assert.IsType<MenuFlyout>(button.Flyout);
        flyout.ShowAt(button);
        Dispatcher.UIThread.RunJobs();

        var topLevel = window.GetVisualDescendants().OfType<MenuItem>().ToList();
        AssertRunModeSubtree(topLevel);

        // The CPU→Never leaf binds {Binding ScopedHost.SetRunModeCommand}; execute it
        // and confirm the op reached the daemon — proves the Button.Flyout popup
        // inherited the TasksViewModel DataContext (the compiled binding resolved).
        MenuItem never = topLevel.Single(mi => Equals(mi.Header, Strings.HostMenuRunModes))
            .Items.OfType<MenuItem>().First()      // CPU
            .Items.OfType<MenuItem>().Last();      // Never
        Assert.NotNull(never.Command);
        // Deterministic: await the command's OWN task rather than a wall-clock settle
        // (AGENTS.md bans wall-clock settles; HeadlessSync's 5 s ceiling false-fails on
        // contended runners — PR #69). The op runs on the control lane, so awaiting
        // ExecuteAsync waits for the lane turn — and thus the fake's SetMode call — to
        // complete; RunJobs then flushes the success-path RequestRefresh post.
        await ((IAsyncRelayCommand)never.Command!).ExecuteAsync(never.CommandParameter);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("set_mode:cpu:never:0", fake.Calls);
        flyout.Hide();
        window.Close();
    }
}
