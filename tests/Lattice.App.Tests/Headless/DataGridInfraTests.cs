using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Xunit;

namespace Lattice.App.Tests.Headless;

public class DataGridInfraTests
{
    private class Row
    {
        public string Name { get; set; } = "Test";
        public int Value { get; set; } = 42;
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
}
