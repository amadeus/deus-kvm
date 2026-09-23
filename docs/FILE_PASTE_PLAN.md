# On-demand file paste prototype

- [x] Design: Mac to Windows, one regular file at a time, at most 10 MiB. Copy advertises metadata only. Native Explorer paste requests contents over existing paired Bluetooth control/bulk channels. No source deletion, staging download, folders, or network listener.
- [x] Implement metadata snapshots and bounded, deferred Mac file reads; invalidate on clipboard changes, disable, ownership loss, lock, or disconnect.
- [x] Implement Windows OLE virtual file clipboard, asynchronous paste and on-demand stream reads. Require an asynchronous extraction operation before sending file requests; suppress clipboard history/cloud formats.
- [x] Verify protocol boundaries, stale transfers, source changes, and build both platforms.
- [x] Package only current Windows x64 and Mac arm64 test ZIPs; commit verified implementation.
- [ ] Hardware checkpoint: Explorer keyboard/menu paste, no reads before paste, byte equality, zero-byte file, cancel, local clipboard replacement, ownership/disconnect, text and edge-switch regression.

Deferred: Windows to Finder native paste investigation; multiple files/folders; network transport; arbitrary destination applications. Explorer COM behavior and radio performance require real Windows hardware validation.

## Local verification and release receipts

- 79 macOS tests passed, including bounded source reads, changed/deleted sources,
  directory/symlink/size rejection and empty files. Strict SwiftLint passed.
- 120 .NET tests passed, including no requests from metadata inspection, paste
  permission gating, byte equality across blocks/seeks/clones, cancellation,
  stale epoch/offer rejection and malformed block failures.
- Windows Release solution build: zero warnings/errors. Both release publishes
  passed. The packaged Windows PE is x64; both ZIP CRC checks passed.
- Extracted Mac app: `lipo -archs` returned only `arm64`; strict/deep codesign
  verification passed. Neither app was launched during implementation.
- Explorer/OLE integration, actual native progress/cancel behavior, interaction
  with clipboard managers and radio throughput are **not hardware-validated**.
  Test one paste at a time. A failed/canceled offer requires a fresh source copy.

Current visible `releases/` contains only:

| Artifact | Bytes | SHA-256 |
| --- | ---: | --- |
| DeusKVM-mac-arm64-file-paste-2026-09-22.zip | 963233 | `3244c2bc755f12b62e3fe2b34174daf3a0a633c88d4023102a699b625d207214` |
| DeusKVM-Companion-win-x64-file-paste-2026-09-22.zip | 52405003 | `372232d2da0ae6a5a385a5519ee799f1094d9364dcab8f566a5fc6a410f6db96` |

Follow [FILE_PASTE_CHECKPOINT.md](FILE_PASTE_CHECKPOINT.md). Wait for user setup
and feedback before extending scope or claiming native integration success.

Native API references:
[Shell clipboard formats](https://learn.microsoft.com/en-us/windows/win32/shell/clipboard),
[asynchronous extraction](https://learn.microsoft.com/en-us/windows/win32/api/shldisp/nn-shldisp-idataobjectasynccapability).

Native ownership follow-up: OLE delayed rendering can advance the Win32 clipboard
sequence while retaining the same data object. File ownership therefore uses
`OleIsCurrentClipboard`, and the poller treats those sequence changes as imported
updates instead of external copies. A genuinely replaced clipboard still stops
reads. The Windows package is rebuilt after this adjustment.
