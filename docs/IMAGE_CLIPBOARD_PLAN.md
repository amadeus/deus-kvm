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
- [ ] Hardware: same CleanShot copy → Windows Discord and Explorer, in either order.
- [ ] Hardware: same Snipping Tool copy → Mac Discord and Finder Cmd+V, in either order.
- [ ] Hardware: transparency, rapid copies, new local copy while downloading,
  unavailable LAN, lock/reconnect, sharing disabled, text and file regression checks.

Implementation and constraints:

- Screenshots download when their offer is accepted, so native paste works in
  applications without an app-specific key hook. Files retain on-demand transfer.
- Encoded images are capped at 32 MiB and decoded images at 40 million pixels.
  Clipboard content is not cached on disk; file output starts only on paste.
  Image transfer requires reachable
  LAN; no large-image fallback over Bluetooth.
- Mac supplies PNG/TIFF; Windows supplies PNG plus a white-backed CF_DIB fallback.
  Registered PNG retains transparency for Chromium/Electron.
- Both apps must be updated. Older peers continue text/file sharing without
  receiving the new image offers.

Local verification:

- Mac arm64 Debug build and 113 XCTest cases passed, including PNG/TIFF
  transparency, native named-pasteboard capture/privacy, and stale image offers.
- Windows Release solution builds for net48/net10.0 with zero warnings/errors.
  Portable tests: 182 passed, 2 Windows CNG tests skipped on macOS. The added
  net48 native OLE format tests compile but require a Windows test run.
- `scripts/test-file-network.sh` passed existing file regressions and new
  bidirectional multiblock in-memory image byte equality/no-file checks.
- SwiftLint strict, repository SwiftFormat, and whitespace checks passed.
- Extracted Mac ZIP passed deep/strict signature verification and contains only
  arm64. Windows packaging verified the x64 launcher, embedded archive/hash,
  and all 14 runtime files.

Test builds (both must be installed):

| Artifact | SHA-256 |
| --- | --- |
| `releases/DeusKVM-mac-arm64.zip` | `e16caa8deaeb93bcdf1787a89570ea6f4fae0e8e751b31e17f0cbd3ee7d6540e` |
| `releases/DeusKVM-Companion-win-x64-2026-10-20.exe` | `72d731a8bd6b85b1e6de58c97da5e6950b57ce7d8bb87828e341ff4be48b96ff` |

Automated checks do not establish Windows native clipboard behavior, screenshot
producer compatibility, Discord UI behavior, Bluetooth handoffs, or hardware
timing. User hardware findings are recorded below; commits do not mark the
remaining hardware items complete.

## CleanShot mixed-format correction — 2026-10-20

- User tested the updated apps: a fresh CleanShot copy pasted into Windows
  Explorer, but did nothing in Discord. The original Discord hardware check
  failed and remains open for the corrected build.
- Live Mac clipboard metadata showed one item with `public.file-url` and
  `public.png`. The original image-capture gate excluded `public.file-url`, so
  this combination was incorrectly sent as an ordinary file offer.
- Prefer explicitly advertised PNG/TIFF image data even when a file URL is
  present. Keep privacy markers excluded; file-URL-only copies stay files and
  do not cause image decoding or file-content prefetch.
- A named-pasteboard regression reproduced the failure for PNG+URL and TIFF+URL
  before the fix. Tests also cover URL-only and concealed mixed-format copies.
- After the fix, all 111 Mac tests, strict SwiftLint, SwiftFormat, and whitespace
  checks passed. The rebuilt Mac ZIP passed extracted-app signature verification
  and contains only arm64; its current hash is recorded above.
- Only the Mac app changes for this correction. Keep the Windows image-capable
  companion from the first build. Actual corrected CleanShot → Discord attachment
  preview still needs a hardware retest.

## Preserve image and file paste together — 2026-10-20

- User reported that Explorer paste stopped after the image-precedence fix,
  and requested the same image-or-file choice for Windows screenshots on Mac.
- Windows now publishes one OLE data object with PNG, CF_DIB, file descriptor,
  and file stream formats. File reads use the downloaded PNG in memory; regular
  file offers retain their existing network-backed behavior. Clipboard ownership,
  session revocation, and privacy markers cover both representations.
- Mac keeps the imported PNG/TIFF clipboard and installs the cached image with
  the existing Finder Cmd+V handler. File output uses an exclusive private partial
  in the chosen destination folder and an exclusive atomic rename. Cancellation
  removes the partial and filename collisions preserve the existing file.
- New portable tests cover repeated cached-image file reads, seeks/clones,
  disconnected-source independence, metadata validation, and revocation. Mac tests
  verify repeated file pastes preserve the native image clipboard, private file
  permissions, collision protection, and canceled-write cleanup.
- Windows-specific tests check the actual OLE object's combined image/file format
  enumeration and descriptor, and verify ordinary file objects remain file-only.
  They are compiled locally and scheduled by the existing Windows CI net48 test
  target; they have not been executed on this Mac.
- Both apps need this update for the complete two-direction behavior. All four
  hardware destination checks above remain open; do not mark them passed from
  unit tests or packaging.
- Verified fresh packages: extracted Mac app is arm64-only and passes deep/strict
  signature verification; Windows x64 launcher, embedded archive/hash, and 14
  runtime files pass the bundle verifier. Current hashes are in the table above.
