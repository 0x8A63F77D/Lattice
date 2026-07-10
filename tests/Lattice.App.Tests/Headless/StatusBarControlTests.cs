using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Lattice.App.Controls;
using Xunit;

namespace Lattice.App.Tests.Headless;

public class StatusBarControlTests
{
    [AvaloniaFact]
    public void StatusBarControl_renders_all_text_properties()
    {
        var control = new StatusBarControl
        {
            LeftText = "47 tasks",
            WarningText = "1 at risk",
            RightText = "Polling every 5s",
        };

        var window = new Window { Content = control, Width = 800, Height = 600 };
        window.Show();

        // Headless Show() does not run a full layout pass; measure/arrange realizes the tree.
        window.Measure(new Size(window.Width, window.Height));
        window.Arrange(new Rect(0, 0, window.Width, window.Height));

        // Verify all three texts appear in the visual tree.
        var textBlocks = window.GetVisualDescendants().OfType<TextBlock>();
        var allText = string.Join("", textBlocks.Select(tb => tb.Text ?? ""));
        Assert.Contains("47 tasks", allText);
        Assert.Contains("1 at risk", allText);
        Assert.Contains("Polling every 5s", allText);

        // Verify bounds height is 28.
        Assert.Equal(28, (int)control.Bounds.Height);

        window.Close();
    }

    [AvaloniaFact]
    public void StatusBarControl_warning_block_visibility_tracks_WarningText()
    {
        var control = new StatusBarControl
        {
            LeftText = "47 tasks",
            WarningText = "",
            RightText = "Polling every 5s",
        };

        var window = new Window { Content = control, Width = 800, Height = 600 };
        window.Show();

        window.Measure(new Size(window.Width, window.Height));
        window.Arrange(new Rect(0, 0, window.Width, window.Height));

        // GetVisualDescendants() returns elements regardless of IsVisible, so the
        // sensitive assertion is the named warning block's IsVisible itself.
        var warningBlock = window.GetVisualDescendants()
            .OfType<StackPanel>()
            .Single(p => p.Name == "WarningBlock");

        // Empty WarningText → hidden.
        Assert.False(warningBlock.IsVisible);

        // Non-empty WarningText → visible.
        control.WarningText = "1 at risk";
        Assert.True(warningBlock.IsVisible);

        // And hidden again when cleared (binding is live, not one-shot).
        control.WarningText = "";
        Assert.False(warningBlock.IsVisible);

        window.Close();
    }
}
