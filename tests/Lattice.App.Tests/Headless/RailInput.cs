using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Lattice.App.ViewModels;
using Lattice.App.Views;

namespace Lattice.App.Tests.Headless;

/// <summary>
/// Simulates a real click (pointer press + release, no movement) on a rail row via headless
/// pointer input, so the <c>Tapped</c> gesture fires exactly as it does under a live pointer.
///
/// A bare <c>HostList.SelectedIndex = n</c> only drives <c>SelectionChanged</c>, which misses
/// the auth-failed → Edit deep link: that link lives on the click gesture precisely because a
/// single-host rail pre-selects its sole row (RebuildRail), so re-clicking it raises NO
/// <c>SelectionChanged</c>. Tests must therefore exercise the click path, not the selection index.
/// Call <c>Layout(window)</c> first so the row's container is realized and arranged.
/// </summary>
internal static class RailInput
{
    public static void ClickRow(ShellWindow window, HostRailItemViewModel row)
    {
        var container = window.HostList.ContainerFromItem(row)
            ?? throw new InvalidOperationException(
                "Rail row has no realized container — call Layout(window) before clicking.");
        Point center = container.TranslatePoint(
                new Point(container.Bounds.Width / 2, container.Bounds.Height / 2), window)
            ?? throw new InvalidOperationException("Could not translate rail row coordinates.");
        window.MouseDown(center, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(center, MouseButton.Left, RawInputModifiers.None);
    }
}
