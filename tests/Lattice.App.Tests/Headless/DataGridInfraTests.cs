using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
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
}
