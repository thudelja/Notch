#!/usr/bin/env bash
# Builds "Notch.app" into ./build.
#
#   bash scripts/build-app.sh                  build for this Mac
#   bash scripts/build-app.sh --install        build, copy to /Applications and launch
#   bash scripts/build-app.sh --universal      build for Apple silicon and Intel in one app
#   bash scripts/build-app.sh --dmg            also make build/Notch.dmg (drag to Applications)
#   bash scripts/build-app.sh --version 1.2.0  set the version shown in Finder
#
# Releases are built by CI with: --universal --dmg --version <tag>.
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
DMG="build/${APP_NAME}.dmg"

INSTALL=0
UNIVERSAL=0
MAKE_DMG=0
VERSION=""
while [[ $# -gt 0 ]]; do
    case "$1" in
        --install) INSTALL=1 ;;
        --universal) UNIVERSAL=1 ;;
        --dmg) MAKE_DMG=1 ;;
        --version) VERSION="${2:?--version needs a value, like 1.2.0}"; shift ;;
        *) echo "error: unknown option $1 (see the top of this script)" >&2; exit 1 ;;
    esac
    shift
done

if ! command -v swift >/dev/null 2>&1; then
    echo "error: Swift not found. Install the Xcode Command Line Tools with: xcode-select --install" >&2
    exit 1
fi

BUILD_FLAGS=(-c release)
if [[ ${UNIVERSAL} == 1 ]]; then
    BUILD_FLAGS+=(--arch arm64 --arch x86_64)
fi

echo "==> Compiling (release$([[ ${UNIVERSAL} == 1 ]] && echo ", Apple silicon and Intel"))"
swift build "${BUILD_FLAGS[@]}"
BIN_DIR="$(swift build "${BUILD_FLAGS[@]}" --show-bin-path)"

echo "==> Assembling ${APP}"
rm -rf "${APP}"
mkdir -p "${APP}/Contents/MacOS" "${APP}/Contents/Resources"
cp "${BIN_DIR}/${EXECUTABLE}" "${APP}/Contents/MacOS/${EXECUTABLE}"
cp Resources/Info.plist "${APP}/Contents/Info.plist"
cp Resources/AppIcon.icns "${APP}/Contents/Resources/AppIcon.icns"
# English and Slovenian (macOS shows the one the Mac, or Notch's Language setting, asks for).
cp -R Resources/*.lproj "${APP}/Contents/Resources/"
if [[ -n ${VERSION} ]]; then
    plutil -replace CFBundleShortVersionString -string "${VERSION}" "${APP}/Contents/Info.plist"
fi

echo "==> Signing (ad hoc)"
codesign --force --sign - "${APP}"

echo "Built ${APP} ($(lipo -archs "${APP}/Contents/MacOS/${EXECUTABLE}"))"

if [[ ${MAKE_DMG} == 1 ]]; then
    echo "==> Making ${DMG}"
    STAGING="build/dmg"
    rm -rf "${STAGING}" "${DMG}"
    mkdir -p "${STAGING}"
    cp -R "${APP}" "${STAGING}/"
    # A shortcut to Applications, so installing is a drag across the window.
    ln -s /Applications "${STAGING}/Applications"
    hdiutil create -volname "${APP_NAME}" -srcfolder "${STAGING}" -ov -format UDZO "${DMG}" >/dev/null
    rm -rf "${STAGING}"
    echo "Built ${DMG}"
fi

if [[ ${INSTALL} == 1 ]]; then
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
