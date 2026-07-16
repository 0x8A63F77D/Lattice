using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lattice.App.Aggregation;
using Lattice.App.Infrastructure;
using Lattice.App.Localization;
using Lattice.App.Tests.Fakes;
using Lattice.App.ViewModels;
using Lattice.App.Views;
using Lattice.Core;
using Lattice.Tests;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using Rectangle = Avalonia.Controls.Shapes.Rectangle;
using static Lattice.Tests.HeadlessLayout;

namespace Lattice.App.Tests.Headless;

// View-level concerns only (hierarchy classes, design row geometry, the chevron
// binding, in-place liveness, single-host column, teardown drain). Rows are
// built by hand and added on the UI thread — the realized-DataGridRow idiom from
// TasksViewTests. The store → VM hierarchy/expansion pipeline is covered
// exhaustively by ProjectsViewModelTests (Task 3), so it is not re-driven here.
public class ProjectsViewTests
{
    private const string Url = "http://p/";

    // hostCount seeds the registry with that many hosts before the store/VM are
    // built, so tests that need genuine multi-host presentation (the Hosts
    // column, gated on IsAllHostsScope = AllHosts && >1 registered host) can opt
    // in. Zero keeps the hand-built-rows fixtures host-free as before.
    private static (Window Window, ProjectsView View, ProjectsViewModel Vm) MakeView(int hostCount = 0)
    {
        var path = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}.json");
        var registry = new HostRegistry(new LatticeConfig(5, []), path);
        for (var i = 0; i < hostCount; i++)
            registry.AddHost(TestData.MakeHostConfig(name: $"host-{i}", address: $"host-{i}"));
        var manager = new HostMonitorManager(
            registry, () => new RoutingGuiRpcClient(new Dictionary<string, FakeGuiRpcClient>()), new FakeTimeProvider());
        var store = new HostStore(registry, manager, new ImmediateUiDispatcher());
        var clock = new ManualUiClock();
        var vm = new ProjectsViewModel(store, clock);
        var view = new ProjectsView { DataContext = vm };
        var window = new Window { Width = 1280, Height = 800, Content = view };
        return (window, view, vm);
    }

    // Hand-built holders don't run compute/groupKey — they build a plausible
    // GroupSortKey by hand (values don't matter, this file never exercises
    // sort semantics; ProjectRowsTests + ProjectRowViewModelTests cover that).
    private static RowSortKey ParentSortKey(string url) =>
        new(
            new GroupSortKey(
                nameKey: "p", hostCount: 1, shareMax: 100, shareMin: 100, avgCredit: 10, totalCredit: 20,
                statusRank: 0, masterUrl: url),
            RowLevel.ParentRow);

    private static RowSortKey ChildSortKey(string url, string hostName, Guid hostId) =>
        new(
            new GroupSortKey(
                nameKey: "p", hostCount: 1, shareMax: 100, shareMin: 100, avgCredit: 10, totalCredit: 20,
                statusRank: 0, masterUrl: url),
            RowLevel.NewChildRow(hostName, hostId));

    private static ProjectRow ParentHolder(
        ProjectStatusKind statusKind, string statusText, bool showChevron = false, bool isExpanded = false,
        string? url = null)
    {
        url ??= Url;
        var data = new ProjectRowViewModel(
            Key: ProjectRowKey.NewParentKey(url),
            MasterUrl: url, IsParent: true, IsExpanded: isExpanded, ShowChevron: showChevron,
            Name: "P", HostsText: "1", ShareText: "100", ShowShareBar: true, ShareFraction: 1.0,
            AvgCreditText: "10", TotalCreditText: "20", TasksText: "",
            StatusKind: statusKind, StatusText: statusText, SortKey: ParentSortKey(url));
        return new ProjectRow(data.Key, data);
    }

    private static ProjectRow ChildHolder(string hostName)
    {
        var hostId = Guid.NewGuid();
        var data = new ProjectRowViewModel(
            Key: ProjectRowKey.NewChildKey(Url, hostId),
            MasterUrl: Url, IsParent: false, IsExpanded: false, ShowChevron: false,
            Name: hostName, HostsText: "", ShareText: "100", ShowShareBar: true, ShareFraction: 1.0,
            AvgCreditText: "10", TotalCreditText: "20", TasksText: string.Format(Strings.ProjectsTaskCountFmt, 3),
            StatusKind: ProjectStatusKind.Active, StatusText: Strings.ProjectsStatusActive,
            SortKey: ChildSortKey(Url, hostName, hostId));
        return new ProjectRow(data.Key, data);
    }

    private static List<string?> VisibleHeaders(Window window) =>
        window.GetVisualDescendants().OfType<DataGridColumnHeader>()
            .Where(h => h.IsVisible)
            .Select(h => h.Content as string)
            .Where(text => text is not null)
            .ToList();

    private static IEnumerable<string?> TextsIn(Visual row) =>
        row.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text);

    // A parent holder with a caller-chosen project name + url, so a reorder is visible in the
    // Project cell text. (ParentHolder hardcodes Name="P", useless for order assertions.)
    private static ProjectRow NamedParent(string name, string url)
    {
        var data = new ProjectRowViewModel(
            Key: ProjectRowKey.NewParentKey(url),
            MasterUrl: url, IsParent: true, IsExpanded: false, ShowChevron: false,
            Name: name, HostsText: "1", ShareText: "100", ShowShareBar: true, ShareFraction: 1.0,
            AvgCreditText: "10", TotalCreditText: "20", TasksText: "",
            StatusKind: ProjectStatusKind.Active, StatusText: "s", SortKey: ParentSortKey(url));
        return new ProjectRow(data.Key, data);
    }

    // The Project-name text of each REALIZED DataGridRow, ordered by on-screen Y (visual order,
    // NOT DataGridRow.Index — the whole question is whether the visual follows the collection).
    private static List<string?> RenderedProjectNames(ProjectsView view) =>
        view.Grid.GetVisualDescendants().OfType<DataGridRow>()
            .OrderBy(r => r.Bounds.Y)
            .Select(r => VisualTree.FindInVisualTree<TextBlock>(r, t => t.Classes.Contains("projectName"))?.Text)
            .ToList();

    // ROOT CAUSE of "Projects can't sort" (2026-07-15): a header-click sort reorders the Rows
    // COLLECTION through a reconciler Move op, but Avalonia 12.1's DataGrid does not visually
    // reflect a CollectionChanged.Move — the rendered rows stayed put while every VM/F# test (all
    // of which inspect only collection order) passed. Sorting is the FIRST feature that ever
    // reorders rows: before it, compute was always RAC-desc and expand/collapse only inserted or
    // removed in place, so the Move path never reached the real DataGrid. This drives the reorder
    // through the ACTUAL reconciler (CollectionReconciler.Apply on a real diff that yields a Move)
    // and asserts the RENDERED order — the layer the collection-only tests could not see. Red
    // before the applier translated Move to Remove+Insert; green after.
    [AvaloniaFact]
    public void A_reconciler_move_reorders_the_rendered_grid_not_just_the_collection()
    {
        var (window, view, vm) = MakeView();
        window.Show();
        vm.Rows.Add(NamedParent("Alpha", "u-a"));
        vm.Rows.Add(NamedParent("Beta", "u-b"));
        Layout(window);
        Assert.Equal(new string?[] { "Alpha", "Beta" }, RenderedProjectNames(view));

        // The exact call ProjectsViewModel.Rebuild makes: diff current vs a swapped target. With two
        // surviving keys in flipped order the diff emits a single Move — the op the DataGrid ignored.
        var existing = vm.Rows.Select(h => (h.Key, h.Data)).ToArray();
        var target = new[] { (vm.Rows[1].Key, vm.Rows[1].Data), (vm.Rows[0].Key, vm.Rows[0].Data) };
        CollectionReconciler.Apply(vm.Rows, Reconcile.diff(existing, target), (k, r) => new ProjectRow(k, r));
        Layout(window);
        Dispatcher.UIThread.RunJobs();
        Layout(window);

        Assert.Equal(new string?[] { "Beta", "Alpha" }, RenderedProjectNames(view));
        window.Close();
    }

    [AvaloniaFact]
    public void Parent_and_child_rows_carry_their_hierarchy_classes()
    {
        var (window, view, vm) = MakeView();
        window.Show();
        vm.Rows.Add(ParentHolder(ProjectStatusKind.Active, Strings.ProjectsStatusActiveAll, showChevron: true));
        vm.Rows.Add(ChildHolder("host-a"));
        Layout(window);

        var parent = VisualTree.FindRow(view.Grid, 0);
        var child = VisualTree.FindRow(view.Grid, 1);
        Assert.Contains("projectParent", parent.Classes);
        Assert.DoesNotContain("projectChild", parent.Classes);
        Assert.Contains("projectChild", child.Classes);
        Assert.DoesNotContain("projectParent", child.Classes);
        window.Close();
    }

    [AvaloniaFact]
    public void Parent_and_child_rows_render_design_heights()
    {
        var (window, view, vm) = MakeView();
        window.Show();
        vm.Rows.Add(ParentHolder(ProjectStatusKind.Active, Strings.ProjectsStatusActiveAll, showChevron: true));
        vm.Rows.Add(ChildHolder("host-a"));
        Layout(window);

        var parent = VisualTree.FindRow(view.Grid, 0);
        var child = VisualTree.FindRow(view.Grid, 1);
        Assert.Equal(40, parent.Bounds.Height, precision: 0);
        Assert.Equal(32, child.Bounds.Height, precision: 0);
        window.Close();
    }

    // The chevron ToggleButton drives ToggleExpandCommand through a
    // $parent[UserControl] typed-DataContext binding (the compiled-binding risk in
    // this XAML), and its IsChecked follows Data.IsExpanded OneWay (the command
    // owns the state). Pins both the resolved command wiring and the model-driven
    // chevron flip without depending on DataGrid cell hit-testing.
    [AvaloniaFact]
    public void Chevron_is_wired_to_toggle_expand_and_tracks_the_expanded_flag()
    {
        var (window, view, vm) = MakeView();
        window.Show();
        var holder = ParentHolder(ProjectStatusKind.Active, Strings.ProjectsStatusActiveAll, showChevron: true);
        vm.Rows.Add(holder);
        Layout(window);

        var chevron = VisualTree.FindInVisualTree<ToggleButton>(
            VisualTree.FindRow(view.Grid, 0), t => t.Classes.Contains("chevron"));
        Assert.NotNull(chevron);
        Assert.True(chevron!.IsVisible);
        Assert.False(chevron.IsChecked);
        Assert.Same(vm.ToggleExpandCommand, chevron.Command);
        Assert.Equal(Url, chevron.CommandParameter);

        // Expansion is a model fact; the OneWay binding flips the chevron with it.
        holder.Data = holder.Data with { IsExpanded = true };
        Layout(window);
        Assert.True(chevron.IsChecked, "the chevron reflects the now-expanded parent");
        window.Close();
    }

    // In-place status swap (holder.Data replaced, same Key) must repaint the
    // status cell without the row leaving the grid — the row-liveness regression
    // stamped from Tasks' at-risk-in-place test. Manual rows, no Rebuild.
    [AvaloniaFact]
    public void In_place_status_change_updates_the_cell_without_reloading_the_row()
    {
        var (window, view, vm) = MakeView();
        window.Show();
        var holder = ParentHolder(ProjectStatusKind.Active, Strings.ProjectsStatusActiveAll);
        vm.Rows.Add(holder);
        Layout(window);

        var dataGridRow = VisualTree.FindRow(view.Grid, 0);
        Assert.Contains(Strings.ProjectsStatusActiveAll, TextsIn(dataGridRow));

        var suspendedAll = string.Format(Strings.ProjectsStatusAllFmt, Strings.ProjectsStatusSuspended);
        holder.Data = holder.Data with { StatusKind = ProjectStatusKind.Suspended, StatusText = suspendedAll };
        Layout(window);

        // Same realized DataGridRow instance ⇒ no remove+reinsert, so LoadingRow
        // never re-fired; the cell text tracked the holder's Data swap instead.
        Assert.Same(dataGridRow, VisualTree.FindRow(view.Grid, 0));
        Assert.DoesNotContain(Strings.ProjectsStatusActiveAll, TextsIn(dataGridRow));
        Assert.Contains(suspendedAll, TextsIn(dataGridRow));
        window.Close();
    }

    // Visual defect (design 2a): an EXPANDED chevron must stay a bare rotating glyph, never a
    // filled accent surface. The FluentAvalonia ToggleButton ControlTheme paints its checked
    // background onto the template part ContentPresenter#PART_ContentPresenter via a
    // "/template/" setter bound to the accent ToggleButtonBackgroundChecked key — NOT via the
    // Background property — so the chevron's local Background="Transparent" (template-bound only
    // in the default state) loses, and the theme overlay shows through. Probe the actual painted
    // part, not a property read, and compare against the resolved accent so the assertion bites.
    [AvaloniaFact]
    public void Expanded_chevron_shows_no_accent_background_fill()
    {
        var (window, view, vm) = MakeView();
        window.Show();
        vm.Rows.Add(ParentHolder(
            ProjectStatusKind.Active, Strings.ProjectsStatusActiveAll,
            showChevron: true, isExpanded: true));
        Layout(window);

        var chevron = VisualTree.FindInVisualTree<ToggleButton>(
            VisualTree.FindRow(view.Grid, 0), t => t.Classes.Contains("chevron"));
        Assert.NotNull(chevron);
        Assert.True(chevron!.IsChecked, "the expanded parent's chevron is checked");

        var part = VisualTree.FindInVisualTree<ContentPresenter>(
            chevron, cp => cp.Name == "PART_ContentPresenter");
        Assert.NotNull(part);

        // The accent overlay the FA theme would otherwise paint — resolved so the comparison
        // is meaningful rather than an unconditioned "is it transparent" read.
        Assert.True(chevron.TryFindResource("ToggleButtonBackgroundChecked", out var accentObj));
        var accent = Assert.IsAssignableFrom<ISolidColorBrush>(accentObj);
        Assert.NotEqual((byte)0, accent.Color.A); // sanity: the overlay is opaque, so red != green

        var bg = part!.Background;
        var isTransparent = bg is null || (bg is ISolidColorBrush scb && scb.Color.A == 0);
        Assert.True(isTransparent, $"expanded chevron must have no background fill, was {bg}");
        Assert.False(
            bg is ISolidColorBrush accentPaint && accentPaint.Color == accent.Color,
            "chevron background must not be the accent overlay");
        window.Close();
    }

    // Anti-vacuity baseline for the chevron probe above. That test only bites if the Fluent
    // ToggleButton theme actually paints ToggleButtonBackgroundChecked onto PART_ContentPresenter
    // in this headless setup — a chevron carrying no paint at all would otherwise be a false
    // green. This pins that baseline: a plain checked ToggleButton's PART_ContentPresenter DOES
    // carry the resolved accent brush. It does NOT test scoping — the chevron de-tint lives in
    // ProjectsView's UserControl.Styles and is structurally confined to that subtree, so it could
    // never reach a ToggleButton built in this separate window regardless of the .chevron class.
    [AvaloniaFact]
    public void Fluent_toggle_button_paints_its_checked_accent_on_the_content_presenter()
    {
        var toggle = new ToggleButton { IsChecked = true, Content = "x" };
        var window = new Window { Width = 200, Height = 100, Content = toggle };
        window.Show();
        Layout(window);

        var part = VisualTree.FindInVisualTree<ContentPresenter>(
            toggle, cp => cp.Name == "PART_ContentPresenter");
        Assert.NotNull(part);
        Assert.True(toggle.TryFindResource("ToggleButtonBackgroundChecked", out var accentObj));
        var accent = Assert.IsAssignableFrom<ISolidColorBrush>(accentObj);

        var bg = Assert.IsAssignableFrom<ISolidColorBrush>(part!.Background);
        Assert.Equal(accent.Color, bg.Color); // the theme really does tint the part in headless
        window.Close();
    }

    // The expanded chevron's PathIcon at row <paramref name="rowIndex"/>.
    private static PathIcon ChevronGlyph(ProjectsView view, int rowIndex)
    {
        var chevron = VisualTree.FindInVisualTree<ToggleButton>(
            VisualTree.FindRow(view.Grid, rowIndex), t => t.Classes.Contains("chevron"));
        Assert.NotNull(chevron);
        var glyph = VisualTree.FindInVisualTree<PathIcon>(chevron!);
        Assert.NotNull(glyph);
        return glyph!;
    }

    // The glyph's effective (possibly inherited) foreground color.
    private static Color GlyphColor(PathIcon glyph) =>
        Assert.IsAssignableFrom<ISolidColorBrush>(glyph.Foreground).Color;

    // Visibility regression (this branch): the accent-background de-tint (commit 358d264) fixed the
    // checked-state Background, but the Fluent ToggleButton checked visual state ALSO swaps the
    // Foreground to an on-accent (light) color meant to contrast the accent fill. The chevron's
    // PathIcon carries no local Foreground, so in the EXPANDED (checked) state it INHERITS that light
    // color and the glyph vanishes against the light row. Pin the glyph to the neutral token (a local
    // value, highest precedence) so it renders the same VISIBLE neutral in both states — only the
    // rotation differs. Probe the effective glyph foreground, and encode the "identical in both
    // states" design invariant by comparing expanded vs collapsed.
    [AvaloniaFact]
    public void Expanded_chevron_glyph_stays_the_visible_neutral_color()
    {
        var (window, view, vm) = MakeView();
        window.Show();
        vm.Rows.Add(ParentHolder(
            ProjectStatusKind.Active, Strings.ProjectsStatusActiveAll,
            showChevron: true, isExpanded: true, url: "http://expanded/"));   // checked chevron
        vm.Rows.Add(ParentHolder(
            ProjectStatusKind.Active, Strings.ProjectsStatusActiveAll,
            showChevron: true, isExpanded: false, url: "http://collapsed/")); // unchecked chevron
        Layout(window);

        var expandedGlyph = ChevronGlyph(view, 0);
        var collapsedGlyph = ChevronGlyph(view, 1);

        Assert.True(Application.Current!.TryGetResource(
            "LatticeNeutralFgBrush", window.ActualThemeVariant, out var neutralObj));
        var neutral = ((ISolidColorBrush)neutralObj!).Color;

        var expandedColor = GlyphColor(expandedGlyph);
        var collapsedColor = GlyphColor(collapsedGlyph);

        // The expanded glyph is the visible neutral, NOT the light on-accent checked foreground.
        Assert.Equal(neutral, expandedColor);
        // Design invariant: glyph color is identical collapsed vs expanded (only rotation differs).
        Assert.Equal(collapsedColor, expandedColor);
        window.Close();
    }

    // Chevron gutter column (index 0, width 24) must carry no divider anywhere: the body
    // cells opt out via CellStyleClasses="noDivider" (shared DataGridStyles mechanism, already
    // verified elsewhere), and the header's vertical separator — which has no per-column
    // style-class hook — is targeted positionally (chevron is always the first column).
    [AvaloniaFact]
    public void Chevron_gutter_column_has_no_body_divider_and_no_header_separator()
    {
        var (window, view, vm) = MakeView();
        window.Show();
        vm.Rows.Add(ParentHolder(ProjectStatusKind.Active, Strings.ProjectsStatusActiveAll, showChevron: true));
        Layout(window);
        var grid = view.Grid;

        var gutterCells = grid.GetVisualDescendants().OfType<DataGridCell>()
            .Where(c => c.Classes.Contains("noDivider")).ToList();
        Assert.NotEmpty(gutterCells);
        foreach (var c in gutterCells)
        {
            var line = VisualTree.FindInVisualTree<Rectangle>(c, r => r.Name == "PART_RightGridLine");
            Assert.NotNull(line);
            Assert.Equal(0d, line!.Width);
        }

        // DataGridColumnHeader.OwningColumn is internal (no InternalsVisibleTo into this test
        // assembly), so identify the real, realized column headers positionally: the template
        // also instantiates a corner header and a filler-column header, both unarranged (zero
        // width) in this layout, so filtering to Width > 0 isolates the 7 user columns, ordered
        // left-to-right by X — the same "positional, chevron is always first" contract the fix
        // itself relies on.
        var headers = grid.GetVisualDescendants().OfType<DataGridColumnHeader>()
            .Where(h => h.Bounds.Width > 0)
            .OrderBy(h => h.Bounds.X).ToList();
        Assert.False(headers[0].AreSeparatorsVisible);  // chevron gutter: no header separator
        Assert.True(headers[1].AreSeparatorsVisible);   // Project column: keeps its separator
        window.Close();
    }

    // Regression lock for the design-spec column widths (chevron 24 / Project 200 / Hosts 110 /
    // Resource share 140 / Avg credit 100 / Total credit 110 / Status *).
    [AvaloniaFact]
    public void Projects_default_column_widths_match_the_spec()
    {
        var (window, view, _) = MakeView();
        window.Show();
        Layout(window);
        var grid = view.Grid;
        double W(int i) => grid.Columns[i].Width.Value;
        Assert.Equal(24, W(0)); // chevron
        Assert.Equal(200, W(1)); // Project
        Assert.Equal(110, W(2)); // Hosts
        Assert.Equal(140, W(3)); // Resource share
        Assert.Equal(100, W(4)); // Avg credit
        Assert.Equal(110, W(5)); // Total credit
        Assert.False(grid.Columns[6].Width.IsStar); // Status fixed (not a star — Finding A)
        Assert.Equal(300, W(6)); // Status
        window.Close();
    }

    [AvaloniaFact]
    public void Hosts_column_hides_when_scope_is_a_single_host()
    {
        // Two registered hosts so the aggregate baseline genuinely shows the
        // Hosts column (multi-host presentation); scoping to a single host must
        // then hide it. Post-ScopeMachine the column keys on >1 host, not on the
        // AllHosts scope alone, so a one-host baseline would never show it.
        var (window, _, vm) = MakeView(hostCount: 2);
        window.Show();
        Layout(window);
        Assert.Contains(Strings.ProjectsColHosts, VisibleHeaders(window));

        vm.Scope = new ScopeSelection(Guid.NewGuid());
        Layout(window);

        Assert.DoesNotContain(Strings.ProjectsColHosts, VisibleHeaders(window));
        window.Close();
    }

    // Teardown-drain: navigating away discards ProjectsView through the shell's
    // ContentControl WITHOUT touching Grid.ItemsSource, so UnloadingRow never
    // fires — the RowClassBinder's DetachedFromVisualTree drain is the only leg
    // that clears the subscriptions. Pins that the view attaches the binder to
    // the right grid.
    [AvaloniaFact]
    public void Detaching_the_view_drains_all_row_class_subscriptions()
    {
        var (window, view, vm) = MakeView();
        window.Show();
        vm.Rows.Add(ParentHolder(ProjectStatusKind.Active, Strings.ProjectsStatusActiveAll));
        Layout(window);
        Assert.True(view.RowSubscriptionCount > 0, "a realized row should have subscribed");

        window.Content = null;
        Layout(window);

        Assert.Equal(0, view.RowSubscriptionCount);
        window.Close();
    }

    // A REAL header click (not Column.Sort) must reach OnGridSorting, which cancels the DataGrid's
    // flat sort and maps the column Tag to a ProjectSortColumn for the VM. The overlays are turned
    // off first: the loading/empty Borders cover the WHOLE grid including the header row, so they
    // eat header clicks — a real sort-blocker whenever Projects is loading or empty (see
    // Header_click_is_blocked_while_the_loading_overlay_covers_the_grid). The reorder itself +
    // children-follow is covered by ProjectsViewModelTests; hand-built rows only realize the header.
    [AvaloniaFact]
    public void A_real_click_on_the_Project_header_sorts_via_the_cancel_route()
    {
        var (window, view, vm) = MakeView(hostCount: 2);
        window.Show();
        vm.Rows.Add(ParentHolder(ProjectStatusKind.Active, "s", showChevron: true, url: "u-a"));
        vm.Rows.Add(ParentHolder(ProjectStatusKind.Active, "s", showChevron: true, url: "u-b"));
        vm.IsLoading = false;
        vm.IsEmpty = false;
        Layout(window);
        Assert.Null(vm.SortState.Column);

        var header = view.Grid.GetVisualDescendants().OfType<DataGridColumnHeader>()
            .Single(h => (h.Content as string) == Strings.ColProject);
        var pt = header.TranslatePoint(new Point(header.Bounds.Width / 2, header.Bounds.Height / 2), window)!.Value;
        window.MouseMove(pt, RawInputModifiers.None);
        window.MouseDown(pt, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(pt, MouseButton.Left, RawInputModifiers.None);
        Layout(window);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(ProjectSortColumn.ByName, vm.SortState.Column);
        Assert.False(vm.SortState.Descending); // first click = ascending
        window.Close();
    }

    // Regression pin for the sort-blocker: the loading overlay is an opaque Border layered OVER the
    // grid, so while it is up a header click never reaches the header (no Sorting raised). This is
    // why "Projects can't be sorted" while it is still loading. Fixing it means the overlay must not
    // cover the header row.
    [AvaloniaFact]
    public void Header_click_is_blocked_while_the_loading_overlay_covers_the_grid()
    {
        var (window, view, vm) = MakeView(hostCount: 2);
        window.Show();
        vm.Rows.Add(ParentHolder(ProjectStatusKind.Active, "s", showChevron: true, url: "u-a"));
        vm.Rows.Add(ParentHolder(ProjectStatusKind.Active, "s", showChevron: true, url: "u-b"));
        vm.IsEmpty = false;
        vm.IsLoading = true; // overlay up
        Layout(window);

        var header = view.Grid.GetVisualDescendants().OfType<DataGridColumnHeader>()
            .Single(h => (h.Content as string) == Strings.ColProject);
        var pt = header.TranslatePoint(new Point(header.Bounds.Width / 2, header.Bounds.Height / 2), window)!.Value;
        window.MouseMove(pt, RawInputModifiers.None);
        window.MouseDown(pt, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(pt, MouseButton.Left, RawInputModifiers.None);
        Layout(window);
        Dispatcher.UIThread.RunJobs();

        // The click was swallowed by the overlay — no sort. (Documents the bug; see the real-click
        // test above for the working steady-state path.)
        Assert.Null(vm.SortState.Column);
        window.Close();
    }
}
