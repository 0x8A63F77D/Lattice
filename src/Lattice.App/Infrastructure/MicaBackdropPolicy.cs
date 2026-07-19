using Avalonia.Controls;

namespace Lattice.App.Infrastructure;

/// <summary>
/// Pure decision for the window backdrop, given the transparency level the
/// platform actually GRANTED (never the requested hint). This is #11's code
/// half: the shell requests <c>TransparencyLevelHint="Mica, None"</c> but must
/// branch on what the OS delivered — Win10/macOS/Linux and battery-saver mode
/// request Mica and are denied, and the old shell painted an opaque brush over
/// whatever came back, hiding a granted Mica and wasting the request.
///
/// Design authority (Lattice M2 Spec): "Mica: on Windows, the nav and
/// command-bar regions use Mica material; #202020 is the solid fallback for
/// macOS/Linux and battery saver; content surfaces are always opaque." So the
/// choice is region-scoped — the window and the Mica-bearing region surfaces go
/// transparent together; CONTENT surfaces are not represented here, they are
/// opaque unconditionally.
///
/// <see cref="WindowTransparencyLevel"/> in Avalonia 12.1 is a readonly STRUCT
/// with static properties (<c>None</c>/<c>Transparent</c>/<c>Blur</c>/
/// <c>AcrylicBlur</c>/<c>Mica</c>), NOT an enum — there is no compiler
/// exhaustiveness to lean on, so this is an equality map and totality lives in
/// the else-branch. Matching the <c>PartialBarPolicy</c>/<c>TasksOverlayPolicy</c>/
/// <c>ColumnVisibilityPolicy</c> precedent, the decision is a pure static so the
/// #11 acceptance can assert it headlessly (failure-mode-locality razor: a wrong
/// output is bounded and machine-checkable, unlike scattered reactive glue).
/// </summary>
public static class MicaBackdropPolicy
{
    /// <param name="granted">
    /// The level the platform reports as ACTUALLY applied
    /// (<see cref="TopLevel.ActualTransparencyLevel"/>), not the requested hint.
    /// </param>
    public static BackdropChoice Resolve(WindowTransparencyLevel granted) =>
        // ONLY Mica takes the transparent path. Every other GRANTED level — None
        // (which is also how a DENIED Mica comes back), Transparent, Blur,
        // AcrylicBlur — folds to the opaque fallback via this else-branch, never
        // to a broken transparent-without-material state. Equality-based because
        // WindowTransparencyLevel is a struct (no exhaustive switch to lean on).
        granted == WindowTransparencyLevel.Mica
            ? new BackdropChoice(WindowTransparent: true, RegionSurfacesTransparent: true)
            : new BackdropChoice(WindowTransparent: false, RegionSurfacesTransparent: false);
}

/// <summary>
/// The backdrop decision. <see cref="WindowTransparent"/> drives the window
/// background (transparent so the OS material shows, else the opaque canvas);
/// <see cref="RegionSurfacesTransparent"/> drives the Mica-bearing region
/// surfaces (nav pane) the same way. Content surfaces are opaque regardless and
/// are deliberately absent from this type.
/// </summary>
public readonly record struct BackdropChoice(bool WindowTransparent, bool RegionSurfacesTransparent);
