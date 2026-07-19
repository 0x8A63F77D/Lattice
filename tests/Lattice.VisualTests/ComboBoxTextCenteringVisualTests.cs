using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lattice.App.Localization;
using Xunit;

namespace Lattice.VisualTests;

/// <summary>
/// Ink-gap machine gate for the Tasks command-bar "State" ComboBox vertical centering
/// (fixed by the <c>ComboBox /template/ ContentPresenter#ContentPresenter</c> style in
/// <c>App.axaml</c>). FluentAvalonia's closed-box selection text top-aligns inside the
/// stretched content region, so the glyph sits high (owner-observed on macOS, PR #84).
///
/// Unlike the screenshot-baseline tests in this project, this one is NOT env-gated: it
/// asserts a RELATIVE quantity — the difference between the glyph's top gap and bottom gap
/// inside the box — not pixel-exact colour. That difference is a deterministic integer-layout
/// property (Inter is pinned, RenderScaling is 1.0), so it is stable across the ubuntu/windows/
/// macOS CI runners where the pixel-exact baselines would drift. It therefore runs in the normal
/// <c>dotnet test</c> (ci.yml) and gates the fix everywhere.
///
/// Precedent: the M2c-1 / #13 ink-gap technique. Measured signal (both themes): the unfixed bias
/// is −5px (high); the fix moves it to +1px (centered).
/// </summary>
[Trait("Category", "Visual")]
public class ComboBoxTextCenteringVisualTests
{
    // The fixed bias measures +1px; the defect measures −5px. A ±3px band cleanly separates them
    // while absorbing single-pixel glyph-AA jitter across render backends.
    private const int MaxBiasPx = 3;

    [AvaloniaFact]
    public void State_combo_text_is_vertically_centered_light() => AssertCentered(ThemeVariant.Light);

    [AvaloniaFact]
    public void State_combo_text_is_vertically_centered_dark() => AssertCentered(ThemeVariant.Dark);

    private static void AssertCentered(ThemeVariant variant)
    {
        var (topGap, bottomGap) = MeasureInkGaps(variant);
        Assert.True(
            Math.Abs(topGap - bottomGap) <= MaxBiasPx,
            $"State ComboBox text is not vertically centered ({variant}): topGap={topGap}px, " +
            $"bottomGap={bottomGap}px, bias={topGap - bottomGap}px (tolerance ±{MaxBiasPx}px). " +
            "A negative bias means the glyph sits high inside the box.");
    }

    /// <summary>
    /// Renders a faithful copy of the Tasks command-bar State ComboBox (the same five
    /// <see cref="ComboBoxItem"/>s and <c>SelectedIndex=0</c>, inside a command-bar-height row)
    /// under the shipping App styles, and measures the selected text's ink gap to the box top vs
    /// bottom. The scan is confined to the selection <c>ContentPresenter</c>'s own rect so the FA
    /// bottom accent stroke (a full-width dark line at the box floor) never counts as glyph ink.
    /// </summary>
    private static (int topGap, int bottomGap) MeasureInkGaps(ThemeVariant variant)
    {
        Application.Current!.RequestedThemeVariant = variant;

        // Warm the theme's Skia glyph/render caches once (first render of a variant differs) so the
        // measured render is deterministic regardless of which theme ran first. Dispose the discarded
        // window + Skia frame immediately: this test is not env-gated, so it shares the process with
        // the screenshot-baseline captures, and a leaked WriteableBitmap can segfault libSkiaSharp
        // (VisualCapture's Avalonia #19611 note).
        var warmWindow = Render(variant, out _, out var warmFrame);
        warmFrame.Dispose();
        warmWindow.Close();

        var window = Render(variant, out var combo, out var frame);
        using (frame)
        {
            var buf = PixelBuffer.From(frame);

            var boxOrigin = combo.TranslatePoint(new Point(0, 0), window)!.Value;
            int boxTop = (int)Math.Round(boxOrigin.Y);
            int boxBottom = (int)Math.Round(boxOrigin.Y + combo.Bounds.Height);
            int boxInteriorLeft = (int)Math.Round(boxOrigin.X) + 3;

            var sel = combo.GetVisualDescendants().OfType<ContentPresenter>()
                .First(cp => cp.Name == "ContentPresenter");
            var selOrigin = sel.TranslatePoint(new Point(0, 0), window)!.Value;
            int selTop = (int)Math.Round(selOrigin.Y);
            int selBottom = (int)Math.Round(selOrigin.Y + sel.Bounds.Height);
            int selLeft = (int)Math.Round(selOrigin.X);
            int selRight = (int)Math.Round(selOrigin.X + sel.Bounds.Width);

            var bgc = buf.Rgb(boxInteriorLeft, boxTop + 3);

            int inkTop = -1, inkBottom = -1;
            for (int y = selTop; y < selBottom && y < buf.Height; y++)
            {
                for (int x = selLeft; x < selRight && x < buf.Width; x++)
                {
                    if (ColourDistance(buf.Rgb(x, y), bgc) > 90)
                    {
                        if (inkTop < 0) inkTop = y;
                        inkBottom = y;
                        break;
                    }
                }
            }

            window.Close();
            Assert.True(inkTop >= 0 && inkTop >= boxTop && inkBottom <= boxBottom,
                $"expected the selected 'All' text to render inside the box, but found no ink " +
                $"(inkTop={inkTop}, inkBottom={inkBottom}, box=[{boxTop}..{boxBottom}])");
            return (inkTop - boxTop, boxBottom - 1 - inkBottom);
        }
    }

    private static Window Render(ThemeVariant variant, out ComboBox combo, out Bitmap frame)
    {
        Application.Current!.RequestedThemeVariant = variant;
        combo = new ComboBox { VerticalAlignment = VerticalAlignment.Center };
        // Mirror TasksView.axaml's StateFilterBox exactly: five ComboBoxItems, "All" selected.
        combo.Items.Add(new ComboBoxItem { Content = Strings.StateAll });
        combo.Items.Add(new ComboBoxItem { Content = Strings.TaskStateRunning });
        combo.Items.Add(new ComboBoxItem { Content = Strings.TaskStateWaiting });
        combo.Items.Add(new ComboBoxItem { Content = Strings.TaskStateSuspended });
        combo.Items.Add(new ComboBoxItem { Content = Strings.TaskStateUploading });
        combo.SelectedIndex = 0;

        var bar = new Border
        {
            Height = 52, // LatticeCommandBarHeight
            Padding = new Thickness(16, 0),
            Child = combo,
        };
        var window = new Window { SizeToContent = SizeToContent.WidthAndHeight, Content = bar };
        window.Show();
        window.Measure(Size.Infinity);
        window.Arrange(new Rect(window.DesiredSize));
        Dispatcher.UIThread.RunJobs();
        frame = window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("No rendered frame captured.");
        return window;
    }

    private static int ColourDistance((int r, int g, int b) a, (int r, int g, int b) b) =>
        Math.Abs(a.r - b.r) + Math.Abs(a.g - b.g) + Math.Abs(a.b - b.b);
}
