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

## Building the platform packages

```sh
# macOS .app bundle (default runtime osx-arm64; pass osx-x64 for Intel)
packaging/macos/bundle.sh
open artifacts/macos/osx-arm64/Lattice.app   # Dock/Finder shows the mark

# Linux hicolor icons + .desktop launcher (per-user by default)
packaging/linux/install-icons.sh
# system-wide: sudo packaging/linux/install-icons.sh /usr/share
```

The Windows `.exe` icon and the running-window icon need no extra step — they are
wired into the normal `dotnet build` / `dotnet publish` of `Lattice.App`.
