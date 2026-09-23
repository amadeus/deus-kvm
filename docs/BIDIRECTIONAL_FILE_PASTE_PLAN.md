# Network-only, bidirectional file paste

User authorized: retain text and metadata/authentication over paired encrypted
Bluetooth; send file contents only over LAN, cap one regular file at **2 GB
(2,000,000,000 bytes)**, and add Windows → Mac on-demand file paste. No contents
transfer at copy time. Preserve working Mac → Explorer native paste and input.

- [x] Phase 1: raise both limits, remove live Bluetooth file-content transport,
  report unavailable LAN, test size boundaries and no fallback.
- [x] Phase 2: user explicitly approved a Finder-only Cmd+V handler and DeusKVM
  progress/cancel UI. Native Finder file-promise paste is not assumed or claimed.
- [x] Phase 3: implement Windows source snapshots/revocation, authenticated LAN
  transport and Mac destination writes triggered by paste. Bounded memory, source
  validation, cancellation and collision handling are required.
- [x] Phase 4: cross-language socket and regression tests, including large files,
  stale clipboard/ownership, bad authentication and unavailable LAN.
- [x] Phase 5: package fresh Windows x64 and signed Mac arm64-only ZIPs, verify
  artifacts, update checkpoint, clean superseded releases and commit.
- [ ] Hardware: both directions, large file/hash, native paste behavior, cancel,
  network failure, clipboard replacement, disable/lock/ownership and input.

Previous checkpoint: user confirmed the network Mac → Windows paste was very fast.
Other hardware edge cases remain open; no measured hardware throughput recorded.


Implementation decisions:
- Files remain single regular files. **2 GB is decimal**, below the signed 32-bit
  metadata boundary; uint32 wire offsets and int64 native stream positions remain
  sufficient. No giant content buffers are allocated.
- Removed the Bluetooth file session/read path, including old-peer FILE_GET
  servicing. Old file-content message IDs are reserved. Text is unchanged.
- Reverse metadata uses `fileReceive: 1`; FILE_ACCEPT is authenticated paired
  Bluetooth metadata generated at paste. Windows dials the Mac in both directions.
  No new Windows firewall exception or inbound listening port.
- Finder Cmd+V is gated by local control, current remote clipboard ownership,
  Finder frontmost and absence of a text-editing field. No menu/right-click paste
  or other Mac application interception. Accessibility and Finder Automation
  permissions are needed. The event tap suppresses only that shortcut; folder
  lookup and all transfer work happen after the key callback returns.
- Finder insertion location comes from a constant AppleScript without source-name
  interpolation. Destination conflicts are rejected; an exclusive hidden partial
  becomes the final file via atomic exclusive rename, with no overwrite.
- Source metadata capture does not open file contents. Clipboard/epoch/session
  checks guard every block; filesystem metadata is checked before/after reads.
  Reverse source state is distinct from an imported virtual file's lifecycle.

Local verification:
- Both full **2,000,000,000-byte** directions matched SHA-256 using production
  Swift/C# socket code. Same-machine times including hash verification were
  4.466 s forward and 5.077 s reverse, not hardware/LAN performance claims.
  Sampled C# working sets were 66/71 MiB; no whole-file arrays were used.
- `scripts/test-file-network.sh`: 2 MiB + 37 byte equality, seeks, wrong-key
  rejection, source change, revocation, setup cancellation and network-only failure;
  reverse multi-block binary equality and partial cleanup.
- `scripts/test-reverse-file.sh`: empty source, collision/no overwrite, cancellation,
  source change, bad key and full 2 GB transfers. Failed reverse transfers leave
  neither final output nor partial files (pre-existing collision output preserved).
- Sparse source unit tests exercise exact maximum, tail, maximum+1, missing files,
  links and directories. Wire metadata tests cover Swift-compatible camelCase.
- Finder shortcut/metadata unit tests cover other apps, remote control, changed
  clipboard, modifier variations, path traversal and out-of-range sizes.

Apple's public file-promise documentation is oriented toward drag/drop; this
implementation uses the separately approved shortcut rather than depending on
unverified Finder native promise behavior:
[NSFilePromiseProvider](https://developer.apple.com/documentation/appkit/nsfilepromiseprovider).
The actual Finder/Explorer UI, permission prompts and Windows CF_HDROP capture still
require hardware validation and are not marked passed by the automated checks.


Final release verification:
- 83 Mac and 140 .NET tests passed; strict SwiftLint and `git diff --check` passed.
- Windows Release solution build: zero warnings/errors. Both release publishes
  succeeded; ZIP CRC and Windows x64 PE checks passed.
- Extracted Mac app contains only `arm64`; deep/strict signature verification
  passed. Packaged Finder Automation entitlement and usage description verified.
- Only the two new platform ZIPs remain in visible `releases/`. No production
  app was launched, restarted or substituted during implementation.

| Artifact | Bytes | SHA-256 |
| --- | ---: | --- |
| DeusKVM-Companion-win-x64-files-2gb-bidirectional-2026-09-22.zip | 52414582 | `fc9c3f0d6fe52d0e72dd8256ce99648d8725798d14549178dc177c7cc12e1e32` |
| DeusKVM-mac-arm64-files-2gb-bidirectional-2026-09-22.zip | 1017636 | `b1ce153ce06a72358fad636e7746c32dd67cb74a914bbd84181578b88371f034` |


Wait for user setup and the hardware checkpoint. In particular, do not describe
Finder keyboard interception/Automation or Windows Explorer source capture as
hardware-validated based on the socket tests or commit. Current instructions:
[FILE_PASTE_CHECKPOINT.md](FILE_PASTE_CHECKPOINT.md).
