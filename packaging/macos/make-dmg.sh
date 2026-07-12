#!/usr/bin/env bash
# Package the Lattice .app into distributable macOS artifacts (issue #56):
#   - Lattice-<rid>.dmg  (drag-to-Applications disk image, primary)
#   - Lattice-<rid>.zip  (zipped .app, fallback)
#
# This is a thin wrapper over bundle.sh (which does the .app assembly). It is
# macOS-only (uses hdiutil + ditto). UNSIGNED for v1 — no codesigning or
# notarization (no Apple Developer account); see packaging/README.md for the
# Gatekeeper right-click-Open caveat users will hit.
#
# Usage: packaging/macos/make-dmg.sh [runtime-id]   (default: osx-arm64)
#   LATTICE_VERSION flows through to bundle.sh's CFBundle*Version stamping.
set -euo pipefail

RID="${1:-osx-arm64}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
OUT_DIR="$REPO_ROOT/artifacts/macos/$RID"
APP="$OUT_DIR/Lattice.app"
DMG="$OUT_DIR/Lattice-$RID.dmg"
ZIP="$OUT_DIR/Lattice-$RID.zip"

# Assemble Lattice.app (self-contained publish + Info.plist + .icns).
"$REPO_ROOT/packaging/macos/bundle.sh" "$RID"

echo "==> Zipping $APP"
rm -f "$ZIP"
# ditto (not zip) preserves symlinks, resource forks and the bundle bit so the
# unzipped .app launches correctly.
ditto -c -k --keepParent "$APP" "$ZIP"

echo "==> Building $DMG"
rm -f "$DMG"
STAGING="$OUT_DIR/dmg-staging"
rm -rf "$STAGING"
mkdir -p "$STAGING"
cp -R "$APP" "$STAGING/"
# The drag-to-install affordance: an /Applications alias next to the app.
ln -s /Applications "$STAGING/Applications"
hdiutil create -volname "Lattice" -srcfolder "$STAGING" -ov -format UDZO "$DMG"
rm -rf "$STAGING"

echo "==> Done:"
echo "    $DMG"
echo "    $ZIP"
