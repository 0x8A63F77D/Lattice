using Lattice.App.Infrastructure;
using Lattice.App.Localization;
using Lattice.App.Tests.Fakes;
using Lattice.App.ViewModels;
using Lattice.Boinc.GuiRpc;
using Lattice.Core;
using Lattice.Tests;
using Xunit;

namespace Lattice.App.Tests;

/// <summary>
/// The Event-log VM is fed by the MessagesReceived stream (raised here directly
/// on the manager, the only way to exercise message-only behaviour — real
/// polling couples messages with snapshots) folded through the pure MessageLog.
/// store.Changed drives prune/count only. QueueUiDispatcher keeps every step
/// synchronous: raise → Drain → assert.
/// </summary>
public class EventLogViewModelTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}.json");
    private readonly Dictionary<string, FakeGuiRpcClient> _fakes = [];
    private HostRegistry _registry = null!;
    private HostMonitorManager _manager = null!;
    private HostStore _store = null!;
    private QueueUiDispatcher _queue = null!;

    public ValueTask InitializeAsync()
    {
        _registry = new HostRegistry(new LatticeConfig(5, []), _path);
        _manager = new HostMonitorManager(_registry, () => new RoutingGuiRpcClient(_fakes), TimeProvider.System);
        _queue = new QueueUiDispatcher();
        _store = new HostStore(_registry, _manager, _queue);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _manager.DisposeAsync();
        File.Delete(_path);
    }

    private HostConfig AddHost(string address, FakeGuiRpcClient? fake = null)
    {
        var host = TestData.MakeHostConfig(name: address, address: address);
        _fakes[address] = fake ?? new FakeGuiRpcClient();
        _registry.AddHost(host);
        return host;
    }

    private EventLogViewModel MakeVm() => new(_store);

    // Distinct seconds ⇒ distinct MessageKey.TimestampTicks, so ordering is by time.
    private static DateTimeOffset T(int sec) => new(2026, 7, 11, 12, 0, sec, TimeSpan.Zero);

    private static Message Msg(
        int seqno, int sec, string body = "hello",
        MessagePriority pri = MessagePriority.Info, string project = "Proj")
        => new(project, pri, seqno, T(sec), body);

    // Raise a per-host batch through the manager; posts to the queue (no drain).
    private void Raise(Guid hostId, params Message[] batch)
        => ManagerTestAccess.RaiseMessagesAdded(_manager, new MessagesAddedEventArgs(hostId, batch));

    [Fact]
    public void Batch_appends_in_time_order_and_a_replay_appends_nothing()
    {
        var host = AddHost("host-a");
        var vm = MakeVm();
        Assert.True(vm.IsEmpty);

        // Deliver out of order; rows must land in timestamp order.
        Raise(host.Id, Msg(2, 2, "second"), Msg(1, 1, "first"));
        _queue.Drain();

        Assert.Equal(["first", "second"], vm.Rows.Select(r => r.Data.Body));
        Assert.False(vm.IsEmpty);

        // Reconnect at the VM level: the same batch re-fetched must dedup here —
        // no new rows, no CollectionChanged (identity preserved).
        var changes = 0;
        vm.Rows.CollectionChanged += (_, _) => changes++;
        Raise(host.Id, Msg(2, 2, "second"), Msg(1, 1, "first"));
        _queue.Drain();

        Assert.Equal(["first", "second"], vm.Rows.Select(r => r.Data.Body));
        Assert.Equal(0, changes);
    }

    [Fact]
    public void All_hosts_merges_by_time_single_host_scopes_to_one()
    {
        var a = AddHost("host-a");
        var b = AddHost("host-b");
        var vm = MakeVm();

        Raise(a.Id, Msg(1, 1, "a1"), Msg(2, 3, "a3"));
        Raise(b.Id, Msg(1, 2, "b2"));
        _queue.Drain();

        Assert.True(vm.IsAllHostsScope);
        Assert.Equal(["a1", "b2", "a3"], vm.Rows.Select(r => r.Data.Body));

        vm.Scope = new ScopeSelection(a.Id);

        Assert.False(vm.IsAllHostsScope);
        Assert.Equal(["a1", "a3"], vm.Rows.Select(r => r.Data.Body));
    }

    [Fact]
    public void Warning_pill_hides_and_restores_user_alert_rows()
    {
        var a = AddHost("host-a");
        var vm = MakeVm();

        Raise(a.Id,
            Msg(1, 1, "info", MessagePriority.Info),
            Msg(2, 2, "warn", MessagePriority.UserAlert),
            Msg(3, 3, "err", MessagePriority.InternalError));
        _queue.Drain();
        Assert.Equal(3, vm.Rows.Count);

        vm.ShowWarning = false;
        Assert.Equal(["info", "err"], vm.Rows.Select(r => r.Data.Body));

        // Re-enabling restores it: the pill filters the view, retention is intact.
        vm.ShowWarning = true;
        Assert.Equal(["info", "warn", "err"], vm.Rows.Select(r => r.Data.Body));
    }

    [Fact]
    public void Search_matches_body_and_project_case_insensitively()
    {
        var a = AddHost("host-a");
        var vm = MakeVm();

        Raise(a.Id,
            Msg(1, 1, "contains SETI here", project: "X"),   // body match (case-insensitive)
            Msg(2, 2, "plain body", project: "seti@home"),   // project match
            Msg(3, 3, "unrelated", project: "other"));       // no match
        _queue.Drain();

        vm.FilterText = "seti";

        Assert.Equal(["contains SETI here", "plain body"], vm.Rows.Select(r => r.Data.Body));
    }

    [Fact]
    public void Unread_counts_warning_and_error_only_while_the_view_is_inactive()
    {
        var a = AddHost("host-a");
        var vm = MakeVm();
        Assert.False(vm.IsViewActive);

        // Inactive: warning + error count; info never does.
        Raise(a.Id,
            Msg(1, 1, "w", MessagePriority.UserAlert),
            Msg(2, 2, "e", MessagePriority.InternalError),
            Msg(3, 3, "i", MessagePriority.Info));
        _queue.Drain();
        Assert.Equal(2, vm.UnreadCount);

        // Activation zeroes the badge.
        vm.IsViewActive = true;
        Assert.Equal(0, vm.UnreadCount);

        // Arrivals while active don't accrue.
        Raise(a.Id, Msg(4, 4, "w2", MessagePriority.UserAlert));
        _queue.Drain();
        Assert.Equal(0, vm.UnreadCount);
    }

    // Expected clipboard timestamp: same local-time conversion and format the
    // row projection applies (EventLogRowViewModel.From), computed in-test so
    // the literal layout assertion stays machine-TZ independent.
    private static string Ts(int sec) =>
        T(sec).ToLocalTime().ToString("MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void Clipboard_text_is_tab_separated_timestamp_host_body_lines_in_merged_order()
    {
        var a = AddHost("host-a");
        var b = AddHost("host-b");
        var vm = MakeVm();

        Raise(a.Id, Msg(1, 1, "a1"), Msg(2, 3, "a3"));
        Raise(b.Id, Msg(1, 2, "b2"));
        _queue.Drain();

        var expected = string.Join(Environment.NewLine,
            $"{Ts(1)}\thost-a\ta1",
            $"{Ts(2)}\thost-b\tb2",
            $"{Ts(3)}\thost-a\ta3");
        Assert.Equal(expected, vm.BuildClipboardText());
    }

    [Fact]
    public void Clipboard_text_covers_only_the_filtered_rows()
    {
        var a = AddHost("host-a");
        var vm = MakeVm();
        Raise(a.Id,
            Msg(1, 1, "info", MessagePriority.Info),
            Msg(2, 2, "warn", MessagePriority.UserAlert));
        _queue.Drain();

        vm.ShowWarning = false;

        Assert.Equal($"{Ts(1)}\thost-a\tinfo", vm.BuildClipboardText());
    }

    // The view's Copy handler skips the clipboard write entirely on
    // text.Length == 0 — that guard depends on this exact contract.
    [Fact]
    public void Clipboard_text_is_empty_for_no_rows()
    {
        AddHost("host-a");
        var vm = MakeVm();

        Assert.Equal("", vm.BuildClipboardText());
    }

    // TSV integrity: an embedded newline or tab in the body would corrupt the
    // pasted row/column structure (BOINC bodies are only end-trimmed at parse;
    // interior control chars survive). Each is flattened to a single space —
    // including \r\n as ONE space, not two.
    [Fact]
    public void Clipboard_text_flattens_control_characters_in_the_body()
    {
        var a = AddHost("host-a");
        var vm = MakeVm();
        Raise(a.Id, Msg(1, 1, "line1\r\nline2\tcol\rmid\nlast"));
        _queue.Drain();

        Assert.Equal($"{Ts(1)}\thost-a\tline1 line2 col mid last", vm.BuildClipboardText());
    }

    // Host display names are user-entered config (DisplayName => Name), so they
    // can carry the same interior tabs/newlines as bodies and must be flattened
    // too — otherwise a single host name corrupts every exported row's columns.
    [Fact]
    public void Clipboard_text_flattens_control_characters_in_the_host_name()
    {
        var host = TestData.MakeHostConfig(name: "host\tA\nnode", address: "addr-a");
        _fakes["addr-a"] = new FakeGuiRpcClient();
        _registry.AddHost(host);
        var vm = MakeVm();
        Raise(host.Id, Msg(1, 1, "body"));
        _queue.Drain();

        Assert.Equal($"{Ts(1)}\thost A node\tbody", vm.BuildClipboardText());
    }

    [Fact]
    public void Host_removal_prunes_its_rows_on_store_changed()
    {
        var a = AddHost("host-a");
        var b = AddHost("host-b");
        var vm = MakeVm();

        Raise(a.Id, Msg(1, 1, "a1"));
        Raise(b.Id, Msg(1, 2, "b1"));
        _queue.Drain();
        Assert.Equal(2, vm.Rows.Count);

        // Registry removal flows synchronously: registry.Changed → HostStore
        // prunes _hosts and raises Changed → VM prunes the removed host's log.
        _registry.RemoveHost(b.Id);

        Assert.Equal("a1", Assert.Single(vm.Rows).Data.Body);
    }

    [Fact]
    public async Task CountsText_reports_visible_messages_and_reachable_hosts()
    {
        var a = AddHost("host-a");
        AddHost("host-b");
        var vm = MakeVm();
        _manager.Start();

        // One visible message; both hosts poll their way to Connected (reachable).
        Raise(a.Id, Msg(1, 1, "hello"));

        await Wait.UntilAsync(
            () =>
            {
                _queue.Drain();
                return vm.CountsText == string.Format(Strings.EventLogCountsFmt, 1, 2);
            },
            "one visible message and two reachable hosts");
    }
}
