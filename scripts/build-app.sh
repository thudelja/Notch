#!/usr/bin/env bash
# Builds "SD Backup.app" into ./build.
#
#   ./scripts/build-app.sh            build only
#   ./scripts/build-app.sh --install  build, copy to /Applications and launch
set -euo pipefail

cd "$(dirname "$0")/.."

APP_NAME="SD Backup"
EXECUTABLE="SDBackup"
APP="build/$APP_NAME.app"

if ! command -v swift >/dev/null 2>&1; then
    echo "error: Swift not found. Install the Xcode Command Line Tools with: xcode-select --install" >&2
    exit 1
fi

echo "→ Compiling (release)…"
swift build -c release
BIN_DIR="$(swift build -c release --show-bin-path)"

echo "→ Assembling $APP…"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN_DIR/$EXECUTABLE" "$APP/Contents/MacOS/$EXECUTABLE"
cp Resources/Info.plist "$APP/Contents/Info.plist"

echo "→ Signing (ad-hoc, for this Mac only)…"
codesign --force --sign - "$APP"

echo "✓ Built $APP"

if [[ "${1:-}" == "--install" ]]; then
    echo "→ Installing to /Applications…"
    pkill -x "$EXECUTABLE" 2>/dev/null && sleep 1 || true
    rm -rf "/Applications/$APP_NAME.app"
    cp -R "$APP" "/Applications/"
    open "/Applications/$APP_NAME.app"
    echo "✓ Installed and launched. Look for the SD card icon in the menu bar."
fi
