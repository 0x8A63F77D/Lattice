# Lattice.VisualTests — screenshot-baseline visual regression (pilot)

Report-only visual-regression **calibration spike** (issue #82). It stands up the full
screenshot-baseline loop end-to-end on **one** representative view and produces the RMSE
distributions needed to set a future enforcement threshold. Coverage is explicitly a non-goal
— see #82.

## What it does

- Captures the view under test with **headless Skia** (`.UseSkia()` + `UseHeadlessDrawing = false`
  in [`TestAppBuilder`](TestAppBuilder.cs)); the rest of the test suite uses the pixel-less headless
  drawing backend, which is why this is a separate project.
- Pins determinism: **Inter** as the default font (test render path only — the shipping app font is
  unchanged), explicit `440×320` pixel size, headless `RenderScaling = 1.0`, and a per-process
  **theme-cache warmup** ([`VisualFixture`](VisualFixture.cs)) — calibration found the first Skia
  render of a theme differs from later ones (dark: ~2628 px), so warming the caches makes captures
  bit-stable.
- Verifies each theme's PNG through Verify's received/verified lifecycle, compared by a **tolerant
  dual-tolerance comparer** ([`TolerantPngComparer`](TolerantPngComparer.cs)) built on
  `Codeuctivity.ImageSharpCompare`: a mean-error band **and** a supra-tolerance pixel-error-count
  guard (per-pixel shift tolerance ignores single-LSB AA noise). On a mismatch it emits a
  **diff-mask PNG** to the artifacts dir.
- [`CalibrationHarness`](CalibrationHarness.cs) renders the view N times per theme and reports
  run-to-run and vs-baseline distributions (RMSE + the gate metrics) as a markdown report.

Why not `Verify.Avalonia` + `IncludeThemeVariant`? Its control converter also emits a byte-exact
visual-tree `.txt` target that short-circuits the image comparison (and suppresses diff emission)
whenever the tree changes — defeating the image gate on exactly the regressions we want to catch.
We drive Verify over the captured PNG streams directly instead; the rest of the #81 stack is intact.

## Running locally (macOS)

These tests **skip** unless opted in, and require a Mac (baselines are macOS/Skia captures):

```sh
LATTICE_RUN_VISUAL_TESTS=1 dotnet test tests/Lattice.VisualTests/Lattice.VisualTests.csproj -c Release
```

The normal `dotnet test Lattice.sln` (and cross-platform `ci.yml`) leaves the env var unset, so the
visual tests skip there and never fail the build. The dedicated `visual-tests.yml` macOS job is the
only place they run, and it is **report-only** (image diffs never fail the build).

## Rebaseline flow (received → verified)

Baselines are the committed `FirstRunViewVisualTests.Render_{light,dark}.verified.png` files.
When an **intended** visual change makes them stale:

1. Run the tests (macOS). A mismatch writes `…​.received.png` next to the baseline (git-ignored) and
   a diff mask under `artifacts/visual/` (also git-ignored).
2. **Review** the received image (and the diff mask) to confirm the change is intended.
3. Promote it:
   ```sh
   cd tests/Lattice.VisualTests
   for f in *.received.png; do mv "$f" "${f/.received./.verified.}"; done
   ```
4. Re-run to confirm green, then commit the updated `*.verified.png`.

**Source of truth.** Per #81, final baselines should be **CI-generated** (single macOS runner).
The current baselines were generated on a dev Mac pending the dev-vs-CI RMSE the calibration job
measures; if that drift is negligible they stand, otherwise promote the CI runner's `received.png`
(downloaded from the `visual-artifacts` job upload) as the baselines.

## Threshold status

The comparer's `MeanErrorThreshold` / `PixelErrorCountGuard` are **placeholders** — the gate is
report-only until the calibration distributions justify a value, exactly like the Stryker #77
pilot. Flipping to enforcement is a separate PR.
