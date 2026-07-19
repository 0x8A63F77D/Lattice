namespace Lattice.App.Infrastructure;

/// <summary>OS platform families that determine the close-to-tray default (issue #92).</summary>
public enum TrayPlatform { Windows, MacOS, Linux }

/// <summary>
/// Pure resolution of the close-to-tray preference default (issue #92, plan Part 4).
/// Windows/macOS have full tray support (F2) so close-to-tray is ON by default
/// (<c>ExitOnClose = false</c>); Linux tray presence is not programmatically detectable
/// (F13), so residency is an explicit user opt-in and the default is exit-on-close
/// (<c>ExitOnClose = true</c>) — a default can never silently strand the app invisible.
/// </summary>
public static class TrayResidencyDefaults
{
    /// <summary>The running platform mapped to a <see cref="TrayPlatform"/>. Anything
    /// that is neither Windows nor macOS (Linux and other unix-likes) maps to Linux,
    /// the opt-in-residency family.</summary>
    public static TrayPlatform Current =>
        OperatingSystem.IsWindows() ? TrayPlatform.Windows
        : OperatingSystem.IsMacOS() ? TrayPlatform.MacOS
        : TrayPlatform.Linux;

    /// <summary>Platform default for <see cref="UiState.ExitOnClose"/> when the user
    /// has never chosen (stored value is null / JSON-missing).</summary>
    public static bool ExitOnCloseDefault(TrayPlatform platform) =>
#pragma warning disable CS8524 // Domain enum: keep CS8509 live so a new TrayPlatform member
        // forces this mapping to be revisited; CS8524 is the residual unnamed-integral case
        // (an out-of-range cast, unreachable for a well-formed value). Same pattern as
        // ThemePreference / WindowClosePolicy; CI runs -warnaserror so the pragma is load-bearing.
        platform switch
        {
            TrayPlatform.Windows => false,
            TrayPlatform.MacOS => false,
            TrayPlatform.Linux => true,
        };
#pragma warning restore CS8524

    /// <summary>Resolves the stored preference against the platform default: a concrete
    /// stored bool wins; null falls to <see cref="ExitOnCloseDefault"/>.</summary>
    public static bool Resolve(bool? stored, TrayPlatform platform) =>
        stored ?? ExitOnCloseDefault(platform);
}
