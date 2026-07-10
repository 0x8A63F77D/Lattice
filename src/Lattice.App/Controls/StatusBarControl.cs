using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace Lattice.App.Controls;

/// <summary>
/// A 28px status bar strip: left text, centered warning block (icon + text), right text.
/// Dumb control — all text set by consumers.
/// </summary>
public class StatusBarControl : TemplatedControl
{
    public static readonly StyledProperty<string> LeftTextProperty =
        AvaloniaProperty.Register<StatusBarControl, string>(nameof(LeftText), "");

    public static readonly StyledProperty<string> WarningTextProperty =
        AvaloniaProperty.Register<StatusBarControl, string>(nameof(WarningText), "");

    public static readonly StyledProperty<string> RightTextProperty =
        AvaloniaProperty.Register<StatusBarControl, string>(nameof(RightText), "");

    public string LeftText
    {
        get => GetValue(LeftTextProperty);
        set => SetValue(LeftTextProperty, value);
    }

    public string WarningText
    {
        get => GetValue(WarningTextProperty);
        set => SetValue(WarningTextProperty, value);
    }

    public string RightText
    {
        get => GetValue(RightTextProperty);
        set => SetValue(RightTextProperty, value);
    }
}
