using Avalonia.Controls;
using Avalonia.Styling;
using Lattice.App.Charting;
using Lattice.App.ViewModels;

namespace Lattice.App.Views;

/// <summary>
/// Statistics page. The only framework-touching concern here is theme: LiveCharts paints are
/// not DynamicResource-aware (contract warning #1), so the view reads the actual theme variant
/// and pushes it to the ViewModel, which rebuilds the chart paints. Everything else is binding.
/// </summary>
public partial class StatisticsView : UserControl
{
    public StatisticsView()
    {
        InitializeComponent();
        // The design's Fluent hover card (§3/§6): the tooltip motion + shadow ride the custom
        // tooltip; its surface/text colours are theme-dependent and applied in ApplyTheme.
        Chart.Tooltip = new FluentChartTooltip();
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplyTheme();
        ActualThemeVariantChanged += OnThemeChanged;
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        ActualThemeVariantChanged -= OnThemeChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyTheme();

    private void ApplyTheme()
    {
        var theme = ActualThemeVariant == ThemeVariant.Dark
            ? StatisticsChartTheme.Dark
            : StatisticsChartTheme.Light;

        if (DataContext is StatisticsViewModel vm)
            vm.Theme = theme;

        // Tooltip surface/text colours are SkiaSharp paints (not DynamicResource-aware, warning
        // #1), so reassign them on every theme switch — matched to the app's Fluent surface (§6).
        var (background, text) = StatisticsChartBuilder.TooltipPaints(theme);
        Chart.TooltipBackgroundPaint = background;
        Chart.TooltipTextPaint = text;
        Chart.TooltipTextSize = 12;
    }
}
