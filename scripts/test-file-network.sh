#!/usr/bin/env bash
# Exercises production Swift and C# network code. No pasteboard, Bluetooth or app launch.
set -euo pipefail
cd "$(dirname "$0")/.."
export DEVELOPER_DIR="${DEVELOPER_DIR:-/Applications/Xcode.app/Contents/Developer}"
export DOTNET_CLI_HOME="$PWD/.build/dotnet-home"
export NUGET_PACKAGES="$PWD/.build/nuget"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
smoke_dir="$(mktemp -d "$PWD/.build/file-network-smoke.XXXXXX")"
server_pid=""
trap 'if [[ -n "$server_pid" ]]; then kill "$server_pid" 2>/dev/null || true; wait "$server_pid" 2>/dev/null || true; fi; rm -rf "$smoke_dir"' EXIT
xcrun swiftc -module-cache-path .build/network-smoke/cache \
  DeusKVM/Companion/CompanionProtocol.swift DeusKVM/Clipboard/ClipboardTransfer.swift \
  DeusKVM/Clipboard/ClipboardFile.swift DeusKVM/Clipboard/FileNetworkCrypto.swift \
  DeusKVM/Clipboard/FileNetworkServer.swift DeusKVM/Clipboard/FileNetworkReceiver.swift scripts/tests/FileNetworkSmoke.swift -o "$smoke_dir/server"
"${DEUSKVM_DOTNET:-$PWD/.build/dotnet/dotnet}" build windows/tests/FileNetworkClient -c Release --disable-build-servers -m:1 -p:UseSharedCompilation=false
"$smoke_dir/server" "$smoke_dir" &
server_pid=$!
for ((i=0; i<100; i++)); do
  [[ -f "$smoke_dir/offer.json" ]] && break
  kill -0 "$server_pid"
  sleep 0.1
done
"${DEUSKVM_DOTNET:-$PWD/.build/dotnet/dotnet}" windows/tests/FileNetworkClient/bin/Release/net10.0/FileNetworkClient.dll "$smoke_dir"
