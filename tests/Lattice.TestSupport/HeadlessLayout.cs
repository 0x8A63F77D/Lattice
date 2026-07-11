using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Lattice.Tests;

/// <summary>
/// Headless Show() does not run a full layout pass, so item containers (rail ListBox,
/// ItemsControl expanders, DataGrid rows) stay unrealized and bindings on them unbound.
/// A single measure/arrange realizes the tree, matching what a real render loop does at
/// startup — bindings themselves are correct; this only forces timing. Consume via
/// <c>using static Lattice.Tests.HeadlessLayout;</c> so call sites read <c>Layout(window)</c>.
/// </summary>
public static class HeadlessLayout
{
    public static void Layout(Window window)
    {
        window.Measure(new Size(window.Width, window.Height));
        window.Arrange(new Rect(0, 0, window.Width, window.Height));
        Dispatcher.UIThread.RunJobs();
    }
}
