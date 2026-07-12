#!/usr/bin/env bash
# Install Lattice's freedesktop hicolor icons and .desktop launcher into an XDG
# data prefix — for local runs and manual installs.
#
# Stages the finalized design artifacts (docs/design/icon/linux/hicolor) so
# `Icon=lattice` resolves, and installs the launcher (Exec=Lattice). Placing the
# `Lattice` binary on PATH is the job of the distribution format — the AppImage
# bundles it (resolved by its AppRun), a distro package installs it — and is out
# of scope here. Artifacts are referenced in place, never hand-edited.
#
# Usage: packaging/linux/install-icons.sh [data-prefix]
#   default $XDG_DATA_HOME (or ~/.local/share). System-wide: sudo … /usr/share
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
HICOLOR_SRC="$REPO_ROOT/docs/design/icon/linux/hicolor"
DESKTOP_SRC="$REPO_ROOT/packaging/linux/lattice.desktop"
PREFIX="${1:-${XDG_DATA_HOME:-$HOME/.local/share}}"

ICON_ROOT="$PREFIX/icons/hicolor"
APPS_DIR="$PREFIX/applications"

echo "==> Installing hicolor icons to $ICON_ROOT"
# Preserve the NxN/apps/lattice.png + scalable/apps/lattice.svg tree verbatim.
find "$HICOLOR_SRC" -type f | while read -r src; do
    rel="${src#"$HICOLOR_SRC"/}"
    dest="$ICON_ROOT/$rel"
    mkdir -p "$(dirname "$dest")"
    cp "$src" "$dest"
done

echo "==> Installing launcher to $APPS_DIR"
mkdir -p "$APPS_DIR"
cp "$DESKTOP_SRC" "$APPS_DIR/lattice.desktop"

if command -v gtk-update-icon-cache >/dev/null 2>&1; then
    gtk-update-icon-cache -f -t "$ICON_ROOT" 2>/dev/null || true
fi

echo "==> Done. Icons + launcher installed."
echo "    Exec=Lattice needs a 'Lattice' binary on PATH — provided by the AppImage"
echo "    (its AppRun) or a distro package; see docs, not this script."
