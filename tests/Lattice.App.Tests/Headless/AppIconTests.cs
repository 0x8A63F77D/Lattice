using Avalonia.Platform;
using Avalonia.Headless.XUnit;
using Lattice.App.Views;
using Xunit;

namespace Lattice.App.Tests.Headless;

/// <summary>
/// Guards the icon packaging wire-up: the .ico ships as an Avalonia asset (csproj
/// <c>AvaloniaResource ... Link="Assets/lattice.ico"</c>) and the shell window binds
/// its <see cref="Avalonia.Controls.Window.Icon"/> to it. A broken Link path or a
/// removed asset would throw at XAML load / fail these asserts rather than silently
/// shipping a blank taskbar mark.
/// </summary>
public class AppIconTests
{
    private const string IconUri = "avares://Lattice.App/Assets/lattice.ico";

    [AvaloniaFact]
    public void Packaged_icon_asset_is_embedded_and_loadable()
    {
        // Directly resolves the Link'd resource path — fails loudly if the csproj
        // Link metadata did not map the out-of-project .ico to /Assets/lattice.ico.
        Assert.True(AssetLoader.Exists(new Uri(IconUri)));
        using Stream stream = AssetLoader.Open(new Uri(IconUri));
        Assert.True(stream.Length > 0);
    }

    [AvaloniaFact]
    public void Shell_window_loads_its_icon()
    {
        // Constructing the window parses the XAML, which runs the avares → WindowIcon
        // converter for Icon="/Assets/lattice.ico". A non-null Icon means the mark is
        // wired for the running window's title bar / taskbar / dock.
        var window = new ShellWindow();
        Assert.NotNull(window.Icon);
    }
}
