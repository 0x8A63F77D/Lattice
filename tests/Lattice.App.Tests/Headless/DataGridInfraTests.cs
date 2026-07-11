using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

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
}
