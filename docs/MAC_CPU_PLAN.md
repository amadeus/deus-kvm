# Mac CPU optimization checkpoint

Working baseline: user confirmed two-way file paste works. Preserve input reports,
forwarding rate, low Bluetooth latency, edge/hotkey return, ownership and clipboard.
User reports ~0.5–0.6% local idle, ~2% local motion, up to ~10% remote motion;
remote CPU drops again when stationary. Settings-window visibility not established.

- [x] Inspect current paths and sample the running signed app without restarting.
- [x] Remove unconditional 50 Hz tap polling; schedule only pending handoff or
  return-probe work while preserving release-before-switch ordering.
- [x] Avoid redundant remote cursor warps when already parked; preserve initial
  disassociation, corrective warps, return warp/reassociation and report rate.
- [x] Avoid unchanged published companion state and allow background timer leeway.
- [x] Verify scheduling/cursor policy, existing input/clipboard regressions, build
  signed arm64-only Mac and Windows x64 packages, commit.
- [ ] User comparison: local idle/motion and remote idle/motion, with the same
  settings-window state and pointing device; edge/hotkey/held-input return, clipboard.

Evidence: `sample 51015 15 1` and concurrent `top` captured the installed app.
CPU samples included 0.6–1.0% early in the mixed window, and 9.7/10.8% peaks.
The tap stack repeatedly enters SLSWarpCursorPosition from InputTap.swift:266;
34 of 41 samples under its main handle-input branch were in this warp path.
These stack counts are not CPU percentages. The window included both local and
remote activity, so it is not a controlled per-state performance benchmark.
Source also confirms a 20 ms periodic tap timer regardless of pending work and
unconditional writes of unchanged @Published companion blind/status values.
Raw measurements stay in ignored `.build/cpu-baseline.sample.txt` and
`.build/cpu-baseline-top.txt`; no input contents were recorded.

## Edge-return regression (priority checkpoint)

User reports intermittent failure to return from Windows at the edge; the hotkey
still works, and it happens outside file transfers. CPU edits were not installed
when reported. Installed baseline capture logs show working edge returns, two
hotkey returns, and one earlier companion heartbeat expiry. They do not identify
the cause of the failed edge attempts.

- [x] Inspect Windows edge detection/session/configuration and Mac return guards.
- [x] Fix negative mouse-identity results persisting indefinitely: retry during
  bounded desktop recovery/periodic device refresh while no matching Mac mouse is
  known; never substitute another mouse or perform PnP lookup per motion packet.
- [x] Add aggregate Windows edge diagnostics (`%LOCALAPPDATA%\DeusKVM\edge-return.log`)
  at most every five seconds, off the raw-input thread, with bounded log rotation.
  Mac capture logs include companion blind state and throttled held-input return
  rejections. No key contents, coordinates, filenames or device identifiers.
- [x] Tests prove a transient miss recovers without reconnect, packet-rate probes
  stay cached, removed handles invalidate, and another mouse is not substituted.
- [ ] Hardware: repeated edge returns, reconnect either Mac, two Macs paired,
  hotkey recovery, normal/held-input crossing and two-way clipboard smoke tests.

The recovery defect is confirmed in source and unit tests; whether it caused the
user's intermittent edge issue remains unconfirmed. If it recurs, collect the
Windows edge log plus matching Mac Capture logs before changing more input logic.

## Local verification and delivery

2026-09-23: strict SwiftLint and `git diff --check` pass. All 86 Mac tests and
143 Windows core tests pass. Release builds succeed. The extracted Mac app is
signed and passes `codesign --verify --deep --strict`; `lipo -archs` reports only
`arm64`. The Windows EXE is x86-64; the ZIP passes CRC validation and its EXE
matches the published binary. `bash -n windows/publish.sh` passes.

Hardware edge recovery and before/after CPU results remain pending. No running
app was replaced or restarted. Test sequence: `docs/CPU_EDGE_CHECKPOINT.md`.
Only the current two ZIPs remain in visible `releases/`.

- `DeusKVM-mac-arm64-cpu-edge-2026-09-23.zip` — 1,021,595 bytes; SHA-256 `02d0f5c83a79ebb2a0b3a9e9948cb9bedac2c6a22fc86044bcb2ca66ea0cf1b7`.
- `DeusKVM-Companion-win-x64-cpu-edge-2026-09-23.zip` — 52,416,716 bytes; SHA-256 `cc39241cc4525b19297d33519c23d0709ecceca852bc8c37ad54d62e9cf97750`.

## Phase 2: remove unnecessary polling

- [x] Replace the always-on 0.5-second coordinator refresh with Bluetooth,
  display, settings, app/session activation and capture lifecycle events.
- [x] Reset a one-shot 10-second link deadline on received traffic. Normal
  heartbeats only rearm it; they do not trigger permission/status recomputation.
- [x] Schedule text clipboard retries only while awaiting a block, cancel on
  completion, local replacement, timeout or session reset.
- [x] Stop permission-view polling once authorized; retain retries during setup.
- [x] Preserve remote safety checks (0.5 s), blocked capture recovery (1 s),
  and clipboard change-count observation (200 ms while sharing). These still
  require checks; ready local capture no longer polls status/permissions.
- [x] Run regression tests/lint, sign and verify arm64 package, commit.
- [ ] Hardware: idle CPU with settings closed and open, permissions/settings
  changes, secure-input recovery, link loss/reconnect, edge/hotkey switching,
  plain text/file clipboard in both directions.

macOS NSPasteboard has no public change notification. Chromium observes private
`_CFPasteboardCache` internals instead; we retain the existing clipboard monitor
rather than introduce a private runtime hook or miss menu/programmatic copies.
Reference: https://chromium.googlesource.com/chromium/src/+/lkgr/base/mac/pasteboard_changed_observation.mm
Secure-input checks also guard actual clipboard reads/writes/file chunks so
removing idle coordinator refresh cannot bypass that boundary.

Phase 2 local verification (2026-09-23): all 90 Mac tests pass, strict SwiftLint
and `git diff --check` pass, Release build succeeds. Extracted package passes
`codesign --verify --deep --strict`; architecture is exactly `arm64`. No Windows
code changed; retain the tested cpu-edge Windows ZIP. No running app was replaced.

Current Mac artifact: `DeusKVM-mac-arm64-idle-events-2026-09-23.zip` — 1,026,880 bytes;
SHA-256 `98bc646f41fea3a1b0ee5e3a205e3a63f7415f7034c386e008f51e6f21e31d9a`. Supersedes the Mac cpu-edge ZIP above; only the current
Mac and Windows archives remain in `releases/`. Hardware results remain pending.
