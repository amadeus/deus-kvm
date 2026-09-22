#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
export DEVELOPER_DIR="${DEVELOPER_DIR:-/Applications/Xcode.app/Contents/Developer}"
mac_arch="${1:-arm64}"
case "$mac_arch" in
  arm64) ;;
  *) echo 'macOS builds support Apple Silicon (arm64) only' >&2; exit 1 ;;
esac
xcodegen generate
xcodebuild -project DeusKVM.xcodeproj -scheme DeusKVM -configuration Release \
  -destination 'generic/platform=macOS' -derivedDataPath .build/ReleaseDerivedData \
  ARCHS=arm64 ONLY_ACTIVE_ARCH=NO build
mac_app=".build/ReleaseDerivedData/Build/Products/Release/DeusKVM.app"
codesign --verify --deep --strict "$mac_app"
mkdir -p releases
ditto -c -k --sequesterRsrc --keepParent "$mac_app" "releases/DeusKVM-mac-$mac_arch.zip"
printf '%s\n' "$PWD/releases/DeusKVM-mac-$mac_arch.zip"
