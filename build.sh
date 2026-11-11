#!/usr/bin/env bash
set -euo pipefail
export DEVELOPER_DIR="${DEVELOPER_DIR:-/Applications/Xcode.app/Contents/Developer}"
export PATH="/opt/homebrew/bin:$PATH"
cd "$(dirname "$0")"
ci_scripts/ci_post_clone.sh
# The same pinned lint tools as CI (ci_scripts/lint-tools.env), not whatever Homebrew has.
tools=$(ci_scripts/install-lint-tools.sh)
"$tools/swiftformat" --lint .
"$tools/swiftlint" lint --strict --no-cache
xcodebuild -project DeusKVM.xcodeproj -scheme DeusKVM -configuration Debug \
    -destination "platform=macOS" -derivedDataPath .build/DerivedData build | xcbeautify
