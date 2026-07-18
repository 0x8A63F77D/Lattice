using Avalonia.Controls;

namespace Lattice.App.ViewModels;

/// <summary>What the shell should do with a window-close attempt.</summary>
public enum CloseVerdict
{
    /// <summary>Cancel the close, hide the window, keep polling in the tray.</summary>
    HideToTray,
    /// <summary>Let the window close AND initiate application shutdown.</summary>
    ExitApplication,
    /// <summary>Let the window close; shutdown is already in progress or externally owned — do not initiate it again.</summary>
    AllowClose,
}

/// <summary>
/// Pure decision core for close-to-tray (issue #92). The ShellWindow.OnClosing
/// shell maps its event args through this single function; no close semantics
/// may live anywhere else.
/// </summary>
public static class WindowClosePolicy
{
    public static CloseVerdict Decide(
        WindowCloseReason reason, bool isProgrammatic, bool exitOnClose, bool exitRequested) =>
        // CS8524: WindowCloseReason is a framework enum — every NAMED case is
        // handled below; the residual warning is about unnamed integral values,
        // which Avalonia never produces. Repo convention: pragma + this comment
        // instead of a domain-forbidden `_` arm. CI runs -warnaserror, so the
        // pragma is load-bearing, not cosmetic.
#pragma warning disable CS8524
        reason switch
        {
            // The platform/framework is already tearing the app down (macOS ⌘Q /
            // Quit menu arrives as ApplicationShutdown per F7; OS logoff/shutdown
            // as OSShutdown). Never fight it, never double-initiate.
            WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown
                => CloseVerdict.AllowClose,
            // Not applicable to a MainWindow; if it ever fires, closing is correct.
            WindowCloseReason.OwnerWindowClosing => CloseVerdict.AllowClose,
            // Tray "Exit" funnel: the controller sets exitRequested, then calls
            // Close(); the close must proceed and shutdown follows.
            WindowCloseReason.WindowClosing or WindowCloseReason.Undefined
                when exitRequested => CloseVerdict.AllowClose,
            // Programmatic Close() from our own code (not the exit funnel) means
            // the caller intends a real close (e.g. future multi-window teardown).
            WindowCloseReason.WindowClosing or WindowCloseReason.Undefined
                when isProgrammatic => CloseVerdict.AllowClose,
            // The user clicked the close button and opted out of tray residency.
            WindowCloseReason.WindowClosing or WindowCloseReason.Undefined
                when exitOnClose => CloseVerdict.ExitApplication,
            WindowCloseReason.WindowClosing or WindowCloseReason.Undefined
                => CloseVerdict.HideToTray,
        };
#pragma warning restore CS8524
}
