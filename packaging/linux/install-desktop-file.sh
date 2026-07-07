#!/usr/bin/env bash
#
# Dev helper: install (or remove) a desktop entry + icons that point at the local Debug build,
# so the app shows in the dock/dash and app list with its icon while developing - without
# building a full deb/rpm/flatpak. GNOME/KDE match the running window to this .desktop via
# StartupWMClass (io.github.bizzehdee.bzTorrentClient), which the app sets as its X11 WmClass.
#
# Usage:
#   packaging/linux/install-desktop-file.sh              # build, then install the dev entry
#   packaging/linux/install-desktop-file.sh --uninstall  # remove it again
#
set -euo pipefail

APP_ID="io.github.bizzehdee.bzTorrentClient"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PROJECT="$REPO_ROOT/bzTorrentClient.Avalonia/bzTorrentClient.Avalonia.csproj"
ASSETS="$REPO_ROOT/bzTorrentClient.Avalonia/Assets"
BINARY="$REPO_ROOT/bzTorrentClient.Avalonia/bin/Debug/net10.0/bzTorrentClient.Avalonia"

DATA_HOME="${XDG_DATA_HOME:-$HOME/.local/share}"
DESKTOP_DIR="$DATA_HOME/applications"
ICON_ROOT="$DATA_HOME/icons/hicolor"
DESKTOP_FILE="$DESKTOP_DIR/$APP_ID.desktop"

refresh_caches() {
    command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$DESKTOP_DIR" 2>/dev/null || true
    command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -f -t "$ICON_ROOT" 2>/dev/null || true
}

if [ "${1:-}" = "--uninstall" ]; then
    rm -f "$DESKTOP_FILE" \
          "$ICON_ROOT/256x256/apps/$APP_ID.png" \
          "$ICON_ROOT/128x128/apps/$APP_ID.png" \
          "$ICON_ROOT/scalable/apps/$APP_ID.svg"
    refresh_caches
    echo "Removed dev desktop entry and icons for $APP_ID."
    exit 0
fi

echo "Building the Debug binary..."
dotnet build "$PROJECT" -c Debug -v quiet
[ -x "$BINARY" ] || { echo "error: built binary not found at $BINARY" >&2; exit 1; }

echo "Installing icons..."
install -Dm644 "$ASSETS/bztorrent-256.png" "$ICON_ROOT/256x256/apps/$APP_ID.png"
install -Dm644 "$ASSETS/bztorrent-128.png" "$ICON_ROOT/128x128/apps/$APP_ID.png"
install -Dm644 "$ASSETS/bztorrent.svg"     "$ICON_ROOT/scalable/apps/$APP_ID.svg"

echo "Installing desktop entry..."
mkdir -p "$DESKTOP_DIR"
# Exec points at the local build; the packaged .desktop uses the installed `bztorrent-client`
# launcher instead. StartupWMClass must match the app's X11 WmClass so the shell links them.
cat > "$DESKTOP_FILE" <<EOF
[Desktop Entry]
Type=Application
Name=bzTorrent Client (dev)
GenericName=BitTorrent Client
Comment=A desktop BitTorrent client (local dev build)
Exec=$BINARY %U
Icon=$APP_ID
Terminal=false
Categories=Network;FileTransfer;P2P;
Keywords=torrent;bittorrent;p2p;download;magnet;
StartupNotify=true
StartupWMClass=$APP_ID
EOF
chmod 644 "$DESKTOP_FILE"

refresh_caches

echo
echo "Installed: $DESKTOP_FILE"
echo "  Exec -> $BINARY"
echo "The app should now appear in your app list, and the running window should show the"
echo "papaya icon in the dock/dash. Remove with: $0 --uninstall"
