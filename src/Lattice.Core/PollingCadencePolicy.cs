namespace Lattice.Core;

/// <summary>
/// Pure cadence decision for tray residency (issue #92): what interval should
/// monitors actually poll at, given the configured interval and UI visibility.
/// HostMonitorManager is the only caller; monitors never see visibility.
/// </summary>
public static class PollingCadencePolicy
{
    /// <summary>Relaxed floor while hidden: never poll faster than this (seconds).
    /// 30, not 60 — get_messages gap risk on busy hosts bounds the floor (Q1).</summary>
    public const int HiddenFloorSeconds = 30;

    /// <summary>
    /// The interval monitors should actually poll at: the configured interval while the
    /// window is visible or full-speed hidden polling is on, otherwise floored at
    /// <see cref="HiddenFloorSeconds"/> (the floor never speeds up an already-slow host).
    /// </summary>
    public static int EffectiveIntervalSeconds(
        int configuredSeconds, bool windowVisible, bool fullSpeedHidden) =>
        windowVisible || fullSpeedHidden
            ? configuredSeconds
            : Math.Max(configuredSeconds, HiddenFloorSeconds);
}
