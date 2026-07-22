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

# One version source for the assembly and the Info.plist: MinVer, derived from
# git (see packaging/version.sh). The publish below stamps the managed assembly
# via MinVer directly — no -p:Version — and this query returns the identical
# string for the Info.plist, so the two can't diverge. Pin a specific value for a
# dry run / local one-off with MinVerVersionOverride=1.2.3.
source "$REPO_ROOT/packaging/version.sh"
VERSION="$(lattice_resolve_version "$REPO_ROOT")"

echo "==> Publishing Lattice ($RID, version $VERSION)"
PUBLISH_DIR="$OUT_DIR/publish"
rm -rf "$APP" "$PUBLISH_DIR"
# MinVer stamps the managed assembly's version, so the app's own Settings -> About
# surface (which reads AssemblyInformationalVersion) shows the real build number
# and not the 1.0.0 SDK default.
dotnet publish "$PROJECT" -c Release -r "$RID" --self-contained true \
    -p:PublishSingleFile=false -o "$PUBLISH_DIR"

echo "==> Assembling $APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$PUBLISH_DIR/." "$APP/Contents/MacOS/"
# Stamp the bundle version so every .app carries a real value rather than the
# template default. sed-on-copy keeps this portable across BSD/GNU sed (no
# in-place -i).
sed "s#<string>0.0.0</string>#<string>${VERSION}</string>#g" "$PLIST" > "$APP/Contents/Info.plist"
cp "$ICNS" "$APP/Contents/Resources/lattice.icns"
echo "    bundle version: $VERSION"

# Ad-hoc re-sign the ASSEMBLED bundle. `dotnet publish` ad-hoc signs the apphost
# Mach-O alone; wrapping it in a hand-built .app (Info.plist + Resources + the
# MacOS/ dylib tree) leaves the bundle with no _CodeSignature/CodeResources, so
# the apphost's thin signature no longer matches the bundle and Gatekeeper
# rejects a downloaded (quarantined) copy as "damaged" — right-click-Open can't
# get past it. Sealing the whole bundle here (identity "-" = ad-hoc, --deep to
# cover the nested dylibs) makes the signature valid, so a quarantined copy reads
# as the ordinary "unidentified developer" (right-click → Open works) instead.
# This is NOT notarization or Developer-ID signing — those need a paid Apple
# account and stay out of scope for v1 (see packaging/README.md).
echo "==> Ad-hoc signing $APP"
codesign --force --deep --sign - "$APP"
codesign --verify --deep --strict "$APP"

echo "==> Done: $APP"
echo "    Open with: open '$APP'   (the Dock/Finder icon is the packaged mark)"
