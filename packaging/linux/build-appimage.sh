#!/usr/bin/env bash
# Build a Lattice AppImage — the PRIMARY Linux distributable (issue #56).
#
# An AppImage is a single self-contained file: no root, no install, cross-distro.
# We assemble a standard AppDir (self-contained publish under usr/bin + an AppRun
# launcher + the freedesktop .desktop/icons) and hand it to appimagetool. The
# icon assets and launcher are the #53 artifacts, referenced in place.
#
# Usage: packaging/linux/build-appimage.sh [runtime-id]   (default: linux-x64)
#   LATTICE_VERSION stamps the assembly + AppImage filename (default 0.0.0).
set -euo pipefail

RID="${1:-linux-x64}"
VERSION="${LATTICE_VERSION:-0.0.0}"

# AppImage uses uname-style arch names, not .NET RIDs.
case "$RID" in
    linux-x64)   ARCH="x86_64" ;;
    linux-arm64) ARCH="aarch64" ;;
    *) echo "unsupported RID: $RID (expected linux-x64 or linux-arm64)" >&2; exit 1 ;;
esac

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PROJECT="$REPO_ROOT/src/Lattice.App/Lattice.App.csproj"
HICOLOR_SRC="$REPO_ROOT/docs/design/icon/linux/hicolor"
DESKTOP_SRC="$REPO_ROOT/packaging/linux/lattice.desktop"
OUT_DIR="$REPO_ROOT/artifacts/linux/$RID"
APPDIR="$OUT_DIR/Lattice.AppDir"
OUTPUT="$OUT_DIR/Lattice-$ARCH.AppImage"

echo "==> Publishing Lattice ($RID, version $VERSION)"
rm -rf "$APPDIR" "$OUTPUT"
# Directory publish (not single-file): the AppDir IS the bundle, so a plain
# apphost + assemblies under usr/bin is the natural layout — no redundant
# self-extract to /tmp at every launch.
dotnet publish "$PROJECT" -c Release -r "$RID" --self-contained true \
    -p:PublishSingleFile=false -p:Version="$VERSION" \
    -o "$APPDIR/usr/bin"

echo "==> Assembling AppDir"
# AppRun: resolve our own directory and exec the apphost. POSIX sh, no bashisms.
cat > "$APPDIR/AppRun" <<'EOF'
#!/bin/sh
HERE="$(dirname "$(readlink -f "$0")")"
exec "$HERE/usr/bin/Lattice" "$@"
EOF
chmod +x "$APPDIR/AppRun"

# Icon theme: the hicolor tree (Icon=lattice resolves from it), plus the
# top-level lattice.png appimagetool expects next to the .desktop.
mkdir -p "$APPDIR/usr/share/icons"
cp -r "$HICOLOR_SRC" "$APPDIR/usr/share/icons/"
cp "$HICOLOR_SRC/256x256/apps/lattice.png" "$APPDIR/lattice.png"

# .desktop: both at AppDir root (required by appimagetool) and the conventional
# usr/share/applications location.
cp "$DESKTOP_SRC" "$APPDIR/lattice.desktop"
mkdir -p "$APPDIR/usr/share/applications"
cp "$DESKTOP_SRC" "$APPDIR/usr/share/applications/lattice.desktop"

echo "==> Locating appimagetool"
# Prefer a tool already on PATH; otherwise fetch the arch-matched AppImage into
# a cache dir. appimagetool is itself an AppImage, so it needs FUSE — we run it
# with APPIMAGE_EXTRACT_AND_RUN=1 so it works on FUSE-less CI runners too.
APPIMAGETOOL="$(command -v appimagetool || true)"
if [ -z "$APPIMAGETOOL" ]; then
    CACHE="${XDG_CACHE_HOME:-$HOME/.cache}/lattice-packaging"
    mkdir -p "$CACHE"
    APPIMAGETOOL="$CACHE/appimagetool-$ARCH.AppImage"
    if [ ! -x "$APPIMAGETOOL" ]; then
        URL="https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-$ARCH.AppImage"
        echo "    downloading $URL"
        curl -fsSL "$URL" -o "$APPIMAGETOOL"
        chmod +x "$APPIMAGETOOL"
    fi
fi

echo "==> Building $OUTPUT"
# ARCH tells appimagetool which runtime to embed. EXTRACT_AND_RUN avoids the
# FUSE requirement on the build host.
ARCH="$ARCH" APPIMAGE_EXTRACT_AND_RUN=1 "$APPIMAGETOOL" "$APPDIR" "$OUTPUT"

echo "==> Done: $OUTPUT"
