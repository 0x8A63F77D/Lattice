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
using Xunit;

namespace Lattice.App.Tests.Headless;

// Composition root, dispatcher discipline and settle rules all come from the
// shared HostGraphFixture — see its class doc. This suite adds only the view
// hosting: which view it builds, and the window it renders in.
public class EventLogViewTests
{
    private static (HostGraphFixture Fx, Window Window, EventLogView View, EventLogViewModel Vm) MakeView()
    {
        var fx = new HostGraphFixture();
        var vm = new EventLogViewModel(fx.Store);
        var view = new EventLogView { DataContext = vm };
        var window = fx.Host(view);
        return (fx, window, view, vm);
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
        var (fx, window, _, _) = MakeView();
        var host = fx.AddHost("host-a");
        window.Show();

        Raise(fx.Manager, host.Id,
            Msg(1, 1, "info", MessagePriority.Info),
            Msg(2, 2, "warn", MessagePriority.UserAlert),
            Msg(3, 3, "err", MessagePriority.InternalError));
        fx.Layout();

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
        fx.Dispose();
    }

    // ---- 2. Priority pill filters the grid ------------------------------

    [AvaloniaFact]
    public void Turning_off_the_warning_pill_removes_warning_rows()
    {
        var (fx, window, _, vm) = MakeView();
        var host = fx.AddHost("host-a");
        window.Show();

        Raise(fx.Manager, host.Id,
            Msg(1, 1, "info", MessagePriority.Info),
            Msg(2, 2, "warn", MessagePriority.UserAlert),
            Msg(3, 3, "err", MessagePriority.InternalError));
        fx.Layout();
        Assert.Equal(3, vm.Rows.Count);

        // Drive the real pill control (two-way bound to ShowWarning), not the VM
        // property directly, so the view's binding is what's exercised.
        var warnPill = window.GetVisualDescendants().OfType<ToggleButton>()
            .Single(tb => tb.Classes.Contains("pill")
                && tb.GetVisualDescendants().OfType<TextBlock>()
                    .Any(t => t.Text == Strings.EventLogPillWarning));
        warnPill.IsChecked = false;
        fx.Layout();

        Assert.False(vm.ShowWarning);
        Assert.Equal(["info", "err"], vm.Rows.Select(r => r.Data.Body));
        fx.Dispose();
    }

    // ---- 3. Following auto-scroll (observed-call seam) -------------------

    [AvaloniaFact]
    public void Appending_a_batch_while_following_scrolls_the_last_row_into_view()
    {
        var (fx, window, view, vm) = MakeView();
        var host = fx.AddHost("host-a");
        window.Show();
        fx.Layout();
        Assert.True(vm.IsFollowing); // default

        object? scrolled = null;
        view.ScrollRowIntoViewOverride = item => scrolled = item;

        Raise(fx.Manager, host.Id, Msg(1, 1, "a"), Msg(2, 2, "b"), Msg(3, 3, "c"));
        // Deliver the queued store post on this thread (the queue dispatcher's
        // deterministic stand-in for the old immediate delivery); the VM append
        // runs the view's follow auto-scroll synchronously in the same drain.
        fx.Drain();

        Assert.NotNull(scrolled);
        Assert.Same(vm.Rows[^1], scrolled);
        fx.Dispose();
    }

    // Badge flow: messages accrue while the Event log page is hidden, so the VM
    // already holds rows before the view is realized. Opening the page (still
    // Following) must jump to the newest row — otherwise the grid sits at the top
    // while the status bar claims "Following live" until the next message.
    [AvaloniaFact]
    public void Opening_the_log_with_prepopulated_rows_scrolls_to_the_newest()
    {
        var (fx, window, view, vm) = MakeView();
        var host = fx.AddHost("host-a");

        // Populate BEFORE the view is shown/realized.
        Raise(fx.Manager, host.Id, Msg(1, 1, "a"), Msg(2, 2, "b"), Msg(3, 3, "c"));
        fx.Drain(); // deliver the batch to the (not-yet-shown) VM
        Assert.True(vm.IsFollowing);
        Assert.Equal(3, vm.Rows.Count);

        object? scrolled = null;
        view.ScrollRowIntoViewOverride = item => scrolled = item;

        window.Show();
        fx.Layout();

        Assert.Same(vm.Rows[^1], scrolled);
        fx.Dispose();
    }

    [AvaloniaFact]
    public void No_auto_scroll_request_is_made_while_not_following()
    {
        var (fx, window, view, vm) = MakeView();
        var host = fx.AddHost("host-a");
        window.Show();
        fx.Layout();

        vm.IsFollowing = false;
        var requests = 0;
        view.ScrollRowIntoViewOverride = _ => requests++;

        Raise(fx.Manager, host.Id, Msg(1, 1, "a"), Msg(2, 2, "b"));
        fx.Drain(); // deliver the batch so "no scroll while not following" is genuinely exercised

        Assert.Equal(0, requests);
        fx.Dispose();
    }

    // ---- 4. Scrolling away from the bottom pauses Following --------------

    [AvaloniaFact]
    public void Scrolling_away_from_the_bottom_pauses_following()
    {
        var (fx, window, _, vm) = MakeView();
        var host = fx.AddHost("host-a");
        window.Show();

        Raise(fx.Manager, host.Id, ManyMessages(60));
        fx.Layout();
        Assert.True(vm.IsFollowing);

        var bar = VerticalScrollBar(window);
        Assert.NotNull(bar);
        Assert.True(bar!.Maximum > 1, "60 rows must overflow the viewport and yield a scrollbar range");

        // Drive the offset away from the bottom, as a user drag would.
        bar.Value = bar.Maximum - 50;
        fx.Layout();

        Assert.False(vm.IsFollowing);
        fx.Dispose();
    }

    // ---- Feedback-loop guard (load-bearing) -----------------------------

    // The auto-scroll our own Following performs moves the vertical offset; that
    // move must NOT be read as a user scroll and pause Following. Pins the
    // _autoScrolling guard: with it, an offset change during an in-flight
    // auto-scroll leaves Following on. Removing the guard fails this test.
    [AvaloniaFact]
    public void Auto_scroll_offset_change_does_not_pause_following()
    {
        var (fx, window, _, vm) = MakeView();
        var host = fx.AddHost("host-a");
        window.Show();

        Raise(fx.Manager, host.Id, ManyMessages(60));
        fx.Layout(); // drains the initial auto-scroll's guard-clear posts
        Assert.True(vm.IsFollowing);

        var bar = VerticalScrollBar(window);
        Assert.NotNull(bar);

        // A new message while Following arms the guard (ScrollToNewestIfFollowing
        // sets _autoScrolling and posts a Background clear to the REAL dispatcher).
        // A BARE Drain delivers the store batch on this thread — arming the guard —
        // WITHOUT pumping that Background clear (fx.Layout would RunJobs and clear
        // it): the "no dispatcher pump between here and the offset move" the guard
        // depends on is preserved exactly.
        Raise(fx.Manager, host.Id, Msg(61, 61, "newest", MessagePriority.Info));
        fx.Drain();

        // Simulate the offset moving off the bottom as the auto-scroll settles.
        bar!.Value = bar.Maximum - 50;

        Assert.True(vm.IsFollowing, "our own auto-scroll must not pause Following");
        fx.Dispose();
    }

    // ---- 5. Row height + teardown drain ---------------------------------

    [AvaloniaFact]
    public void Log_rows_are_26px_tall()
    {
        var (fx, window, _, _) = MakeView();
        var host = fx.AddHost("host-a");
        window.Show();

        Raise(fx.Manager, host.Id, Msg(1, 1, "a"), Msg(2, 2, "b"));
        fx.Layout();

        var row = window.GetVisualDescendants().OfType<DataGridRow>().First();
        Assert.Equal(26, (int)row.Bounds.Height);
        fx.Dispose();
    }

    [AvaloniaFact]
    public void Detaching_the_view_drains_all_row_class_subscriptions()
    {
        var (fx, window, view, _) = MakeView();
        var host = fx.AddHost("host-a");
        window.Show();

        Raise(fx.Manager, host.Id, Msg(1, 1, "a"));
        fx.Layout();
        Assert.True(view.RowSubscriptionCount > 0, "a realized row should have subscribed");

        // Shell navigation teardown: the view leaves the tree while ItemsSource
        // is untouched (no UnloadingRow fires) — detach must drain.
        window.Content = null;
        fx.Layout();

        Assert.Equal(0, view.RowSubscriptionCount);
        fx.Dispose();
    }

    // ---- 6. Column header row (#55 / #57) --------------------------------

    [AvaloniaFact]
    public void Event_log_has_a_visible_column_header_row_with_spec_labels()
    {
        var (fx, window, _, _) = MakeView();
        // Two hosts to earn the Host column: post-ScopeMachine, IsAllHostsScope keys on >1
        // registered host, so a single host hides Host (mirrors the Tasks/Transfers header tests).
        var host = fx.AddHost("host-a");
        fx.AddHost("host-b");
        window.Show();
        Raise(fx.Manager, host.Id, Msg(1, 1, "hello"));
        fx.Layout();

        var grid = window.GetVisualDescendants().OfType<DataGrid>().Single();
        Assert.Equal(DataGridHeadersVisibility.Column, grid.HeadersVisibility);

        var labels = grid.GetVisualDescendants().OfType<DataGridColumnHeader>()
            .Where(h => h.IsVisible && h.Bounds.Width > 0).Select(h => h.Content as string)
            .Where(s => !string.IsNullOrEmpty(s)).ToList();
        Assert.Contains(Strings.EventLogColTime, labels);
        Assert.Contains(Strings.ColHost, labels);
        Assert.Contains(Strings.ColProject, labels);
        Assert.Contains(Strings.EventLogColMessage, labels);
        fx.Dispose();
    }

    [AvaloniaFact]
    public void Event_log_columns_are_resizable_via_header_edge_drag()
    {
        var (fx, window, _, _) = MakeView();
        var host = fx.AddHost("host-a");
        window.Show();
        Raise(fx.Manager, host.Id, Msg(1, 1, "hello"));
        fx.Layout();

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
        fx.Layout();
        Assert.True(header.Bounds.Width > start + 20, $"resize should widen Time: start={start}, now={header.Bounds.Width}");
        fx.Dispose();
    }

    [AvaloniaFact]
    public void Severity_gutter_column_has_no_divider_in_body_or_header()
    {
        var (fx, window, _, _) = MakeView();
        // Two hosts so IsAllHostsScope is true and the Host column is present — the header order
        // asserted below (Time·Host·Project·severity·Message) needs it (post-ScopeMachine >1-host rule).
        var host = fx.AddHost("host-a");
        fx.AddHost("host-b");
        window.Show();
        Raise(fx.Manager, host.Id, Msg(1, 1, "hello"));
        fx.Layout();
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
        fx.Dispose();
    }

    [AvaloniaFact]
    public void Event_log_default_column_widths_match_the_spec()
    {
        var (fx, window, _, _) = MakeView();
        window.Show();
        fx.Layout();
        var grid = window.GetVisualDescendants().OfType<DataGrid>().Single();
        double W(int i) => grid.Columns[i].Width.Value;
        Assert.Equal(128, W(0)); // Time
        Assert.Equal(84,  W(1)); // Host
        Assert.Equal(140, W(2)); // Project
        Assert.Equal(20,  W(3)); // severity
        Assert.False(grid.Columns[4].Width.IsStar); // Message fixed (not a star — Finding A)
        Assert.Equal(560, W(4)); // Message
        fx.Dispose();
    }

    private static Message[] ManyMessages(int count) =>
        Enumerable.Range(1, count).Select(i => Msg(i, i, $"line {i}")).ToArray();

    // REPRODUCTION (Finding D): entering the log with Following active and rows already
    // present (nav-item / badge / startup all share this fresh-mount path) must land at the
    // NEWEST row. Exercises the REAL Grid.ScrollIntoView (no override seam) and reads the
    // actual vertical offset — the badge test above proves only that the HOOK fires.
    [AvaloniaFact]
    public void Entering_the_log_while_following_lands_at_the_bottom()
    {
        var (fx, window, _, vm) = MakeView();
        var host = fx.AddHost("host-a");

        // Rows accrue while the page is hidden (arrived before the view is realized).
        // Drain now, BEFORE Show(), so the 80 rows are genuinely in the VM ahead of
        // the first mount — otherwise the queued batch would only land at Layout()'s
        // drain after Show(), quietly turning this fresh-mount scenario into a
        // visible-view append (Codex P2; matches the sibling prepopulated-row test).
        Raise(fx.Manager, host.Id, ManyMessages(80));
        fx.Drain();
        Assert.True(vm.IsFollowing);

        window.Show();
        fx.Layout();

        var bar = VerticalScrollBar(window);
        Assert.NotNull(bar);
        Assert.True(bar!.Maximum > 1, "80 rows must overflow the viewport");
        Assert.True(bar.Value >= bar.Maximum - 0.5,
            $"entering while Following should sit at the newest row: value={bar.Value}, max={bar.Maximum}");
        fx.Dispose();
    }
}
