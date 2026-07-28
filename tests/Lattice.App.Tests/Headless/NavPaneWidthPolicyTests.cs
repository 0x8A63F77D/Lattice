using System;
using System.IO;
using System.Linq;
using Avalonia.Headless.XUnit;
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
/// The USER-VISIBLE invariant the issue #133 fix buys, at both pane states: one
/// <c>Layout(window)</c> leaves every rail entry with a realized container. The damage is a
/// load-dependent flake, so nothing else in the suite goes reliably red if the guarantee is lost —
/// hence a pin of its own.
///
/// The mechanism that delivers it, and the alternatives that were measured and rejected, live with
/// the settle helper: <c>HeadlessLayout.SuppressPaneWidthAnimation</c> (the pane is born without a
/// width transition) plus the settle's own transition detach. <c>HeadlessLayoutSettleTests</c> pins
/// those. Issue #198 converged what used to be two separate mechanisms; this file deliberately
/// asserts only the outcome, so it stays honest if the mechanism is ever reshaped again.
/// </summary>
public class NavPaneWidthPolicyTests
{
    private static (ShellWindow Window, ShellViewModel Shell) TwoHostShell(double width)
    {
        var path = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}.json");
        var registry = new HostRegistry(new LatticeConfig(5, []), path);
        var manager = new HostMonitorManager(registry, () => new FakeGuiRpcClient(), new FakeTimeProvider());
        var store = new HostStore(registry, manager, new ImmediateUiDispatcher());
        var uiState = new UiStateStore(Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}-ui.json"));
        var shell = new ShellViewModel(registry, store, new ManualUiClock(), uiState, () => new FakeGuiRpcClient());
        var window = new ShellWindow { DataContext = shell };
        registry.AddHost(TestData.MakeHostConfig(name: "office-pc"));
        registry.AddHost(TestData.MakeHostConfig(name: "home-pc"));
        shell.SetRailViewportHeight(1000.0);
        window.Width = width;
        window.Show();
        Layout(window);
        return (window, shell);
    }

    // One Layout(window) realizes a container for EVERY rail entry, at both pane states. Under the
    // animated width the panel realizes only the anchor row and never recovers, which is what
    // reddened ShellRailTests / RailScopeSelectionTests / AuthFailedLinkage.
    [AvaloniaTheory]
    [InlineData(1280)]   // Expanded pane
    [InlineData(1050)]   // Compact pane (the 1000–1099 band)
    public void Every_rail_entry_has_a_realized_container_after_one_layout(double width)
    {
        var (window, shell) = TwoHostShell(width);

        var unrealized = shell.RailEntries.Where(e => window.HostList.ContainerFromItem(e) is null).ToList();

        Assert.True(unrealized.Count == 0,
            $"{unrealized.Count} of {shell.RailEntries.Count} rail entries have no realized container "
            + $"at window width {width}; the pane's width transition latches an empty effective "
            + "viewport on the rail's VirtualizingStackPanel (issue #133).");
        window.Close();
    }
}
