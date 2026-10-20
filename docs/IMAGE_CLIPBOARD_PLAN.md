# Screenshot image clipboard

Goal: copy a screenshot with CleanShot on macOS or Snipping Tool on Windows,
then paste an image attachment into Discord on the other machine.

- [x] Trace the gap: existing capture handled text/file references, while imported
  files used Finder/Explorer-specific paste behavior.
- [x] Negotiate `images: 1` and carry `contentType: "image/png"` on file offers.
- [x] Capture native image data, bound size/dimensions, and transfer PNG in memory
  over the existing authenticated LAN channel in both directions.
- [x] Publish native image formats with imported/privacy markers; cancel stale
  work on clipboard replacement, session changes, disabled sharing, or new offers.
- [x] Verify native Mac format conversion/alpha and isolated pasteboard behavior,
  wire bounds, source immutability, transfer cancellation, and cross-language
  multiblock image transport without screenshot files.
- [x] Package and verify signed Mac arm64-only app and Windows x64 executable.
- [ ] Hardware: CleanShot → Windows Discord attachment preview.
- [ ] Hardware: Snipping Tool → Mac Discord attachment preview.
- [ ] Hardware: transparency, rapid copies, new local copy while downloading,
  unavailable LAN, lock/reconnect, sharing disabled, text and file regression checks.

Implementation and constraints:

- Screenshots download when their offer is accepted, so native paste works in
  applications without an app-specific key hook. Files retain on-demand transfer.
- Encoded images are capped at 32 MiB and decoded images at 40 million pixels.
  Clipboard content is not persisted to disk. Image transfer requires reachable
  LAN; no large-image fallback over Bluetooth.
- Mac supplies PNG/TIFF; Windows supplies PNG plus a white-backed CF_DIB fallback.
  Registered PNG retains transparency for Chromium/Electron.
- Both apps must be updated. Older peers continue text/file sharing without
  receiving the new image offers.

Local verification:

- Mac arm64 Debug build and 109 XCTest cases passed, including PNG/TIFF
  transparency, native named-pasteboard capture/privacy, and stale image offers.
- Windows Release solution builds for net48/net10.0 with zero warnings/errors.
  Portable tests: 180 passed, 2 Windows CNG tests skipped on macOS.
- `scripts/test-file-network.sh` passed existing file regressions and new
  bidirectional multiblock in-memory image byte equality/no-file checks.
- SwiftLint strict, repository SwiftFormat, and whitespace checks passed.
- Extracted Mac ZIP passed deep/strict signature verification and contains only
  arm64. Windows packaging verified the x64 launcher, embedded archive/hash,
  and all 14 runtime files.

Test builds (both must be installed):

| Artifact | SHA-256 |
| --- | --- |
| `releases/DeusKVM-mac-arm64.zip` | `09a74b1fea9a7e7201d7945e21bb6b00d5ad69a22db29ebfd6f48a37cc61f8c0` |
| `releases/DeusKVM-Companion-win-x64-2026-10-19.exe` | `6232f3f41454bf31411de830b47e88a0ef807cf089a6bfb82e6634666591200c` |

The Windows native clipboard APIs, actual screenshot producers, Discord UI,
Bluetooth handoffs, and hardware timing have not been exercised here. Automated
checks and commits do not mark those hardware items complete.
