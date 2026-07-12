using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
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

public class EventLogViewTests
{
    private static (Window Window, EventLogView View, EventLogViewModel Vm,
        HostRegistry Registry, HostMonitorManager Manager, Dictionary<string, FakeGuiRpcClient> Fakes) MakeView()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}.json");
        var registry = new HostRegistry(new LatticeConfig(5, []), path);
        var fakes = new Dictionary<string, FakeGuiRpcClient>();
        var manager = new HostMonitorManager(registry, () => new RoutingGuiRpcClient(fakes), new FakeTimeProvider());
        var store = new HostStore(registry, manager, new ImmediateUiDispatcher());
        var vm = new EventLogViewModel(store);
        var view = new EventLogView { DataContext = vm };
        var window = new Window { Width = 1280, Height = 800, Content = view };
        return (window, view, vm, registry, manager, fakes);
    }

    private static HostConfig AddHost(
        HostRegistry registry, Dictionary<string, FakeGuiRpcClient> fakes, string address)
    {
        var host = TestData.MakeHostConfig(name: address, address: address);
        fakes[address] = new FakeGuiRpcClient();
        registry.AddHost(host);
        return host;
    }

    private static DateTimeOffset T(int sec) =>
        new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.Zero).AddSeconds(sec);

    private static Message Msg(int seqno, int sec, string body,
        MessagePriority pri = MessagePriority.Info, string project = "Proj")
        => new(project, pri, seqno, T(sec), body);

    private static void Raise(HostMonitorManager manager, Guid hostId, params Message[] batch)
        => ManagerTestAccess.RaiseMessagesAdded(manager, new MessagesAddedEventArgs(hostId, batch));

    private static ScrollBar? VerticalScrollBar(Window window) =>
        window.GetVisualDescendants().OfType<ScrollBar>()
            .FirstOrDefault(b => b.Name == "PART_VerticalScrollbar");

    // ---- 1. Row-severity classes ----------------------------------------

    [AvaloniaFact]
    public void Warning_and_error_rows_carry_their_severity_classes()
    {
        var (window, _, _, registry, manager, fakes) = MakeView();
        var host = AddHost(registry, fakes, "host-a");
        window.Show();

        Raise(manager, host.Id,
            Msg(1, 1, "info", MessagePriority.Info),
            Msg(2, 2, "warn", MessagePriority.UserAlert),
            Msg(3, 3, "err", MessagePriority.InternalError));
        Layout(window);

        var rows = window.GetVisualDescendants().OfType<DataGridRow>()
            .ToDictionary(r => ((EventLogRow)r.DataContext!).Data.Body);

        Assert.DoesNotContain("warning", rows["info"].Classes);
        Assert.DoesNotContain("error", rows["info"].Classes);
        Assert.Contains("warning", rows["warn"].Classes);
        Assert.DoesNotContain("error", rows["warn"].Classes);
        Assert.Contains("error", rows["err"].Classes);
        Assert.DoesNotContain("warning", rows["err"].Classes);

        // Mockup §2c (grid-template-columns 128px 84px 140px 20px 1fr): the
        // priority icon is a dedicated 20px column present on EVERY row — info
        // rows included (regular gray info glyph), not only the tinted ones.
        // Exactly one visible icon per row, and its cell is the 20px column.
        foreach (var row in rows.Values)
        {
            var icon = Assert.Single(row.GetVisualDescendants().OfType<Avalonia.Controls.PathIcon>()
                .Where(i => i.IsVisible));
            var cell = icon.FindAncestorOfType<DataGridCell>();
            Assert.NotNull(cell);
            Assert.Equal(20, (int)cell!.Bounds.Width);
        }
        window.Close();
    }

    // ---- 2. Priority pill filters the grid ------------------------------

    [AvaloniaFact]
    public void Turning_off_the_warning_pill_removes_warning_rows()
    {
        var (window, _, vm, registry, manager, fakes) = MakeView();
        var host = AddHost(registry, fakes, "host-a");
        window.Show();

        Raise(manager, host.Id,
            Msg(1, 1, "info", MessagePriority.Info),
            Msg(2, 2, "warn", MessagePriority.UserAlert),
            Msg(3, 3, "err", MessagePriority.InternalError));
        Layout(window);
        Assert.Equal(3, vm.Rows.Count);

        // Drive the real pill control (two-way bound to ShowWarning), not the VM
        // property directly, so the view's binding is what's exercised.
        var warnPill = window.GetVisualDescendants().OfType<ToggleButton>()
            .Single(tb => tb.Classes.Contains("pill")
                && tb.GetVisualDescendants().OfType<TextBlock>()
                    .Any(t => t.Text == Strings.EventLogPillWarning));
        warnPill.IsChecked = false;
        Layout(window);

        Assert.False(vm.ShowWarning);
        Assert.Equal(["info", "err"], vm.Rows.Select(r => r.Data.Body));
        window.Close();
    }

    // ---- 3. Following auto-scroll (observed-call seam) -------------------

    [AvaloniaFact]
    public void Appending_a_batch_while_following_scrolls_the_last_row_into_view()
    {
        var (window, view, vm, registry, manager, fakes) = MakeView();
        var host = AddHost(registry, fakes, "host-a");
        window.Show();
        Layout(window);
        Assert.True(vm.IsFollowing); // default

        object? scrolled = null;
        view.ScrollRowIntoViewOverride = item => scrolled = item;

        Raise(manager, host.Id, Msg(1, 1, "a"), Msg(2, 2, "b"), Msg(3, 3, "c"));

        Assert.NotNull(scrolled);
        Assert.Same(vm.Rows[^1], scrolled);
        window.Close();
    }

    // Badge flow: messages accrue while the Event log page is hidden, so the VM
    // already holds rows before the view is realized. Opening the page (still
    // Following) must jump to the newest row — otherwise the grid sits at the top
    // while the status bar claims "Following live" until the next message.
    [AvaloniaFact]
    public void Opening_the_log_with_prepopulated_rows_scrolls_to_the_newest()
    {
        var (window, view, vm, registry, manager, fakes) = MakeView();
        var host = AddHost(registry, fakes, "host-a");

        // Populate BEFORE the view is shown/realized.
        Raise(manager, host.Id, Msg(1, 1, "a"), Msg(2, 2, "b"), Msg(3, 3, "c"));
        Assert.True(vm.IsFollowing);
        Assert.Equal(3, vm.Rows.Count);

        object? scrolled = null;
        view.ScrollRowIntoViewOverride = item => scrolled = item;

        window.Show();
        Layout(window);

        Assert.Same(vm.Rows[^1], scrolled);
        window.Close();
    }

    [AvaloniaFact]
    public void No_auto_scroll_request_is_made_while_not_following()
    {
        var (window, view, vm, registry, manager, fakes) = MakeView();
        var host = AddHost(registry, fakes, "host-a");
        window.Show();
        Layout(window);

        vm.IsFollowing = false;
        var requests = 0;
        view.ScrollRowIntoViewOverride = _ => requests++;

        Raise(manager, host.Id, Msg(1, 1, "a"), Msg(2, 2, "b"));

        Assert.Equal(0, requests);
        window.Close();
    }

    // ---- 4. Scrolling away from the bottom pauses Following --------------

    [AvaloniaFact]
    public void Scrolling_away_from_the_bottom_pauses_following()
    {
        var (window, _, vm, registry, manager, fakes) = MakeView();
        var host = AddHost(registry, fakes, "host-a");
        window.Show();

        Raise(manager, host.Id, ManyMessages(60));
        Layout(window);
        Assert.True(vm.IsFollowing);

        var bar = VerticalScrollBar(window);
        Assert.NotNull(bar);
        Assert.True(bar!.Maximum > 1, "60 rows must overflow the viewport and yield a scrollbar range");

        // Drive the offset away from the bottom, as a user drag would.
        bar.Value = bar.Maximum - 50;
        Layout(window);

        Assert.False(vm.IsFollowing);
        window.Close();
    }

    // ---- Feedback-loop guard (load-bearing) -----------------------------

    // The auto-scroll our own Following performs moves the vertical offset; that
    // move must NOT be read as a user scroll and pause Following. Pins the
    // _autoScrolling guard: with it, an offset change during an in-flight
    // auto-scroll leaves Following on. Removing the guard fails this test.
    [AvaloniaFact]
    public void Auto_scroll_offset_change_does_not_pause_following()
    {
        var (window, _, vm, registry, manager, fakes) = MakeView();
        var host = AddHost(registry, fakes, "host-a");
        window.Show();

        Raise(manager, host.Id, ManyMessages(60));
        Layout(window); // drains the initial auto-scroll's guard-clear posts
        Assert.True(vm.IsFollowing);

        var bar = VerticalScrollBar(window);
        Assert.NotNull(bar);

        // A new message while Following arms the guard (ScrollToNewestIfFollowing
        // sets _autoScrolling and posts a Background clear that has NOT run yet —
        // no dispatcher pump between here and the offset move below).
        Raise(manager, host.Id, Msg(61, 61, "newest", MessagePriority.Info));

        // Simulate the offset moving off the bottom as the auto-scroll settles.
        bar!.Value = bar.Maximum - 50;

        Assert.True(vm.IsFollowing, "our own auto-scroll must not pause Following");
        window.Close();
    }

    // ---- 5. Row height + teardown drain ---------------------------------

    [AvaloniaFact]
    public void Log_rows_are_26px_tall()
    {
        var (window, _, _, registry, manager, fakes) = MakeView();
        var host = AddHost(registry, fakes, "host-a");
        window.Show();

        Raise(manager, host.Id, Msg(1, 1, "a"), Msg(2, 2, "b"));
        Layout(window);

        var row = window.GetVisualDescendants().OfType<DataGridRow>().First();
        Assert.Equal(26, (int)row.Bounds.Height);
        window.Close();
    }

    [AvaloniaFact]
    public void Detaching_the_view_drains_all_row_class_subscriptions()
    {
        var (window, view, _, registry, manager, fakes) = MakeView();
        var host = AddHost(registry, fakes, "host-a");
        window.Show();

        Raise(manager, host.Id, Msg(1, 1, "a"));
        Layout(window);
        Assert.True(view.RowSubscriptionCount > 0, "a realized row should have subscribed");

        // Shell navigation teardown: the view leaves the tree while ItemsSource
        // is untouched (no UnloadingRow fires) — detach must drain.
        window.Content = null;
        Layout(window);

        Assert.Equal(0, view.RowSubscriptionCount);
        window.Close();
    }

    // ---- 6. Column header row (#55 / #57) --------------------------------

    [AvaloniaFact]
    public void Event_log_has_a_visible_column_header_row_with_spec_labels()
    {
        var (window, _, _, registry, manager, fakes) = MakeView();
        var host = AddHost(registry, fakes, "host-a");
        window.Show();
        Raise(manager, host.Id, Msg(1, 1, "hello"));
        Layout(window);

        var grid = window.GetVisualDescendants().OfType<DataGrid>().Single();
        Assert.Equal(DataGridHeadersVisibility.Column, grid.HeadersVisibility);

        var labels = grid.GetVisualDescendants().OfType<DataGridColumnHeader>()
            .Where(h => h.IsVisible && h.Bounds.Width > 0).Select(h => h.Content as string)
            .Where(s => !string.IsNullOrEmpty(s)).ToList();
        Assert.Contains(Strings.EventLogColTime, labels);
        Assert.Contains(Strings.ColHost, labels);
        Assert.Contains(Strings.ColProject, labels);
        Assert.Contains(Strings.EventLogColMessage, labels);
        window.Close();
    }

    [AvaloniaFact]
    public void Event_log_columns_are_resizable_via_header_edge_drag()
    {
        var (window, _, _, registry, manager, fakes) = MakeView();
        var host = AddHost(registry, fakes, "host-a");
        window.Show();
        Raise(manager, host.Id, Msg(1, 1, "hello"));
        Layout(window);

        var grid = window.GetVisualDescendants().OfType<DataGrid>().Single();
        Assert.True(grid.CanUserResizeColumns);
        var header = grid.GetVisualDescendants().OfType<DataGridColumnHeader>()
            .Single(h => (h.Content as string) == Strings.EventLogColTime);
        var start = header.Bounds.Width;
        var edge = header.TranslatePoint(new Point(header.Bounds.Width - 2, header.Bounds.Height / 2), window)!.Value;
        var target = edge.WithX(edge.X + 48);
        window.MouseMove(edge, RawInputModifiers.None);
        window.MouseDown(edge, MouseButton.Left, RawInputModifiers.None);
        window.MouseMove(target, RawInputModifiers.None);
        window.MouseUp(target, MouseButton.Left, RawInputModifiers.None);
        Layout(window);
        Assert.True(header.Bounds.Width > start + 20, $"resize should widen Time: start={start}, now={header.Bounds.Width}");
        window.Close();
    }

    [AvaloniaFact]
    public void Severity_gutter_column_has_no_divider_in_body_or_header()
    {
        var (window, _, _, registry, manager, fakes) = MakeView();
        var host = AddHost(registry, fakes, "host-a");
        window.Show();
        Raise(manager, host.Id, Msg(1, 1, "hello"));
        Layout(window);
        var grid = window.GetVisualDescendants().OfType<DataGrid>().Single();

        var iconCells = grid.GetVisualDescendants().OfType<DataGridCell>()
            .Where(c => c.Classes.Contains("priorityIcon")).ToList();
        Assert.NotEmpty(iconCells);
        foreach (var c in iconCells)
        {
            var line = VisualTree.FindInVisualTree<Avalonia.Controls.Shapes.Rectangle>(c, r => r.Name == "PART_RightGridLine");
            Assert.NotNull(line);
            Assert.Equal(0d, line!.Width);
        }

        // Header: order the REAL headers (Bounds.Width>0) left-to-right; severity is the 4th visible
        // column in all-hosts scope. Assert its separator is off and a normal header keeps its own.
        var headers = grid.GetVisualDescendants().OfType<DataGridColumnHeader>()
            .Where(h => h.Bounds.Width > 0).OrderBy(h => h.Bounds.X).ToList();
        // headers: Time(0) Host(1) Project(2) severity(3) Message(4)
        Assert.False(headers[3].AreSeparatorsVisible); // severity gutter
        Assert.True(headers[2].AreSeparatorsVisible);  // Project keeps its separator
        window.Close();
    }

    [AvaloniaFact]
    public void Event_log_default_column_widths_match_the_spec()
    {
        var (window, _, _, _, _, _) = MakeView();
        window.Show();
        Layout(window);
        var grid = window.GetVisualDescendants().OfType<DataGrid>().Single();
        double W(int i) => grid.Columns[i].Width.Value;
        Assert.Equal(128, W(0)); // Time
        Assert.Equal(84,  W(1)); // Host
        Assert.Equal(140, W(2)); // Project
        Assert.Equal(20,  W(3)); // severity
        Assert.True(grid.Columns[4].Width.IsStar); // Message
        window.Close();
    }

    private static Message[] ManyMessages(int count) =>
        Enumerable.Range(1, count).Select(i => Msg(i, i, $"line {i}")).ToArray();
}
