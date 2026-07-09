using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Lattice.App.Tests.Headless;

public class AppSmokeTests
{
    [AvaloniaFact]
    public void A_window_can_open_under_the_fluent_theme()
    {
        var window = new Window { Title = "smoke", Width = 400, Height = 300 };
        window.Show();
        Assert.True(window.IsVisible);
        window.Close();
    }
}
