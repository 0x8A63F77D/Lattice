using Avalonia;
using Avalonia.Headless;
using Lattice.App.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Lattice.App.Tests;

public class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Lattice.App.App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
