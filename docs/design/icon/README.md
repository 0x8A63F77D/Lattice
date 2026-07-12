# Lattice — App Icon Package

Finalized mark (定稿): the woven crystal-lattice, opaque Fluent 2 rendering — 20a (light) / 20b (dark). This is the packaged handoff for `docs/design/icon/`.

## Contents

```
svg/
  lattice-light.svg            Master, light theme (source of truth, 256px+)
  lattice-dark.svg             Master, dark theme
  lattice-light-small.svg      Simplified geometry for 16/24/32 (flat fills, thicker struts, no filters)
  lattice-dark-small.svg
  lattice-mono-template.svg    Monochrome template (black + alpha, transparent bg) — menu-bar / tray

png/
  light/  dark/  mono/         16 · 24 · 32 · 48 · 64 · 128 · 256 · 512 · 1024
                               (16/24/32 rendered from the simplified small variant)

windows/lattice.ico            16 · 24 · 32 · 48 · 256 (16/24/32 = minimal variant)
macos/lattice.icns             16→1024 incl. @2x retina slices
linux/hicolor/                 freedesktop layout: NxN/apps/lattice.png + scalable/apps/lattice.svg
```

## Color

Fluent blue family (echoes the in-app accent).
- Light plate: `#2585DE → #0C4A8E`  · weft `#C9DEF2` · warp `#76A9DC` · accent `#9AE2FF → #5FC7FF`
- Dark plate: `#0E3A63 → #051526`  · weft `#9DBAD6` · warp `#45678D` · accent `#6FD2FF → #2FB4F5` (accent strut glows)

## Notes

- Master SVGs carry the full Fluent depth (soft per-crossing shadows, subtle wallpaper bloom, faint noise). At 16/24/32 those filters muddy, so small sizes use the simplified variant — same geometry, flat colors, thicker struts.
- The `.ico` / `.icns` / hicolor PNGs are built from the **light** master (the canonical app icon; its blue plate reads on both light taskbars and dark docks).
- Monochrome template is a single-color + alpha image (accent strut at full alpha, others dimmed) for the future tray / menu-bar surface — the OS tints it.
- Packaging wire-up (csproj `ApplicationIcon`, `Info.plist`, `.desktop`) is a separate follow-up PR. Artifacts are generated, not hand-edited — revisions route back through Claude Design.
