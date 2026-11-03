#!/usr/bin/env bash
# Builds "Notch.app" into ./build.
#
#   bash scripts/build-app.sh            build only
#   bash scripts/build-app.sh --install  build, copy to /Applications and launch
#
# Variables are always written as ${NAME}: macOS ships bash 3.2, which otherwise reads a
# following non-ASCII character (like "…") as part of the variable name.
set -euo pipefail

cd "$(dirname "$0")/.."

APP_NAME="Notch"
EXECUTABLE="SDBackup"
# The app's name before it was called Notch; --install replaces it.
OLD_APP_NAME="SD Backup"
APP="build/${APP_NAME}.app"

if ! command -v swift >/dev/null 2>&1; then
    echo "error: Swift not found. Install the Xcode Command Line Tools with: xcode-select --install" >&2
    exit 1
fi

echo "==> Compiling (release)"
swift build -c release
BIN_DIR="$(swift build -c release --show-bin-path)"

echo "==> Assembling ${APP}"
rm -rf "${APP}"
mkdir -p "${APP}/Contents/MacOS" "${APP}/Contents/Resources"
cp "${BIN_DIR}/${EXECUTABLE}" "${APP}/Contents/MacOS/${EXECUTABLE}"
cp Resources/Info.plist "${APP}/Contents/Info.plist"
cp Resources/AppIcon.icns "${APP}/Contents/Resources/AppIcon.icns"

echo "==> Signing (ad hoc, for this Mac only)"
codesign --force --sign - "${APP}"

echo "Built ${APP}"

if [[ "${1:-}" == "--install" ]]; then
    echo "==> Installing to /Applications"
    pkill -x "${EXECUTABLE}" 2>/dev/null && sleep 1 || true
    if [[ -d "/Applications/${OLD_APP_NAME}.app" ]]; then
        echo "==> Removing /Applications/${OLD_APP_NAME}.app (the same app under its old name)"
        rm -rf "/Applications/${OLD_APP_NAME}.app"
    fi
    rm -rf "/Applications/${APP_NAME}.app"
    cp -R "${APP}" "/Applications/"
    open "/Applications/${APP_NAME}.app"
    echo "Installed and launched. Look for the Notch icon (an SD card with a check) in the menu bar."
fi
