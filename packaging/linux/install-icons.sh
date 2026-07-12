#!/usr/bin/env bash
# Install Lattice's freedesktop hicolor icons and .desktop launcher.
#
# Stages the finalized design artifacts (docs/design/icon/linux/hicolor) into an
# XDG icon theme prefix so `Icon=lattice` resolves, and installs the launcher.
# When given a published build, the installed launcher's Exec is rewritten to the
# ABSOLUTE path of the Lattice binary, so it starts regardless of PATH; otherwise
# the template's `Exec=Lattice` is kept (which relies on a Lattice binary being on
# PATH, e.g. from a distro package). Icons and the desktop template are referenced
# in place, never hand-edited here.
#
# Usage:
#   packaging/linux/install-icons.sh [data-prefix] [published-app-dir]
#     data-prefix       : default $XDG_DATA_HOME (or ~/.local/share). Per-user.
#                         For a system install: sudo … /usr/share
#     published-app-dir : optional output of `dotnet publish -r linux-x64` for
#                         src/Lattice.App. If given, the installed launcher points
#                         Exec at "<published-app-dir>/Lattice" (absolute).
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
DEST_DESKTOP="$APPS_DIR/lattice.desktop"
if [ -n "$APP_DIR" ]; then
    # Absolute Exec = no PATH lookup, so the launcher works for any data-prefix.
    # (A symlink into a sibling bin dir is not guaranteed to be on the desktop
    # environment's PATH, and unqualified Exec= resolves through PATH.)
    APP_DIR="$(cd "$APP_DIR" 2>/dev/null && pwd)" || { echo "error: publish dir not found: $2" >&2; exit 1; }
    APP_BIN="$APP_DIR/Lattice"
    [ -x "$APP_BIN" ] || { echo "error: no executable 'Lattice' in $APP_DIR" >&2; exit 1; }
    # Rewrite the single Exec line; quote the path so spaces survive Desktop-Entry
    # parsing. The template has one [Desktop Entry] group, so appending is safe.
    { grep -v '^Exec=' "$DESKTOP_SRC"; printf 'Exec="%s"\n' "$APP_BIN"; } > "$DEST_DESKTOP"
    echo "    Exec -> \"$APP_BIN\""
else
    cp "$DESKTOP_SRC" "$DEST_DESKTOP"
fi

if command -v gtk-update-icon-cache >/dev/null 2>&1; then
    gtk-update-icon-cache -f -t "$ICON_ROOT" 2>/dev/null || true
fi

echo "==> Done. 'Icon=lattice' resolves from the hicolor theme."
# if-block, not `[ -z ] && echo`: as the script's last command the latter would
# exit 1 on the (non-empty APP_DIR) success path and fail CI/package callers.
if [ -z "$APP_DIR" ]; then
    echo "    (No app dir given — the launcher's Exec=Lattice needs a Lattice binary on PATH.)"
fi
