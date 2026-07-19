using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace Lattice.VisualTests;

/// <summary>
/// Measured paint gate for the selected-row tint — issue #13's "resource set but pixels not
/// painted" gap. <c>ThemeResourceTests</c> proves <c>LatticeSelectedTintBrush</c> RESOLVES to
/// distinct light/dark values; this proves it actually PAINTS on a selected row, in both themes.
///
/// The selection fill is a good pixel target precisely where the loading/empty overlay fill was
/// NOT: the tint is chromatically distinct from an unselected row's background (light #EBF3FC vs
/// #FFFFFF = 35; dark #123B5C vs #292929 = 92; both far past the 8-unit tolerance), so a dropped
/// or mis-wired selection brush is visible in the pixels — whereas an overlay surface repaints the
/// same colour the grid already paints behind it, giving a pixel probe nothing to bite on.
///
/// It renders a standalone <c>DataGrid.lattice</c> with three POCO rows and row 0 selected (the
/// same "faithful standalone control under the shipping styles" posture as
/// <see cref="ComboBoxTextCenteringVisualTests"/>, so no async host/snapshot pipeline is involved),
/// then asserts the selected row's fill (a) matches the tint token and (b) differs from an
/// unselected row's fill. Both assertions bite: breaking the DataGridRowSelected*BackgroundBrush
/// wiring collapses the selected row back onto the plain row background.
///
/// NOT env-gated — see <see cref="PixelProbe"/> for the CI-lane rationale (solid, AA-free
/// region colours, stable across the ubuntu/windows/macOS runners).
/// </summary>
[Trait("Category", "Visual")]
public class SelectionTintVisualTests
{
    private sealed record Item(string Name, string Value);

    [AvaloniaFact]
    public void Selected_row_paints_the_selection_tint_light() => AssertSelectionTint(ThemeVariant.Light);

    [AvaloniaFact]
    public void Selected_row_paints_the_selection_tint_dark() => AssertSelectionTint(ThemeVariant.Dark);

    private static void AssertSelectionTint(ThemeVariant variant)
    {
        // Warm the theme's Skia caches once (first render of a variant differs); discard it and
        // dispose the frame immediately — this test shares the process with the env-gated baseline
        // captures, and a leaked WriteableBitmap can segfault libSkiaSharp (VisualCapture's
        // Avalonia #19611 note).
        var warm = Render(variant, out _, out _, out var warmFrame);
        warmFrame.Dispose();
        warm.Close();

        var window = Render(variant, out var selected, out var unselected, out var frame);
        using (frame)
        {
            var buf = PixelBuffer.From(frame);
            Color tint = PixelProbe.Resolve(window, "LatticeSelectedTintBrush");

            // Sample each row's left padding strip (x = 3, before the first cell's 8 px text
            // padding), vertically centred: the row's selection fill spans the full row width, so
            // this is a clean patch clear of glyph ink.
            var sel = buf.Sample(window, selected, 3, selected.Bounds.Height / 2);
            var plain = buf.Sample(window, unselected, 3, unselected.Bounds.Height / 2);

            window.Close();

            Assert.True(PixelProbe.Near(sel, tint),
                $"[{variant}] selected row should paint the selection tint {tint}, sampled {PixelProbe.Hex(sel)}");
            // If the selection brush wiring broke, the selected row falls back to the plain row
            // background and is indistinguishable from an unselected row — caught here.
            int rowDelta = Math.Abs(sel.r - plain.r) + Math.Abs(sel.g - plain.g) + Math.Abs(sel.b - plain.b);
            Assert.True(rowDelta > PixelProbe.Tolerance,
                $"[{variant}] selected row {PixelProbe.Hex(sel)} is not distinguishable from an unselected row {PixelProbe.Hex(plain)} (delta {rowDelta} <= {PixelProbe.Tolerance}) — the selection tint did not paint");
        }
    }

    private static Window Render(ThemeVariant variant, out DataGridRow selected, out DataGridRow unselected, out Bitmap frame)
    {
        Application.Current!.RequestedThemeVariant = variant;

        var grid = new DataGrid { AutoGenerateColumns = false, CanUserResizeColumns = false, IsReadOnly = true };
        grid.Classes.Add("lattice");
        grid.Columns.Add(new DataGridTextColumn { Header = "Name", Width = new DataGridLength(150), Binding = new Binding(nameof(Item.Name)) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Value", Width = new DataGridLength(150), Binding = new Binding(nameof(Item.Value)) });
        grid.ItemsSource = new[]
        {
            new Item("alpha", "1"),
            new Item("beta", "2"),
            new Item("gamma", "3"),
        };
        grid.SelectedIndex = 0;

        var window = new Window { Width = 360, Height = 200, Content = grid };
        window.Show();
        window.Measure(new Size(360, 200));
        window.Arrange(new Rect(0, 0, 360, 200));
        Dispatcher.UIThread.RunJobs();

        var rows = grid.GetVisualDescendants().OfType<DataGridRow>().OrderBy(r => r.Bounds.Y).ToList();
        selected = rows.Single(r => r.IsSelected);
        unselected = rows.First(r => !r.IsSelected);
        frame = window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("No rendered frame captured.");
        return window;
    }
}
