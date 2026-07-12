# Packaging

Per-platform icon wire-up (issue #39/#53) **and** the release-artifact build
scripts + tag-driven release workflow (issue #56).

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

## Release artifacts (issue #56)

Every platform ships a **self-contained** build — the .NET runtime is bundled, so
end users need nothing installed. Each format has a build script; the release
workflow ([`.github/workflows/release.yml`](../.github/workflows/release.yml)) runs
them on the matching runner and attaches the output to a GitHub Release.

| Platform | Script | Produces | Notes |
|----------|--------|----------|-------|
| **Windows** | [`windows/build-zip.ps1`](windows/build-zip.ps1) | `Lattice-win-x64.zip` (single-file `Lattice.exe`) | Portable, unzip-and-run. Unsigned → SmartScreen prompt. |
| **macOS** | [`macos/make-dmg.sh`](macos/make-dmg.sh) | `Lattice-osx-arm64.dmg` (and `osx-x64`) | Drag-to-Applications. Ad-hoc signed, not notarized → Gatekeeper prompt. |
| **Linux** | [`linux/build-appimage.sh`](linux/build-appimage.sh) | `Lattice-x86_64.AppImage` | Primary. Single file, no root, cross-distro. |
| **Linux** | [`linux/build-tarball.sh`](linux/build-tarball.sh) | `Lattice-<ver>-linux-x64.tar.gz` | Unpack and `./Lattice`. |

All scripts honor `LATTICE_VERSION` (default `0.0.0`) to stamp the assembly /
bundle version and the artifact filename. Default RIDs are `win-x64`, `osx-arm64`,
`osx-x64`, `linux-x64`; `*-arm64` for Windows/Linux is an easy future addition
(the AppImage cross-arch build is the only non-trivial one).

```sh
# Local builds (each writes under artifacts/<platform>/<rid>/)
LATTICE_VERSION=0.1.0 packaging/macos/make-dmg.sh osx-arm64
LATTICE_VERSION=0.1.0 packaging/linux/build-appimage.sh linux-x64
LATTICE_VERSION=0.1.0 packaging/linux/build-tarball.sh linux-x64
pwsh packaging/windows/build-zip.ps1 -Rid win-x64 -Version 0.1.0
```

### Self-contained publish settings

The per-RID publish knobs live in a `RuntimeIdentifier`-guarded `<PropertyGroup>`
in [`src/Lattice.App/Lattice.App.csproj`](../src/Lattice.App/Lattice.App.csproj),
so a RID-less `dotnet build` / `dotnet test` (what CI runs) is untouched — they
engage only for `dotnet publish -r <rid>`:

- `SelfContained=true` — bundle the runtime.
- `PublishSingleFile=true` + `IncludeNativeLibrariesForSelfExtract=true` +
  `EnableCompressionInSingleFile=true` — the Windows portable-exe path. The `.app`
  and AppDir builders opt out (`-p:PublishSingleFile=false`) since they already
  ship a directory layout.
- **`PublishTrimmed=false` (deliberate).** Avalonia resolves controls, converters
  and styles through reflection the IL trimmer cannot see, so trimming risks a
  runtime `MissingMethodException`. It stays off until proven safe per-RID. This
  is the documented, intentional trade-off (larger artifacts for correctness).
- `PublishReadyToRun=false` — avoids cross-arch crossgen issues (e.g. building
  `osx-x64` on an Apple-Silicon runner).

### Release workflow

`release.yml` triggers on a `v*` tag **or** manual `workflow_dispatch` — never on
a branch push (that is [`ci.yml`](../.github/workflows/ci.yml)'s job).

- **Tag push** (`git tag v0.1.0 && git push --tags`): builds all platforms, then
  a `release` job publishes a GitHub Release with every artifact attached. The
  version is derived from the tag (`v0.1.0` → `0.1.0`).
- **`workflow_dispatch`**: a dry run — builds and uploads the artifacts as
  workflow-run artifacts (downloadable from the run) but publishes **no** Release.
  Use it to exercise packaging on a branch before cutting a real tag.

### Signing caveats (unsigned for v1)

There are no paid code-signing certificates (Authenticode / Apple Developer ID
are out of scope — see issue #56). Users therefore see an OS trust prompt on
first launch:

- **macOS (Gatekeeper):** the `.app` is **ad-hoc signed** by `bundle.sh`
  (`codesign --sign -`) but not notarized. Ad-hoc signing matters: without it the
  hand-assembled bundle has no sealed `_CodeSignature`, so a downloaded
  (quarantined) copy is rejected as **"damaged" — which right-click-Open cannot
  bypass.** With it, a quarantined copy reads as the ordinary "unidentified
  developer," so **right-clicking the app → Open** (then confirm) works. Or drop
  the quarantine attribute outright:
  ```sh
  xattr -dr com.apple.quarantine /Applications/Lattice.app
  ```
  Proper Developer-ID signing + notarization (no prompt at all) needs a paid
  Apple account and is deferred past v1.
- **Windows (SmartScreen):** an unsigned `.exe` shows "Windows protected your PC."
  Click **More info → Run anyway**.
- **Linux:** no signing gate. `chmod +x Lattice-x86_64.AppImage && ./Lattice-x86_64.AppImage`.

### AppImage / FUSE

`build-appimage.sh` runs `appimagetool` with `APPIMAGE_EXTRACT_AND_RUN=1`, so the
build host needs no FUSE. The produced AppImage, however, uses the standard
runtime, which needs FUSE **on the end user's machine**; on a FUSE-less system a
user can still run it with `./Lattice-x86_64.AppImage --appimage-extract-and-run`.

## Linux distribution

Two distributables are planned; both consume only the assets here.

- **AppImage** (primary) — a self-contained single file (no root, cross-distro; a
  good fit since Lattice has no complex native dependencies). It bundles the
  `Lattice` binary and resolves `Exec=Lattice` via its AppRun, consuming exactly
  `linux/lattice.desktop` + the `docs/design/icon/linux/hicolor` tree. Building the
  AppImage (AppRun + recipe) is release-engineering, tracked separately.
- **Tarball** — a self-contained publish, tarred: unpack and run `./Lattice`
  directly (the taskbar icon comes from `Window.Icon`). Adding a desktop-menu entry
  is the user's own business, as with any tarball.

`install-icons.sh` is an optional convenience that stages the hicolor theme and a
`Exec=Lattice` launcher into an XDG prefix (for a `Lattice` binary already on PATH).
Neither distributable requires it — placing the binary on PATH is the job of the
distribution format, not this script.
