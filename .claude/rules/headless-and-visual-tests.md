---
paths:
  - "tests/Lattice.App.Tests/**"
  - "tests/Lattice.VisualTests/**"
  - "tests/Lattice.TestSupport/**"
  - ".github/workflows/visual-tests.yml"
---

# Headless and visual test canon

CLAUDE.md carries the core settle rules (`HeadlessLayout.Layout`/`Settle`/`Pump`, serial
headless assemblies, no wall-clock settles). This file adds the mechanics behind them and the
pixel-testing recipes.

## Settling

- `HeadlessLayout` (`tests/Lattice.TestSupport/HeadlessLayout.cs`) is the ONE owner of settling.
  Do not add a second mechanism.
- Transitions are detached with a disposable `SetValue(Animatable.TransitionsProperty, null,
  BindingPriority.Animation)` override, never by read-and-assign: assignment promotes
  style-declared transitions to local values. Re-scan descendants every pump round (late-realized
  rows and popups arrive with live transitions).
- A headless `Window.Show()` already runs a full layout pass, so the nav pane is born without its
  width transition (`SuppressPaneWidthAnimation`). Its class handler must be registered by a
  `[ModuleInitializer]` in EACH test assembly — one in the shared support assembly fires too late.
  Keep the installer idempotent.
- Merged-row views: Projects is the only view whose rows merge hosts, so `Rows.Count` is
  satisfiable by the first host alone. Settle multi-host Projects tests on the parent row's
  `HostsText` (its attachment count), conjoined with the existing condition. To reproduce this flake
  class, delay the second fake host's connect; revert the injection before committing.

## Pixel probes and baselines

- Only `Lattice.VisualTests` captures real pixels (headless Skia, `UseHeadlessDrawing=false`).
  `Lattice.App.Tests` uses a fake drawing backend.
- Screenshot baselines are env-gated by `LATTICE_RUN_VISUAL_TESTS=1` (macOS `visual-tests.yml`);
  targeted pixel probes (sentinel / solid-colour asserts) are NOT gated and run in `ci.yml`.
- Captures are `Rgba8888`, not `Bgra8888`. Every new pixel probe samples a CHROMATIC surface at
  least once so channel order is exercised. Use the shared `PixelBuffer`/`PixelProbe` helpers.
- Geometry (bounds arithmetic) is the instrument for layout gaps; pixels for paint defects — a
  same-colour gap scans clean. Before concluding "cannot reproduce", measure every adjacent-edge
  pair in the affected region.
- Verify PNG streams directly (`Verify(new MemoryStream(png), extension: "png")`), not
  `Verify(control)`: its visual-tree `.txt` target short-circuits the image comparison exactly
  when the structure regresses.
- Warm each theme once (discarded capture) before measured captures — the first capture per theme
  per process differs.
- Comparer pixel-shift tolerance 16 (summed |ΔRGB|) so single-LSB anti-aliasing noise is not
  counted.
- Fallback-vs-material checks: render over a magenta sentinel window background; an opaque region
  that stops painting reveals magenta.

## Owner-review captures

- Modal dialogs: host in a sized window, fire-and-forget `ShowAsync`, then loop `RunJobs` +
  layout + `ForceRenderTimerTick()` until the open transition settles (a bare `RunJobs` captures it
  mid-fade). `MenuFlyout.ShowAt(control)` and nested submenus (`IsSubMenuOpen = true`) composite
  into `CaptureRenderedFrame()`.
- Capture harnesses for owner review are throwaway: env-gated, never committed, so the reviewed
  branch stays byte-identical. Look at the PNGs yourself before handing them over.
- Design-candidate menus: render N candidates in one build by writing each candidate's value at
  Local priority over the production style's output, then falsify with ONE real source edit (the
  harness must report which candidate the shipped style produces, and the baseline constant must
  be a local literal). Anchor crops on the label baseline, not the holding panel.
  `DrawImage` `sourceRect` is in PIXELS, not DIPs; crop into an owned bitmap immediately.
