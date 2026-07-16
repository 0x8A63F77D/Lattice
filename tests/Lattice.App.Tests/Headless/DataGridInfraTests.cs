using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using static Lattice.Tests.HeadlessLayout;

namespace Lattice.App.Tests.Headless;

public class DataGridInfraTests
{
    private class Row
    {
        public string Name { get; set; } = "Test";
        public int Value { get; set; } = 42;
    }

    // Mirrors the RowHolder<TKey,TRow> shape TasksView binds against post-
    // retrofit (issue #24): the DataGrid column's Binding path is nested
    // ("Data.Project"), not a direct property of the ItemsSource element.
    private class NestedRow(string project)
    {
        public InnerData Data { get; } = new(project);
    }

    private class InnerData(string project)
    {
        public string Project { get; } = project;
    }

    private static DataGrid MakeGrid() => new()
    {
        ItemsSource = new[] { new Row(), new Row() },
        Columns =
        {
            new DataGridTextColumn { Header = "Name", Binding = new Avalonia.Data.Binding("Name") },
            new DataGridTextColumn { Header = "Value", Binding = new Avalonia.Data.Binding("Value") }
        }
    };

    // A themed DataGrid carrying the shared "lattice" class with two fixed-width
    // columns, so a resize drag produces a measurable width delta.
    private static DataGrid MakeLatticeGrid(string extraClasses = "")
    {
        var grid = new DataGrid
        {
            ItemsSource = new[] { new Row(), new Row() },
            Columns =
            {
                new DataGridTextColumn { Header = "Name", Binding = new Avalonia.Data.Binding("Name"), Width = new DataGridLength(120) },
                new DataGridTextColumn { Header = "Value", Binding = new Avalonia.Data.Binding("Value"), Width = new DataGridLength(120) },
            },
        };
        grid.Classes.Add("lattice");
        foreach (var c in extraClasses.Split(' ', System.StringSplitOptions.RemoveEmptyEntries))
            grid.Classes.Add(c);
        return grid;
    }

    // A lattice grid whose FIRST column is a gutter marked .noDivider, then two normal
    // columns + a star column (filler inactive, mirroring the real views). Column 0's
    // PART_RightGridLine must collapse to zero width.
    private static DataGrid MakeGutterGrid()
    {
        var grid = new DataGrid
        {
            ItemsSource = new[] { new Row(), new Row() },
            Columns =
            {
                new DataGridTextColumn { Header = "G", Binding = new Avalonia.Data.Binding("Value"),
                                         Width = new DataGridLength(24), CellStyleClasses = { "noDivider" } },
                new DataGridTextColumn { Header = "Name", Binding = new Avalonia.Data.Binding("Name"), Width = new DataGridLength(120) },
                new DataGridTextColumn { Header = "V2", Binding = new Avalonia.Data.Binding("Value"), Width = new DataGridLength(120) },
                new DataGridTextColumn { Header = "Star", Binding = new Avalonia.Data.Binding("Name"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) },
            },
        };
        grid.Classes.Add("lattice");
        return grid;
    }

    private static Window ShowInWindow(Control content)
    {
        var window = new Window
        {
            Width = 400,
            Height = 300,
            Content = content
        };
        window.Show();
        return window;
    }

    private static Window ShowInWindow(Control content, double width, double height)
    {
        var window = new Window { Width = width, Height = height, Content = content };
        window.Show();
        return window;
    }

    // Finding A fixture: a lattice grid of ALL fixed-width columns and NO star, mirroring the real
    // views after the round-2 fix. A star (*) column makes the DataGrid fit-to-width — it pins the
    // grid total to the viewport, so it never overflows: horizontal scroll never engages and
    // dragging a column just reshuffles space between columns ("总宽度限制死了…列在互相共享窗口宽度").
    // Fixed columns instead let the total exceed the viewport, so the grid overflows into a working
    // h-scrollbar; when they fit, the DataGridFillerColumn takes the trailing slack (no scrollbar).
    private static DataGrid MakeFixedGrid()
    {
        var grid = new DataGrid
        {
            ItemsSource = new[] { new Row(), new Row() },
            Columns =
            {
                new DataGridTextColumn { Header = "A", Binding = new Avalonia.Data.Binding("Name"),  Width = new DataGridLength(120), MinWidth = 120 },
                new DataGridTextColumn { Header = "B", Binding = new Avalonia.Data.Binding("Value"), Width = new DataGridLength(120), MinWidth = 120 },
                new DataGridTextColumn { Header = "C", Binding = new Avalonia.Data.Binding("Name"),  Width = new DataGridLength(120), MinWidth = 120 },
                new DataGridTextColumn { Header = "D", Binding = new Avalonia.Data.Binding("Value"), Width = new DataGridLength(120), MinWidth = 120 },
            },
        };
        grid.Classes.Add("lattice");
        return grid;
    }

    private static ScrollBar? HorizontalScrollBar(DataGrid grid) =>
        grid.GetVisualDescendants().OfType<ScrollBar>()
            .FirstOrDefault(b => b.Name == "PART_HorizontalScrollbar");

    // (i) When the window is narrower than the sum of the fixed column widths, the columns hold
    // their widths (they do NOT shrink to fit) and the grid OVERFLOWS into a horizontal scrollbar.
    // A star column could never do this — it would absorb the shortfall and pin the total to the
    // viewport, so no scrollbar ever appeared (owner Finding A).
    [AvaloniaFact]
    public void Fixed_columns_overflow_a_too_narrow_window_into_a_scrollbar()
    {
        var grid = MakeFixedGrid();
        // Total 120*4 = 480, wider than this window: the grid must overflow, not shrink columns.
        var window = ShowInWindow(grid, 360, 300);
        Layout(window);

        Assert.Equal(120d, grid.Columns[0].ActualWidth);
        Assert.Equal(120d, grid.Columns[1].ActualWidth);
        Assert.Equal(120d, grid.Columns[2].ActualWidth);
        Assert.Equal(120d, grid.Columns[3].ActualWidth);

        var hbar = HorizontalScrollBar(grid);
        Assert.NotNull(hbar);
        Assert.True(hbar!.IsVisible, "overflowing fixed columns must surface the horizontal scrollbar");
        Assert.True(hbar.Maximum > 0, "the scrollbar must have a real scroll range");
        window.Close();
    }

    // (ii) That scrollbar is a WORKING one: a wheel with a horizontal delta moves the offset. This
    // is the half the owner reported broken ("左右的滚动是不可用的"). DataGrid.UpdateScroll computes
    // offset -= delta.X, so a negative X scrolls right.
    [AvaloniaFact]
    public void The_overflow_horizontal_scrollbar_moves_under_a_wheel_delta()
    {
        var grid = MakeFixedGrid(); // total 480 > 360px viewport -> overflow
        var window = ShowInWindow(grid, 360, 300);
        Layout(window);

        var hbar = HorizontalScrollBar(grid);
        Assert.NotNull(hbar);
        Assert.True(hbar!.IsVisible, "the tight grid must show a horizontal scrollbar");
        Assert.True(hbar.Maximum > 0, "the scrollbar must have a real scroll range");

        var mid = grid.TranslatePoint(new Point(grid.Bounds.Width / 2, grid.Bounds.Height / 2), window)!.Value;
        var startValue = hbar.Value;
        window.MouseWheel(mid, new Vector(-4, 0), RawInputModifiers.None);
        Layout(window);
        Assert.True(hbar.Value > startValue,
            $"a horizontal wheel delta should move the offset: start={startValue}, now={hbar.Value}");
        window.Close();
    }

    // (iii) The owner's core complaint: a truncated column could not be dragged wider to reveal its
    // content ("有的标题明明没完全显示出来我拉不动"). With a star column, dragging just reshuffled
    // space and clamped once the star hit its min. With all-fixed columns, dragging a column wider
    // pushes the grid total PAST the viewport, so it overflows into a working h-scrollbar — the
    // content is revealed and scrollable. Starts fitting (no scrollbar), ends overflowing.
    [AvaloniaFact]
    public void Dragging_a_fixed_column_wider_overflows_into_a_working_scrollbar()
    {
        var grid = MakeFixedGrid(); // total 480, fits the 700px window with slack -> no scrollbar
        var window = ShowInWindow(grid, 700, 300);
        Layout(window);

        var before = HorizontalScrollBar(grid);
        Assert.NotNull(before);
        Assert.False(before!.IsVisible, "with slack the fixed columns fit and there is no h-scrollbar");

        var header = grid.GetVisualDescendants().OfType<DataGridColumnHeader>()
            .Single(h => (h.Content as string) == "A");
        var edge = header.TranslatePoint(new Point(header.Bounds.Width - 2, header.Bounds.Height / 2), window)!.Value;
        var target = edge.WithX(edge.X + 400); // drag A far wider than the slack
        window.MouseMove(edge, RawInputModifiers.None);
        window.MouseDown(edge, MouseButton.Left, RawInputModifiers.None);
        window.MouseMove(target, RawInputModifiers.None);
        window.MouseUp(target, MouseButton.Left, RawInputModifiers.None);
        Layout(window);

        Assert.True(grid.Columns[0].ActualWidth > 300, "column A should have widened well past its start");
        Assert.Equal(120d, grid.Columns[1].ActualWidth); // neighbours unchanged (not reshuffled)
        Assert.Equal(120d, grid.Columns[2].ActualWidth);
        Assert.Equal(120d, grid.Columns[3].ActualWidth);

        var hbar = HorizontalScrollBar(grid);
        Assert.NotNull(hbar);
        Assert.True(hbar!.IsVisible, "dragging a column wider than the viewport must surface the scrollbar");
        Assert.True(hbar.Maximum > 0, "the scrollbar must have a real scroll range");

        var mid = grid.TranslatePoint(new Point(grid.Bounds.Width / 2, grid.Bounds.Height / 2), window)!.Value;
        var startValue = hbar.Value;
        window.MouseWheel(mid, new Vector(-4, 0), RawInputModifiers.None);
        Layout(window);
        Assert.True(hbar.Value > startValue, "the revealed scrollbar must scroll under a wheel delta");
        window.Close();
    }

    // Finding C: the Fluent DataGridColumnHeader template reserves a fixed 32px sort-icon slot
    // (ColumnDefinition Width="Auto" MinWidth="{DynamicResource DataGridSortIconMinWidth}") on EVERY
    // header, even while the icon is hidden. In a narrow column (Tasks' Elapsed 68 / Remaining 74)
    // that slot plus the 8,0 cell padding leaves the label ~20px, so it truncates with the blank
    // reserved slot to its right (owner Finding C). The shared style zeroes DataGridSortIconMinWidth
    // so the label uses the real column width; the icon still appears (Auto-sized) when sorted.
    // Probe: the header's PART_ContentPresenter fills its `*` grid slot, so its width == column width
    // minus padding minus the sort reservation. At 68px it must clear the label, not sit at ~20px.
    [AvaloniaFact]
    public void Narrow_sortable_header_gives_its_label_the_full_column_width()
    {
        var grid = new DataGrid
        {
            CanUserSortColumns = true,
            ItemsSource = new[] { new Row(), new Row() },
            Columns = { new DataGridTextColumn { Header = "Elapsed",
                        Binding = new Avalonia.Data.Binding("Value"), Width = new DataGridLength(68) } },
        };
        grid.Classes.Add("lattice");
        var window = ShowInWindow(grid);
        Layout(window);

        var header = grid.GetVisualDescendants().OfType<DataGridColumnHeader>()
            .Single(h => (h.Content as string) == "Elapsed");
        var cp = VisualTree.FindInVisualTree<ContentPresenter>(header, c => c.Name == "PART_ContentPresenter");
        Assert.NotNull(cp);
        // 68 - 16 padding - 0 sort reservation = 52. With the theme's default 32px slot it would be ~20.
        Assert.True(cp!.Bounds.Width >= 45,
            $"Elapsed header content should get the column width, not the sort-icon slot: width={cp.Bounds.Width}");
        window.Close();
    }

    // GridLinesVisibility=All draws the horizontal rule ADDITIVELY below a RowHeight-sized row,
    // which would push a lattice row's measured height to 37 (36 + 1px). The shared style pins an
    // explicit DataGridRow.Height so the rule sits INSIDE the spec pitch (box-sizing:border-box in
    // the mockup) and the row stays at exactly 36px. Removing the pin regresses this to 37.
    [AvaloniaFact]
    public void Lattice_row_height_absorbs_the_horizontal_rule_and_stays_at_spec_pitch()
    {
        var grid = MakeLatticeGrid();
        var window = ShowInWindow(grid);
        Layout(window);

        Assert.Equal(Avalonia.Controls.DataGridGridLinesVisibility.All, grid.GridLinesVisibility);
        var row = grid.GetVisualDescendants().OfType<DataGridRow>().First();
        Assert.Equal(36, (int)row.Bounds.Height);
        window.Close();
    }

    [AvaloniaFact]
    public void DataGrid_with_themed_window_renders_and_header_style_resolves()
    {
        var dataGrid = MakeGrid();
        ShowInWindow(dataGrid);

        // Verify the DataGrid actually materialized rows in the visual tree
        var row = VisualTree.FindInVisualTree<DataGridRow>(dataGrid);
        Assert.NotNull(row);

        // Find a DataGridColumnHeader in the visual tree
        var header = VisualTree.FindInVisualTree<DataGridColumnHeader>(dataGrid);
        Assert.NotNull(header);

        // Assert the Lattice header style resolved: FontSize 11
        Assert.Equal(11, header.FontSize);
    }

    [AvaloniaFact]
    public void Selected_row_paints_Lattice_selected_tint_at_full_opacity()
    {
        var dataGrid = MakeGrid();
        var window = ShowInWindow(dataGrid);

        dataGrid.SelectedIndex = 0;

        var row = VisualTree.FindInVisualTree<DataGridRow>(dataGrid, r => r.IsSelected);
        Assert.NotNull(row);

        // The Fluent DataGrid theme paints selection via Rectangle#BackgroundRectangle
        // inside the row template (over the row Background), so the tint must land there.
        var rect = VisualTree.FindInVisualTree<Rectangle>(row, r => r.Name == "BackgroundRectangle");
        Assert.NotNull(rect);

        Assert.True(Application.Current!.TryGetResource(
            "LatticeSelectedTintBrush", window.ActualThemeVariant, out var expected));
        var expectedColor = ((SolidColorBrush)expected!).Color;

        var fill = Assert.IsAssignableFrom<ISolidColorBrush>(rect.Fill);
        Assert.Equal(expectedColor, fill.Color);
        Assert.Equal(1.0, rect.Opacity);
    }

    [AvaloniaFact]
    public void Numeric_TextBlock_class_applies_tabular_numeral_font_features()
    {
        var numeric = new TextBlock { Text = "0123456789", Classes = { "numeric" } };
        ShowInWindow(numeric);

        // TextBlock.numeric must carry OpenType tabular-numeral features (+tnum)
        Assert.NotNull(numeric.FontFeatures);
        Assert.NotEmpty(numeric.FontFeatures);
    }

    // Task 7 sorting check (issue #24 Tasks retrofit): TasksView's columns
    // retarget to nested "Data.X" binding paths after Rows became
    // RowHolder<TaskRowKey,TaskRowViewModel> instances. Avalonia's DataGrid
    // resolves DataGridColumn.GetSortPropertyName() from the raw Binding.Path
    // string and evaluates it via TypeHelper.GetNestedPropertyValue, which
    // splits on '.' — nested paths sort correctly with no SortMemberPath
    // override needed. Verified empirically here (not just from docs) because
    // a silently-broken sort would be an invisible regression in the real view.
    [AvaloniaFact]
    public void Sorting_by_a_nested_binding_path_orders_rows()
    {
        var dataGrid = new DataGrid
        {
            ItemsSource = new[] { new NestedRow("charlie"), new NestedRow("alpha"), new NestedRow("bravo") },
            Columns = { new DataGridTextColumn { Header = "Project", Binding = new Avalonia.Data.Binding("Data.Project") } },
        };
        var window = ShowInWindow(dataGrid);
        window.Measure(new Size(400, 300));
        window.Arrange(new Rect(0, 0, 400, 300));
        Dispatcher.UIThread.RunJobs();

        // Sort() needs the header cell realized (it forwards through
        // DataGridColumnHeader.InvokeProcessSort), which the layout pass above provides.
        dataGrid.Columns.Single().Sort(ListSortDirection.Ascending);
        Dispatcher.UIThread.RunJobs();
        window.Measure(new Size(400, 300));
        window.Arrange(new Rect(0, 0, 400, 300));

        var orderedProjects = dataGrid.GetVisualDescendants().OfType<DataGridRow>()
            .OrderBy(r => r.Index)
            .Select(r => ((NestedRow)r.DataContext!).Data.Project)
            .ToArray();

        Assert.Equal(["alpha", "bravo", "charlie"], orderedProjects);
    }

    // Negative control / trap documentation: Avalonia's DataGrid.CanUserResizeColumns
    // defaults to FALSE (WPF defaults it TRUE — the migration trap this fix targets).
    // A plain DataGrid with NO "lattice" class stays non-resizable; proven empirically,
    // not from docs. This test passes before and after the fix and stands as documentation.
    [AvaloniaFact]
    public void Plain_DataGrid_defaults_CanUserResizeColumns_to_false()
    {
        var grid = MakeGrid();
        var window = ShowInWindow(grid);
        Layout(window);

        Assert.False(grid.CanUserResizeColumns);
    }

    // The fix: the shared DataGrid.lattice style enables column resizing, proven by a real
    // interaction probe — Avalonia has NO resize-Thumb in the DataGridColumnHeader template
    // (resize is a 5px pointer hit-region at the header edge, gated by
    // DataGridColumn.ActualCanUserResize -> OwningGrid.CanUserResizeColumns), so the honest
    // geometry probe is an actual header-edge drag that widens the column. The column-widened
    // assertion is deliberately the SOLE failure point here: no pre-drag property assert to
    // short-circuit it, so the geometry probe carries its own red-first weight under mutation.
    // (The bare CanUserResizeColumns property gate is covered by the class-combination theory.)
    [AvaloniaFact]
    public void Lattice_DataGrid_enables_resize_and_header_edge_drag_widens_the_column()
    {
        var grid = MakeLatticeGrid();
        var window = ShowInWindow(grid);
        Layout(window);

        var firstColumn = grid.Columns[0];
        var startWidth = firstColumn.ActualWidth;

        var header = grid.GetVisualDescendants().OfType<DataGridColumnHeader>()
            .Single(h => (h.Content as string) == "Name");

        // The resize hit-region is the rightmost 5px of the header; press 2px inside the
        // right edge, drag right by 48px, and the column must grow.
        var edge = header.TranslatePoint(new Point(header.Bounds.Width - 2, header.Bounds.Height / 2), window)!.Value;
        var target = edge.WithX(edge.X + 48);
        window.MouseMove(edge, RawInputModifiers.None);
        window.MouseDown(edge, MouseButton.Left, RawInputModifiers.None);
        window.MouseMove(target, RawInputModifiers.None);
        window.MouseUp(target, MouseButton.Left, RawInputModifiers.None);
        Layout(window);

        Assert.True(
            firstColumn.ActualWidth > startWidth + 20,
            $"resize drag should widen the column: start={startWidth}, now={firstColumn.ActualWidth}");
    }

    // All four data views resolve to the same DataGrid.lattice style, but each mounts it
    // with a different class string ("lattice", "lattice compact", "lattice eventlog").
    // Verify the selector's resize setter lands under every real class combination so the
    // fix is confirmed for all four grids, not just the bare class.
    [AvaloniaTheory]
    [InlineData("")]           // Projects / EventLog base ("lattice")
    [InlineData("compact")]    // Tasks / Transfers density toggle ("lattice compact")
    [InlineData("eventlog")]   // EventLog ("lattice eventlog")
    public void Lattice_class_combinations_all_enable_column_resize(string extraClasses)
    {
        var grid = MakeLatticeGrid(extraClasses);
        var window = ShowInWindow(grid);
        Layout(window);

        Assert.True(grid.CanUserResizeColumns);
    }

    [AvaloniaFact]
    public void Lattice_grid_enables_gridlines_and_divider_brush_is_spec_color()
    {
        var grid = MakeLatticeGrid();
        var window = ShowInWindow(grid);
        Layout(window);

        Assert.Equal(Avalonia.Controls.DataGridGridLinesVisibility.All, grid.GridLinesVisibility);

        Assert.True(Application.Current!.TryGetResource(
            "LatticeGridDividerBrush", window.ActualThemeVariant, out var divider));
        var vfill = Assert.IsAssignableFrom<ISolidColorBrush>(grid.VerticalGridLinesBrush);
        Assert.Equal(((ISolidColorBrush)divider!).Color, vfill.Color);

        Assert.True(Application.Current!.TryGetResource(
            "LatticeStrokeSubtleBrush", window.ActualThemeVariant, out var rowRule));
        var hfill = Assert.IsAssignableFrom<ISolidColorBrush>(grid.HorizontalGridLinesBrush);
        Assert.Equal(((ISolidColorBrush)rowRule!).Color, hfill.Color);
        Assert.NotEqual(vfill.Color, hfill.Color);
        window.Close();
    }

    [AvaloniaFact]
    public void Header_bottom_rule_paints_the_stroke_not_the_system_gridline()
    {
        var grid = MakeLatticeGrid();
        var window = ShowInWindow(grid);
        Layout(window);

        var rule = VisualTree.FindInVisualTree<Rectangle>(grid, r => r.Name == "PART_ColumnHeadersAndRowsSeparator");
        Assert.NotNull(rule);
        Assert.True(Application.Current!.TryGetResource(
            "LatticeStrokeBrush", window.ActualThemeVariant, out var stroke));
        var fill = Assert.IsAssignableFrom<ISolidColorBrush>(rule!.Fill);
        Assert.Equal(((ISolidColorBrush)stroke!).Color, fill.Color);
        window.Close();
    }

    [AvaloniaFact]
    public void Gutter_cell_class_collapses_the_right_gridline_to_zero_width()
    {
        var grid = MakeGutterGrid();
        var window = ShowInWindow(grid);
        Layout(window);

        var gutterCells = grid.GetVisualDescendants().OfType<DataGridCell>()
            .Where(c => c.Classes.Contains("noDivider")).ToList();
        Assert.NotEmpty(gutterCells);
        foreach (var cell in gutterCells)
        {
            var line = VisualTree.FindInVisualTree<Rectangle>(cell, r => r.Name == "PART_RightGridLine");
            Assert.NotNull(line);
            Assert.Equal(0d, line!.Width);
        }

        // A normal (non-gutter) body cell keeps a 1px divider.
        var normalCell = grid.GetVisualDescendants().OfType<DataGridCell>()
            .First(c => !c.Classes.Contains("noDivider") && c.FindAncestorOfType<DataGridRow>() != null);
        var normalLine = VisualTree.FindInVisualTree<Rectangle>(normalCell, r => r.Name == "PART_RightGridLine");
        Assert.NotNull(normalLine);
        Assert.NotEqual(0d, normalLine!.Width);
        window.Close();
    }

    [AvaloniaFact]
    public void Header_separator_uses_the_divider_brush_and_header_text_is_secondary()
    {
        var grid = MakeLatticeGrid();
        var window = ShowInWindow(grid);
        Layout(window);

        var header = grid.GetVisualDescendants().OfType<DataGridColumnHeader>()
            .First(h => (h.Content as string) == "Name");
        Assert.True(Application.Current!.TryGetResource(
            "LatticeGridDividerBrush", window.ActualThemeVariant, out var divider));
        var sep = Assert.IsAssignableFrom<ISolidColorBrush>(header.SeparatorBrush);
        Assert.Equal(((ISolidColorBrush)divider!).Color, sep.Color);

        Assert.True(Application.Current!.TryGetResource(
            "LatticeTextSecondaryBrush", window.ActualThemeVariant, out var fg));
        var hfg = Assert.IsAssignableFrom<ISolidColorBrush>(header.Foreground);
        Assert.Equal(((ISolidColorBrush)fg!).Color, hfg.Color);
        Assert.Equal(11d, header.FontSize);
        Assert.Equal(Avalonia.Media.FontWeight.SemiBold, header.FontWeight);
        window.Close();
    }

    [AvaloniaFact]
    public void Numeric_cell_class_applies_tabular_figures_to_its_textblock()
    {
        var grid = MakeLatticeGrid();
        grid.Columns[1].CellStyleClasses.Add("numericCell");
        var window = ShowInWindow(grid);
        Layout(window);

        var cell = grid.GetVisualDescendants().OfType<DataGridCell>()
            .First(c => c.Classes.Contains("numericCell"));
        var tb = VisualTree.FindInVisualTree<TextBlock>(cell);
        Assert.NotNull(tb);
        Assert.NotNull(tb!.FontFeatures);
        Assert.Contains(tb.FontFeatures!, f => f.Tag == "tnum" && f.Value == 1);
        window.Close();
    }

    private static DataGridRow BodyRow(DataGrid grid, int index) =>
        grid.GetVisualDescendants().OfType<DataGridRow>().Single(r => r.Index == index);

    [AvaloniaFact]
    public void Hovering_a_plain_row_paints_the_hover_brush()
    {
        var grid = MakeLatticeGrid();
        var window = ShowInWindow(grid);
        Layout(window);

        var row = BodyRow(grid, 0);
        // Hover applies instantly (no Background transition — see DataGridStyles.axaml), so the
        // synchronous read below is already the settled brush.
        var mid = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;
        window.MouseMove(mid, RawInputModifiers.None);
        Layout(window);

        Assert.Contains(":pointerover", row.Classes);
        Assert.True(Application.Current!.TryGetResource(
            "LatticeRowHoverBrush", window.ActualThemeVariant, out var hover));
        var bg = Assert.IsAssignableFrom<ISolidColorBrush>(row.Background);
        Assert.Equal(((ISolidColorBrush)hover!).Color, bg.Color);
        window.Close();
    }

    // Finding B (suspect 2 — RULED OUT on the pinned theme, kept as an upgrade guard): the owner's
    // dark-hover "flash then settle" was suspect 1 (the 100ms BrushTransition overshoot + a too-bright
    // #383838), fixed in 79bf516 by dropping the transition and toning the brush. Suspect 2 was that
    // the Fluent DataGridRow theme paints its own bright :pointerover overlay onto the BackgroundRect
    // (newer Avalonia.Controls.DataGrid main does exactly that via DataGridRowHoveredBackgroundColor).
    // Verified it is ABSENT in the pinned 12.1 theme: hovered, the BackgroundRectangle stays fully
    // transparent, so our row Background is the ONLY hover color — nothing overlays it. If a future
    // DataGrid bump reintroduces the overlay, this reddens and we neutralize the resource then.
    [AvaloniaFact]
    public void Row_hover_has_no_theme_overlay_on_the_background_rectangle()
    {
        var grid = MakeLatticeGrid();
        var window = ShowInWindow(grid);
        Layout(window);

        var row = BodyRow(grid, 0);
        var mid = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;
        window.MouseMove(mid, RawInputModifiers.None);
        Layout(window);

        Assert.Contains(":pointerover", row.Classes);
        var rect = VisualTree.FindInVisualTree<Rectangle>(row, r => r.Name == "BackgroundRectangle");
        Assert.NotNull(rect);
        var fill = Assert.IsAssignableFrom<ISolidColorBrush>(rect!.Fill);
        Assert.Equal(0, fill.Color.A); // transparent: no bright wash layered over our hover
        window.Close();
    }

    [AvaloniaFact]
    public void Hovering_an_at_risk_row_keeps_its_warning_tint()
    {
        var grid = MakeLatticeGrid();
        var window = ShowInWindow(grid);
        Layout(window);

        var row = BodyRow(grid, 0);
        // Hover applies instantly (no Background transition), so the read below is settled.
        row.Classes.Add("atRisk");
        Layout(window);
        var mid = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;
        window.MouseMove(mid, RawInputModifiers.None);
        Layout(window);

        Assert.True(Application.Current!.TryGetResource(
            "LatticeWarningTintBrush", window.ActualThemeVariant, out var warn));
        Assert.True(Application.Current!.TryGetResource(
            "LatticeRowHoverBrush", window.ActualThemeVariant, out var hover));
        var bg = Assert.IsAssignableFrom<ISolidColorBrush>(row.Background);
        Assert.Equal(((ISolidColorBrush)warn!).Color, bg.Color);
        Assert.NotEqual(((ISolidColorBrush)hover!).Color, bg.Color);
        window.Close();
    }

    [AvaloniaFact]
    public void Column_header_does_not_change_background_on_hover()
    {
        var grid = MakeLatticeGrid();
        var window = ShowInWindow(grid);
        Layout(window);

        var header = grid.GetVisualDescendants().OfType<DataGridColumnHeader>()
            .First(h => (h.Content as string) == "Name");
        var root = VisualTree.FindInVisualTree<Grid>(header, g => g.Name == "PART_ColumnHeaderRoot");
        Assert.NotNull(root);
        var before = (root!.Background as ISolidColorBrush)?.Color;

        var pt = header.TranslatePoint(new Point(header.Bounds.Width / 2, header.Bounds.Height / 2), window)!.Value;
        window.MouseMove(pt, RawInputModifiers.None);
        Layout(window);

        var after = (root.Background as ISolidColorBrush)?.Color;
        Assert.Equal(before, after);
        window.Close();
    }
}
