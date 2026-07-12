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

# Linux: publish, then install icons + launcher (Exec pinned to the built binary)
dotnet publish src/Lattice.App -c Release -r linux-x64 --self-contained true -o out/linux
packaging/linux/install-icons.sh "${XDG_DATA_HOME:-$HOME/.local/share}" out/linux
# icons/launcher only (binary already on PATH): packaging/linux/install-icons.sh
# system-wide: sudo packaging/linux/install-icons.sh /usr/share /path/to/publish
```

Passing the publish dir rewrites the installed launcher's `Exec` to the binary's
absolute path, so it starts regardless of PATH. Without it, the template ships
`Exec=Lattice`, which relies on a `Lattice` binary being on PATH (e.g. a distro
package).

The Windows `.exe` icon and the running-window icon need no extra step — they are
wired into the normal `dotnet build` / `dotnet publish` of the app.
