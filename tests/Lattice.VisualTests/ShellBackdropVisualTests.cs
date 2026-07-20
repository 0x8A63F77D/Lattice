using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lattice.App.Controls;
using Lattice.App.Infrastructure;
using Lattice.App.ViewModels;
using Lattice.App.Views;
using Lattice.Core;
using Lattice.Tests;
using Xunit;

namespace Lattice.VisualTests;

/// <summary>
/// Measured fallback END-STATE for the Mica region-scoping (Codex P1, PR #99). The property gate in
/// <c>ShellBackdropTests</c> proves the region brushes are ASSIGNED; this proves they actually PAINT.
/// It renders the real <see cref="TasksView"/> (the representative view — same "one view" posture as
/// <see cref="FirstRunViewVisualTests"/>) over a MAGENTA sentinel window background: an opaque region
/// that stops painting reveals the sentinel and reddens, so stripping a ContentRegion's canvas or
/// breaking the command-bar brush is caught end-to-end, not just at the property level.
///
/// The Mica-GRANTED end-state is deliberately NOT pixel-tested here: the headless platform coerces
/// <c>ActualTransparencyLevel</c> away from Mica (verified — it is why the recursion test uses an
/// internal seam) and there is no compositor, so the granted-Mica pixels are an owner-on-hardware
/// check (#11); shell screenshot baselines are #13, kept out of this code PR by the plan.
///
/// NOT env-gated: it asserts solid, AA-free region colours (not glyph-exact pixels), which are stable
/// across the ubuntu/windows/macOS CI renderers — same rationale as <see cref="ComboBoxTextCenteringVisualTests"/>.
/// </summary>
[Trait("Category", "Visual")]
public class ShellBackdropVisualTests
{
    // Magenta is not a Lattice token, so it only appears where an opaque surface failed to paint.
    private static readonly Color Sentinel = Color.FromRgb(255, 0, 255);

    [AvaloniaFact]
    public void Fallback_paints_command_bar_surface_and_content_canvas_light() => AssertFallbackColours(ThemeVariant.Light);

    [AvaloniaFact]
    public void Fallback_paints_command_bar_surface_and_content_canvas_dark() => AssertFallbackColours(ThemeVariant.Dark);

    private static void AssertFallbackColours(ThemeVariant variant)
    {
        // Warm the theme's Skia caches once (first render of a variant differs); discard it. Dispose the
        // frame immediately — this test shares the process with the env-gated baseline captures, and a
        // leaked WriteableBitmap can segfault libSkiaSharp (VisualCapture's Avalonia #19611 note).
        var warm = Render(variant, out _, out _, out var warmFrame);
        warmFrame.Dispose();
        warm.Close();

        var window = Render(variant, out var commandBar, out var statusBar, out var frame);
        using (frame)
        {
            var buf = PixelBuffer.From(frame);
            Color surface = PixelProbe.Resolve(window, "LatticeCommandBarSurfaceBrush");
            Color canvas = PixelProbe.Resolve(window, "LatticeCanvasBrush");

            // Command-bar band: sample the Border's left padding (x < 16 px, no child content), vertically
            // centred — a clean patch of the command-bar surface.
            var cb = buf.Sample(window, commandBar, 4, commandBar.Bounds.Height / 2);
            // Status strip: its own template is transparent, so it shows the opaque ContentRegion canvas
            // behind it. Sample the empty centre, clear of the docked left/right text and the 1 px top stroke.
            var sb = buf.Sample(window, statusBar, statusBar.Bounds.Width / 2, statusBar.Bounds.Height / 2);

            window.Close();

            Assert.True(PixelProbe.Near(cb, surface),
                $"[{variant}] command-bar band should paint the surface colour {surface}, sampled {PixelProbe.Hex(cb)}");
            Assert.True(PixelProbe.Near(sb, canvas),
                $"[{variant}] content region (behind the status strip) should paint the canvas colour {canvas}, sampled {PixelProbe.Hex(sb)}");
            // The whole point of the sentinel: if any opaque surface stopped painting, its magenta shows.
            Assert.False(PixelProbe.Near(cb, Sentinel) || PixelProbe.Near(sb, Sentinel),
                $"[{variant}] a surface revealed the sentinel backdrop — an opaque region stopped painting (cb={PixelProbe.Hex(cb)}, sb={PixelProbe.Hex(sb)})");
            // No command-bar-vs-canvas distinctness assertion: the design's dark command-bar region
            // (#202020) is intentionally within a hair of the dark canvas (#1F1F1F). Breaking the
            // command-bar brush is still caught in Light (surface #F5F5F5 vs canvas #FAFAFA) and, in both
            // themes, a transparent command bar is caught by the sentinel above.
        }
    }

    private static Window Render(ThemeVariant variant, out Border commandBar, out StatusBarControl statusBar, out Bitmap frame)
    {
        Application.Current!.RequestedThemeVariant = variant;

        var tmp = Path.Combine(Path.GetTempPath(), $"lt-vis-{Guid.NewGuid():N}.json");
        var registry = new HostRegistry(new LatticeConfig(5, []), tmp);
        // The manager is never started (no sockets/threads), so the client factory and clock never fire.
        var manager = new HostMonitorManager(registry, () => new FakeGuiRpcClient(), TimeProvider.System);
        var store = new HostStore(registry, manager, AvaloniaUiDispatcher.Instance);
        var uiState = new UiStateStore(Path.Combine(Path.GetTempPath(), $"lt-vis-{Guid.NewGuid():N}-ui.json"));
        var vm = new TasksViewModel(store, new NoTickClock(), uiState, new DensityPreference(uiState),
            new HostControlService(registry, manager, () => new FakeGuiRpcClient()));

        var window = new Window
        {
            Width = 900,
            Height = 600,
            // Stand-in backdrop: any opaque region that fails to paint reveals this instead of a token colour.
            Background = new SolidColorBrush(Sentinel),
            Content = new TasksView { DataContext = vm },
        };
        window.Show();
        window.Measure(new Size(900, 600));
        window.Arrange(new Rect(0, 0, 900, 600));
        Dispatcher.UIThread.RunJobs();

        commandBar = window.GetVisualDescendants().OfType<Border>().First(b => b.Name == "CommandBar");
        statusBar = window.GetVisualDescendants().OfType<StatusBarControl>().First();
        frame = window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("No rendered frame captured.");
        return window;
    }

    // A clock that never ticks: TasksViewModel only needs Now for a formatted string; the render does not
    // depend on it, and this avoids a real DispatcherTimer running inside the pixel test.
    private sealed class NoTickClock : IUiClock
    {
        public event EventHandler? Tick { add { } remove { } }
        public DateTimeOffset Now => DateTimeOffset.UnixEpoch;
    }
}
