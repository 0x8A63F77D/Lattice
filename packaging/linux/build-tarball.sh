#!/usr/bin/env bash
# Build a Lattice Linux tarball — the secondary distributable (issue #56).
#
# A self-contained directory publish, tarred: unpack and run ./Lattice directly
# (the window/taskbar icon comes from the embedded Window.Icon). For users who
# want a menu entry, the hicolor icons + .desktop launcher ride along and a
# short README explains the optional install.
#
# Usage: packaging/linux/build-tarball.sh [runtime-id]   (default: linux-x64)
#   Version is derived from git by MinVer (packaging/version.sh); override a dry
#   run / local one-off with MinVerVersionOverride=1.2.3.
set -euo pipefail

RID="${1:-linux-x64}"

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PROJECT="$REPO_ROOT/src/Lattice.App/Lattice.App.csproj"
HICOLOR_SRC="$REPO_ROOT/docs/design/icon/linux/hicolor"
DESKTOP_SRC="$REPO_ROOT/packaging/linux/lattice.desktop"
OUT_DIR="$REPO_ROOT/artifacts/linux/$RID"

# Tarball name Lattice-<version>-<rid>.tar.gz from the MinVer source that also
# stamps the assembly during publish below.
source "$REPO_ROOT/packaging/version.sh"
VERSION="$(lattice_resolve_version "$REPO_ROOT")"
STAGE_NAME="Lattice-$VERSION-$RID"
STAGE="$OUT_DIR/$STAGE_NAME"
TARBALL="$OUT_DIR/$STAGE_NAME.tar.gz"

echo "==> Publishing Lattice ($RID, version $VERSION)"
rm -rf "$STAGE" "$TARBALL"
# Directory publish (not single-file) so the tarball unpacks to a plain
# ./Lattice apphost + assemblies — nothing self-extracts to /tmp. MinVer stamps
# the assembly version.
dotnet publish "$PROJECT" -c Release -r "$RID" --self-contained true \
    -p:PublishSingleFile=false \
    -o "$STAGE"

echo "==> Bundling optional desktop-integration assets"
# Icons + launcher for the user who wants a menu entry; the app runs without
# them. Kept under share/ so the tarball root stays the runnable app.
mkdir -p "$STAGE/share/icons"
cp -r "$HICOLOR_SRC" "$STAGE/share/icons/"
cp "$DESKTOP_SRC" "$STAGE/share/lattice.desktop"

cat > "$STAGE/README.txt" <<'EOF'
Lattice — multi-host BOINC monitoring dashboard
================================================

Run it
------
This is a self-contained build: no .NET install is needed. Just:

    ./Lattice

Optional: add a desktop menu entry
----------------------------------
The window/taskbar icon works out of the box. To also show Lattice in your
application menu, install the bundled launcher + icons into your XDG data dir
(run from this folder):

    # icons
    cp -r share/icons/hicolor ~/.local/share/icons/
    # launcher — point Exec at where you keep this folder's ./Lattice
    sed "s|^Exec=Lattice$|Exec=$(pwd)/Lattice|" share/lattice.desktop \
        > ~/.local/share/applications/lattice.desktop

For the no-install, single-file experience, use the AppImage instead.
EOF

echo "==> Creating $TARBALL"
# Tar with the versioned top-level dir so it unpacks cleanly (no tarbomb).
tar -czf "$TARBALL" -C "$OUT_DIR" "$STAGE_NAME"

echo "==> Done: $TARBALL"
