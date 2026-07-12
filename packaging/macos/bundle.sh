#!/usr/bin/env bash
# Assemble a macOS .app bundle for Lattice.
#
# Avalonia/.NET has no built-in macOS bundler, so this script does the standard
# three-part assembly by hand: publish the self-contained binary, lay out the
# Contents/{MacOS,Resources} tree, and drop in Info.plist + the .icns mark. The
# icon is the finalized design artifact (docs/design/icon/macos/lattice.icns),
# referenced in place — never hand-edited here.
#
# Usage: packaging/macos/bundle.sh [runtime-id]   (default: osx-arm64)
set -euo pipefail

RID="${1:-osx-arm64}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PROJECT="$REPO_ROOT/src/Lattice.App/Lattice.App.csproj"
ICNS="$REPO_ROOT/docs/design/icon/macos/lattice.icns"
PLIST="$REPO_ROOT/packaging/macos/Info.plist"
OUT_DIR="$REPO_ROOT/artifacts/macos/$RID"
APP="$OUT_DIR/Lattice.app"

echo "==> Publishing Lattice ($RID)"
PUBLISH_DIR="$OUT_DIR/publish"
rm -rf "$APP" "$PUBLISH_DIR"
dotnet publish "$PROJECT" -c Release -r "$RID" --self-contained true \
    -p:PublishSingleFile=false -o "$PUBLISH_DIR"

echo "==> Assembling $APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$PUBLISH_DIR/." "$APP/Contents/MacOS/"
# Stamp the bundle version so every .app carries a real value rather than the
# template default. Override with LATTICE_VERSION=1.2.3; sed-on-copy keeps this
# portable across BSD/GNU sed (no in-place -i).
VERSION="${LATTICE_VERSION:-0.0.0}"
sed "s#<string>0.0.0</string>#<string>${VERSION}</string>#g" "$PLIST" > "$APP/Contents/Info.plist"
cp "$ICNS" "$APP/Contents/Resources/lattice.icns"
echo "    bundle version: $VERSION"

echo "==> Done: $APP"
echo "    Open with: open '$APP'   (the Dock/Finder icon is the packaged mark)"
