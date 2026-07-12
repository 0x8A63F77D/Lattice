# Packaging

Per-platform wire-up for the Lattice application icon (issue #39, packaging leg).

The mark itself lives in [`docs/design/icon/`](../docs/design/icon) — the finalized
Claude Design package. It is a **generated artifact**: everything here references it
in place and never hand-edits it. Revisions route back through Claude Design.

## How the icon is wired, per platform

| Platform | Mechanism | Source artifact |
|----------|-----------|-----------------|
| **Windows (.exe)** | `<ApplicationIcon>` in `src/Lattice.App/Lattice.App.csproj` embeds the Win32 PE icon. | `docs/design/icon/windows/lattice.ico` |
| **Running window** (all OSes: title bar / taskbar / dock) | `Window.Icon="/Assets/lattice.ico"` in `ShellWindow.axaml`; the `.ico` is embedded as an Avalonia asset via `<AvaloniaResource ... Link="Assets/lattice.ico" />`. | `docs/design/icon/windows/lattice.ico` |
| **macOS (.app)** | [`macos/Info.plist`](macos/Info.plist) `CFBundleIconFile=lattice` + [`macos/bundle.sh`](macos/bundle.sh) assembles the bundle and copies the `.icns` to `Contents/Resources/lattice.icns`. | `docs/design/icon/macos/lattice.icns` |
| **Linux** | [`linux/lattice.desktop`](linux/lattice.desktop) `Icon=lattice` + [`linux/install-icons.sh`](linux/install-icons.sh) stages the freedesktop hicolor theme. | `docs/design/icon/linux/hicolor/` |

### Design decision: reference, don't copy

The `.ico` is embedded through MSBuild `Link` metadata rather than copied into a
`src/Lattice.App/Assets/` folder. This keeps the generated package single-source
(no drift between a design copy and a build copy) while still giving the asset a
clean logical path (`avares:///Assets/lattice.ico`). A headless test
(`tests/Lattice.App.Tests/Headless/AppIconTests.cs`) asserts the asset is embedded
and that `Window.Icon` resolves, so a broken `Link` fails the build's test gate.

The `.icns` and the hicolor tree are filesystem inputs to the bundle/install
scripts (not app resources), so they are referenced by path — no embedding needed.

### Binary name

The app project sets `<AssemblyName>Lattice</AssemblyName>`, so the build produces
`Lattice.exe` / a `Lattice` apphost (not `Lattice.App`). That single name is what
every launch surface references: `Info.plist`'s `CFBundleExecutable=Lattice` and
`lattice.desktop`'s `Exec=Lattice`. It is also the avares assembly base
(`avares://Lattice/…`).

## Building the platform packages

```sh
# macOS .app bundle (default runtime osx-arm64; pass osx-x64 for Intel).
# LATTICE_VERSION stamps CFBundle*Version (default 0.0.0).
LATTICE_VERSION=0.1.0 packaging/macos/bundle.sh
open artifacts/macos/osx-arm64/Lattice.app   # Dock/Finder shows the mark

# Linux: install the hicolor icons + .desktop launcher (per-user by default)
packaging/linux/install-icons.sh
# system-wide: sudo packaging/linux/install-icons.sh /usr/share
```

The Windows `.exe` icon and the running-window icon need no extra step — they are
wired into the normal `dotnet build` / `dotnet publish` of the app.

## Linux distribution

The planned primary distributable is an **AppImage** (self-contained, no root,
cross-distro — a good fit given Lattice has no complex native dependencies). The
AppImage bundles the `Lattice` binary and resolves `Exec=Lattice` through its
AppRun; it consumes exactly the assets here — `linux/lattice.desktop` and the
`docs/design/icon/linux/hicolor` tree. Building the AppImage (AppRun + recipe) is
release-engineering tracked separately, not part of this icon-wiring PR.

`install-icons.sh` is scoped to what icon-wiring owns: staging the hicolor theme
and the launcher into an XDG prefix for local runs / manual installs. Putting the
`Lattice` binary on PATH belongs to the distribution format (the AppImage, or a
distro package), not this script.
