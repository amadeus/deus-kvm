#!/usr/bin/env bash
# Builds the Apple Silicon release, signed with the team's Developer ID so any Mac can open it,
# and notarizes and staples it when a notarytool keychain profile exists (DEUSKVM_NOTARY_PROFILE,
# default "deuskvm"; create it once with `xcrun notarytool store-credentials deuskvm`).
# Without a Developer ID certificate it falls back to the development signing; without the
# profile it skips notarization and says so (other Macs then show the Gatekeeper prompt).
set -euo pipefail
cd "$(dirname "$0")/.."
export DEVELOPER_DIR="${DEVELOPER_DIR:-/Applications/Xcode.app/Contents/Developer}"
mac_arch="${1:-arm64}"
case "$mac_arch" in
  arm64) ;;
  *) echo 'macOS builds support Apple Silicon (arm64) only' >&2; exit 1 ;;
esac
team="UHD99KF9X7"
profile="${DEUSKVM_NOTARY_PROFILE:-deuskvm}"
signing=()
if security find-identity -v -p codesigning | grep -q "Developer ID Application: .*($team)"; then
  signing=(CODE_SIGN_STYLE=Manual "CODE_SIGN_IDENTITY=Developer ID Application" DEVELOPMENT_TEAM="$team"
    "OTHER_CODE_SIGN_FLAGS=--timestamp")
else
  echo "No Developer ID Application certificate for $team: using development signing (Gatekeeper will warn on other Macs)" >&2
fi
xcodegen generate
xcodebuild -project DeusKVM.xcodeproj -scheme DeusKVM -configuration Release \
  -destination 'generic/platform=macOS' -derivedDataPath .build/ReleaseDerivedData \
  ARCHS=arm64 ONLY_ACTIVE_ARCH=NO "${signing[@]}" build
mac_app=".build/ReleaseDerivedData/Build/Products/Release/DeusKVM.app"
codesign --verify --deep --strict "$mac_app"
mkdir -p releases
if [[ ${#signing[@]} -gt 0 ]] && xcrun notarytool history --keychain-profile "$profile" >/dev/null 2>&1; then
  upload="$(mktemp -d)/DeusKVM.zip"
  ditto -c -k --keepParent "$mac_app" "$upload"
  xcrun notarytool submit "$upload" --keychain-profile "$profile" --wait
  xcrun stapler staple "$mac_app"
  spctl --assess --type execute --verbose=2 "$mac_app"
else
  echo "Notarization skipped (no keychain profile \"$profile\"): run" \
    "xcrun notarytool store-credentials $profile --apple-id <Apple ID> --team-id $team" >&2
fi
ditto -c -k --sequesterRsrc --keepParent "$mac_app" "releases/DeusKVM-mac-$mac_arch.zip"
printf '%s\n' "$PWD/releases/DeusKVM-mac-$mac_arch.zip"
