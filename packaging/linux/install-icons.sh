#!/usr/bin/env bash
# Install Lattice's freedesktop hicolor icons and .desktop entry.
#
# Stages the finalized design artifacts (docs/design/icon/linux/hicolor) into an
# XDG icon theme prefix so the .desktop entry's `Icon=lattice` resolves, then
# installs the launcher. Icons are referenced in place — never hand-edited here.
#
# Usage: packaging/linux/install-icons.sh [prefix]
#   default prefix: $XDG_DATA_HOME (or ~/.local/share) — a per-user install.
#   For a system install: sudo packaging/linux/install-icons.sh /usr/share
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

echo "==> Done. 'Icon=lattice' now resolves from the hicolor theme."
