namespace Lattice.App.Infrastructure;

/// <summary>
/// The tray-icon asset a platform should show: which embedded image to load
/// (<see cref="Uri"/>) and whether macOS must mark it as a TEMPLATE image
/// (<see cref="IsTemplate"/>) so AppKit tints it.
/// </summary>
public readonly record struct TrayIconAsset(string Uri, bool IsTemplate);

/// <summary>
/// Pure platform → tray-icon-asset decision (issue #108). macOS gets a monochrome
/// TEMPLATE glyph — pure black with an alpha hierarchy — that the OS tints for the
/// light and dark menu bars and for the menu-open highlight (Apple's menu-bar
/// convention). Windows and Linux keep the full-color <c>lattice.ico</c> their
/// notification areas render natively; a busy colored glyph reads poorly next to
/// native macOS menu-bar items, which is the whole reason #108 exists.
///
/// The macOS asset is the 36 px (<c>@2x</c>-resolution) master, not the 18 px @1x:
/// Avalonia's macOS tray path (<c>TrayIconImpl.SetIcon</c> → native
/// <c>NSImage(initWithData:)</c>, resized to ~18 pt) consumes a SINGLE bitmap, so
/// the Apple <c>@2x</c> filename convention is never density-resolved there — one
/// 36 px rep is crisp at 18 pt on Retina (2x backing) and downsamples cleanly on 1x.
///
/// Kept a pure static (mirroring <see cref="TrayResidencyDefaults"/>,
/// <c>MicaBackdropPolicy</c>, and the ViewModels policy modules) so the selection
/// is machine-checkable headlessly — failure-mode-locality razor: a wrong asset
/// choice is a bounded, testable output, unlike the rendered menu-bar appearance,
/// which only the owner's eye can judge on hardware.
/// </summary>
public static class TrayIconAssetPolicy
{
    /// <summary>Full-color app icon; Windows/Linux notification areas render it natively.</summary>
    public const string ColorIconUri = "avares://Lattice/Assets/lattice.ico";

    /// <summary>macOS monochrome template glyph (36 px master; see class remarks for why @2x).</summary>
    public const string MacTemplateIconUri = "avares://Lattice/Assets/latticeTrayTemplate@2x.png";

    /// <summary>Resolves the icon asset and template flag for the running platform family.</summary>
    public static TrayIconAsset Select(TrayPlatform platform) =>
#pragma warning disable CS8524 // Domain enum: keep CS8509 live so a new TrayPlatform member
        // forces this mapping to be revisited; CS8524 is the residual unnamed-integral case
        // (an out-of-range cast, unreachable for a well-formed value). Same pattern as
        // TrayResidencyDefaults / ThemePreference; CI runs -warnaserror so the pragma is load-bearing.
        platform switch
        {
            TrayPlatform.MacOS => new TrayIconAsset(MacTemplateIconUri, IsTemplate: true),
            TrayPlatform.Windows => new TrayIconAsset(ColorIconUri, IsTemplate: false),
            TrayPlatform.Linux => new TrayIconAsset(ColorIconUri, IsTemplate: false),
        };
#pragma warning restore CS8524
}
