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
    public StatisticsView() => InitializeComponent();

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
        if (DataContext is StatisticsViewModel vm)
            vm.Theme = ActualThemeVariant == ThemeVariant.Dark
                ? StatisticsChartTheme.Dark
                : StatisticsChartTheme.Light;
    }
}
