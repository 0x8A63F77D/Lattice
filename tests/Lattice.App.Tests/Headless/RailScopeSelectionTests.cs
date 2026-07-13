using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Lattice.App.Aggregation;
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
/// Codex round-4 P2: rail scope selection is driven by the CLICK/TAP gesture (OnHostRailTapped),
/// not by ListBox SelectionChanged. That closes two gaps the selection path had: (A) a header expand
/// that reveals a scoped host must re-highlight it (the old manual selection-revert clobbered it), and
/// (B) a click on a host that does NOT change the selection (a sole preselected host, a re-clicked
/// scoped host) must still persist the scope. Highlight is OWNED by ScopeMachine.highlightOf via
/// RebuildRail / ReassertRailHighlight; a rebuild never fabricates a scope event (R5). Every case is
/// driven through REAL pointer input (RailInput.ClickRow) — never a bare SelectedIndex assignment.
/// </summary>
public class RailScopeSelectionTests
{
    private static (ShellWindow Window, ShellViewModel Shell, HostRegistry Registry, HostStore Store, UiStateStore UiState)
        MakeShell(double height = 800)
    {
        var uiState = new UiStateStore(Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}-ui.json"));
        var path = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}.json");
        var registry = new HostRegistry(new LatticeConfig(5, []), path);
        var manager = new HostMonitorManager(registry, () => new FakeGuiRpcClient(), new FakeTimeProvider());
        var store = new HostStore(registry, manager, new ImmediateUiDispatcher());
        var shell = new ShellViewModel(registry, store, new ManualUiClock(), uiState, () => new FakeGuiRpcClient());
        var window = new ShellWindow { DataContext = shell, Height = height, Width = 1280 };
        return (window, shell, registry, store, uiState);
    }

    // Behavior 1 (Finding B): clicking the sole, already-preselected host persists the scope, and a
    // later second host must not lose it. Falsification: revert the fix and the persist assertion goes
    // RED (the old handler only reacted to a SelectionChanged, which a re-click of the sole row never
    // raises, and the Tapped handler acted only on AuthFailed).
    [AvaloniaFact]
    public void Clicking_the_sole_host_persists_the_scope_and_it_survives_a_second_host()
    {
        var (window, shell, registry, _, uiState) = MakeShell();
        window.Show();
        var host = TestData.MakeHostConfig(name: "only");
        registry.AddHost(host);
        Layout(window);

        // SingleHost: the sole row is the derived highlight, but Scope stays AllHosts — nothing is
        // written for a lone host UNLESS the user explicitly clicks it (decisions §7, no auto-pin).
        Assert.True(shell.Scope.IsAllHosts);
        Assert.Null(uiState.Load().ScopeHostId);
        var soleRow = Assert.Single(shell.RailEntries.OfType<HostRailItemViewModel>());
        Assert.Same(soleRow, shell.SelectedRailEntry);

        RailInput.ClickRow(window, soleRow);
        Layout(window);

        Assert.Equal(host.Id, shell.Scope.HostId);
        Assert.Equal(host.Id, uiState.Load().ScopeHostId);   // persisted (fresh Load)

        // Add a second host later: the explicitly chosen scope must survive.
        registry.AddHost(TestData.MakeHostConfig(name: "second"));
        shell.SetRailViewportHeight(1000.0);
        Layout(window);
        Assert.Equal(host.Id, shell.Scope.HostId);
        Assert.Equal(host.Id, uiState.Load().ScopeHostId);
        window.Close();
    }

    // Behavior 2 (Finding A): a scoped host hidden in a collapsed Healthy group is re-highlighted when
    // the header is expanded. Falsification: revert the fix and the final Same(scopedRow, highlight)
    // goes RED — the old handler reverted the selection to the stale pre-click value (null), clobbering
    // the highlight RebuildRail had just derived.
    [AvaloniaFact]
    public void Expanding_the_healthy_header_rehighlights_the_scoped_host_hidden_in_the_collapsed_group()
    {
        var (window, shell, registry, _, _) = MakeShell(height: 700);
        window.Show();
        for (var i = 0; i < 12; i++) registry.AddHost(TestData.MakeHostConfig(name: $"h{i}"));
        Layout(window);   // grouped (overflow), all Healthy, collapsed by default

        // Expand Healthy so a host row is visible; click one to scope it.
        RailInput.ClickRow(window, HealthyHeader(shell));
        Layout(window);
        var scopedId = shell.RailEntries.OfType<HostRailItemViewModel>().First().HostId;
        RailInput.ClickRow(window, shell.FindHostRow(scopedId)!);
        Layout(window);
        Assert.Equal(scopedId, shell.Scope.HostId);

        // Collapse Healthy → the scoped host row is hidden and nothing is highlighted (NoHighlight:
        // scope still holds the host, but it has no rendered row).
        RailInput.ClickRow(window, HealthyHeader(shell));
        Layout(window);
        Assert.DoesNotContain(shell.RailEntries, e => e is HostRailItemViewModel);
        Assert.Null(shell.SelectedRailEntry);

        // Click the Healthy header → expand → the now-visible scoped host row is the highlight again,
        // scope unchanged.
        RailInput.ClickRow(window, HealthyHeader(shell));
        Layout(window);

        Assert.Equal(scopedId, shell.Scope.HostId);
        Assert.Same(shell.FindHostRow(scopedId), shell.SelectedRailEntry);
        window.Close();
    }

    // Behavior 3: click an unselected host → scope + persist; re-click the already-scoped host →
    // still scoped, idempotent persist, no crash.
    [AvaloniaFact]
    public void Clicking_hosts_scopes_and_persists_and_reclicking_the_scoped_host_is_idempotent()
    {
        var (window, shell, registry, _, uiState) = MakeShell();
        window.Show();
        var a = TestData.MakeHostConfig(name: "a");
        var b = TestData.MakeHostConfig(name: "b");
        registry.AddHost(a);
        registry.AddHost(b);
        shell.SetRailViewportHeight(1000.0);   // Flat
        Layout(window);

        var rowB = shell.RailEntries.OfType<HostRailItemViewModel>().Single(r => r.HostId == b.Id);
        RailInput.ClickRow(window, rowB);
        Layout(window);
        Assert.Equal(b.Id, shell.Scope.HostId);
        Assert.Equal(b.Id, uiState.Load().ScopeHostId);

        RailInput.ClickRow(window, rowB);   // re-click the scoped host
        Layout(window);
        Assert.Equal(b.Id, shell.Scope.HostId);
        Assert.Equal(b.Id, uiState.Load().ScopeHostId);
        Assert.Same(rowB, shell.SelectedRailEntry);
        window.Close();
    }

    // Behavior 4: click the All-hosts sentinel → scope AllHosts, persisted id cleared to null.
    [AvaloniaFact]
    public void Clicking_the_all_hosts_sentinel_scopes_all_hosts_and_clears_the_persisted_id()
    {
        var (window, shell, registry, _, uiState) = MakeShell();
        window.Show();
        var a = TestData.MakeHostConfig(name: "a");
        registry.AddHost(a);
        registry.AddHost(TestData.MakeHostConfig(name: "b"));
        shell.SetRailViewportHeight(1000.0);   // Flat
        Layout(window);

        // Scope to a host first, so the sentinel click has something to clear.
        RailInput.ClickRow(window, shell.RailEntries.OfType<HostRailItemViewModel>().Single(r => r.HostId == a.Id));
        Layout(window);
        Assert.Equal(a.Id, uiState.Load().ScopeHostId);

        var sentinel = shell.RailEntries.OfType<AllHostsRailItemViewModel>().Single();
        RailInput.ClickRow(window, sentinel);
        Layout(window);

        Assert.True(shell.Scope.IsAllHosts);
        Assert.Null(uiState.Load().ScopeHostId);
        Assert.Same(sentinel, shell.SelectedRailEntry);
        window.Close();
    }

    // Behavior 5: a click on the non-collapsible Attention header changes no scope AND does not leave
    // the header as the highlighted selection (its toggle is a no-op, so ReassertRailHighlight restores
    // the derived highlight — the sentinel, since scope is AllHosts).
    [AvaloniaFact]
    public void Clicking_the_non_collapsible_attention_header_changes_no_scope_and_leaves_no_header_highlighted()
    {
        var (window, shell, registry, store, uiState) = MakeShell(height: 700);
        window.Show();
        for (var i = 0; i < 12; i++) registry.AddHost(TestData.MakeHostConfig(name: $"h{i}"));
        // Two hosts Unreachable (Retrying, attempt ≥ 4) → Attention tier; the rest Healthy. The next
        // rebuild (driven by Layout's viewport feed) reads these statuses and forms an Attention group.
        store.Hosts[0].Status = new ConnectionStatus(store.Hosts[0].Config.Id, HostConnectionState.Retrying, 5, null, "timeout", null);
        store.Hosts[1].Status = new ConnectionStatus(store.Hosts[1].Config.Id, HostConnectionState.Retrying, 5, null, "timeout", null);
        Layout(window);

        var attention = shell.RailEntries.OfType<GroupHeaderRailItemViewModel>()
            .Single(g => g.Tier.Equals(RailTier.Attention));
        Assert.False(attention.IsCollapsible);   // Attention is pinned open (decisions §2)
        Assert.True(shell.Scope.IsAllHosts);

        RailInput.ClickRow(window, attention);
        Layout(window);   // drains the deferred toggle + ReassertRailHighlight

        Assert.True(shell.Scope.IsAllHosts);                                 // no scope change
        Assert.Null(uiState.Load().ScopeHostId);
        Assert.IsNotType<GroupHeaderRailItemViewModel>(shell.SelectedRailEntry);
        Assert.Same(shell.RailEntries.OfType<AllHostsRailItemViewModel>().Single(), shell.SelectedRailEntry);
        window.Close();
    }

    // Behavior 6 (R5): a rail rebuild derives the highlight via highlightOf but NEVER mutates or
    // persists scope. Force several rebuilds (resize, host add) with scope AllHosts and prove no id is
    // ever persisted — the guarantee the old _rebuilding guard gave, now structural (SelectedRailEntry
    // has no scope side effect).
    [AvaloniaFact]
    public void Rail_rebuilds_derive_the_highlight_without_mutating_or_persisting_scope()
    {
        var (window, shell, registry, _, uiState) = MakeShell();
        window.Show();
        registry.AddHost(TestData.MakeHostConfig(name: "a"));
        registry.AddHost(TestData.MakeHostConfig(name: "b"));
        shell.SetRailViewportHeight(1000.0);
        Layout(window);
        Assert.True(shell.Scope.IsAllHosts);
        Assert.Null(uiState.Load().ScopeHostId);

        shell.SetRailViewportHeight(300.0);    // rebuild → grouped
        Layout(window);
        shell.SetRailViewportHeight(1000.0);   // rebuild → flat
        Layout(window);
        registry.AddHost(TestData.MakeHostConfig(name: "c"));   // rebuild → row added
        Layout(window);

        Assert.True(shell.Scope.IsAllHosts);
        Assert.Null(uiState.Load().ScopeHostId);
        Assert.Same(shell.RailEntries.OfType<AllHostsRailItemViewModel>().Single(), shell.SelectedRailEntry);
        window.Close();
    }

    // Behavior 7 (round-1 preserved): a click on an auth-failed host opens exactly ONE Edit dialog with
    // the password error AND scopes the host — one gesture, no double dialog-open, no double scope event.
    [AvaloniaFact]
    public async Task Clicking_an_auth_failed_host_opens_one_edit_dialog_and_scopes_it()
    {
        var (window, shell, registry, store, uiState) = MakeShell();
        window.Show();
        var host = TestData.MakeHostConfig(name: "auth");
        registry.AddHost(host);
        registry.AddHost(TestData.MakeHostConfig(name: "other"));
        store.Hosts[0].Status = new ConnectionStatus(host.Id, HostConnectionState.AuthFailed, 1, null, "unauthorized", null);
        shell.SetRailViewportHeight(1000.0);   // Flat: sentinel + host rows
        Layout(window);
        foreach (var row in shell.RailEntries.OfType<HostRailItemViewModel>()) row.Refresh();

        var authRow = shell.RailEntries.OfType<HostRailItemViewModel>().Single(r => r.HostId == host.Id);
        Assert.Equal(RailState.AuthFailed, authRow.State);

        RailInput.ClickRow(window, authRow);
        await HeadlessSync.WaitUntilAsync(() => window.GetVisualDescendants().OfType<AddHostDialog>().Any());

        var dialog = Assert.Single(window.GetVisualDescendants().OfType<AddHostDialog>());   // no double-open
        var vm = Assert.IsType<AddHostViewModel>(dialog.DataContext);
        Assert.Equal(HostDialogMode.Edit, vm.Mode);
        Assert.True(vm.HasPasswordError);
        Assert.Equal(host.Id, shell.Scope.HostId);                 // the click also scoped the host
        Assert.Equal(host.Id, uiState.Load().ScopeHostId);
        Assert.IsNotType<SettingsViewModel>(shell.CurrentPage);    // did NOT navigate to Settings
        dialog.Hide();
        window.Close();
    }

    private static GroupHeaderRailItemViewModel HealthyHeader(ShellViewModel shell) =>
        shell.RailEntries.OfType<GroupHeaderRailItemViewModel>().Single(g => g.Tier.Equals(RailTier.Healthy));
}
