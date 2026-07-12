#!/usr/bin/env bash
# Install Lattice's freedesktop hicolor icons, .desktop entry, and (optionally)
# the app binary so the launcher actually starts the app.
#
# Stages the finalized design artifacts (docs/design/icon/linux/hicolor) into an
# XDG icon theme prefix so the .desktop entry's `Icon=lattice` resolves, installs
# the launcher, and — when given a published build — links the `Lattice` binary
# onto PATH so `Exec=Lattice` resolves. Icons are referenced in place, never
# hand-edited here.
#
# Usage:
#   packaging/linux/install-icons.sh [data-prefix] [published-app-dir]
#     data-prefix       : default $XDG_DATA_HOME (or ~/.local/share). Per-user.
#                         For a system install: sudo … /usr/share
#     published-app-dir : optional output of `dotnet publish -r linux-x64` for
#                         src/Lattice.App. If given, the `Lattice` binary is linked
#                         into the sibling bin dir so the launcher's Exec resolves.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
HICOLOR_SRC="$REPO_ROOT/docs/design/icon/linux/hicolor"
DESKTOP_SRC="$REPO_ROOT/packaging/linux/lattice.desktop"
PREFIX="${1:-${XDG_DATA_HOME:-$HOME/.local/share}}"
APP_DIR="${2:-}"

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

if [ -n "$APP_DIR" ]; then
    # ~/.local/share -> ~/.local/bin ; /usr/share -> /usr/bin
    BIN_DIR="$(dirname "$PREFIX")/bin"
    APP_BIN="$APP_DIR/Lattice"
    [ -x "$APP_BIN" ] || { echo "error: no executable 'Lattice' in $APP_DIR" >&2; exit 1; }
    echo "==> Linking $APP_BIN -> $BIN_DIR/Lattice"
    mkdir -p "$BIN_DIR"
    ln -sf "$APP_BIN" "$BIN_DIR/Lattice"
fi

if command -v gtk-update-icon-cache >/dev/null 2>&1; then
    gtk-update-icon-cache -f -t "$ICON_ROOT" 2>/dev/null || true
fi

echo "==> Done. 'Icon=lattice' resolves from the hicolor theme."
[ -z "$APP_DIR" ] && echo "    (No app dir given — ensure a 'Lattice' binary is on PATH for Exec=Lattice.)"
