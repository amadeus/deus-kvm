# On-demand file paste prototype

- [x] Design: Mac to Windows, one regular file at a time, at most 10 MiB. Copy advertises metadata only. Native Explorer paste requests contents over existing paired Bluetooth control/bulk channels. No source deletion, staging download, folders, or network listener.
- [x] Implement metadata snapshots and bounded, deferred Mac file reads; invalidate on clipboard changes, disable, ownership loss, lock, or disconnect.
- [x] Implement Windows OLE virtual file clipboard, asynchronous paste and on-demand stream reads. Fetch only when the native file stream is read (optional async callbacks are not authorization); suppress clipboard history/cloud formats.
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

Initial prototype release receipts (Windows build superseded by the Access Denied fix below):

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

## Access Denied follow-up — 2026-09-22

User hardware result: Windows Explorer displayed "Error Copying File or Folder"
with "Access Denied". The transfer checkpoint has **not passed**.

Source inspection found explicit STG_E_ACCESSDENIED rejections for a missing
optional async-start callback and for OLE identity queried by a stream callback.
The original build did not record which branch fired; the precise hardware cause
is not confirmed. Microsoft's API documents async extraction as optional.

- [x] Accept native synchronous reads without requiring StartOperation. Metadata
  queries and creation of a stream still do not request any file contents.
- [x] Keep OLE identity checks on the clipboard STA. Stream callbacks use the
  installed clipboard-owner HWND plus the existing session/desktop checks.
- [x] Add bounded fixed-event diagnostics without file names, paths or contents.
- [x] Run regression tests/build; package and verify replacement Windows x64 ZIP.
- [x] Commit fix and record final release receipt.
- [x] User confirms native Explorer paste works with a small file after the Windows fix.

Native on-demand semantics: a consumer actually reading contents starts transfer.
DeusKVM never pre-downloads files or renders contents for metadata inspection.
A third-party clipboard manager that explicitly reads the stream can initiate
transfer; the optional async API is not a reliable way to identify user intent.
Strict user-action detection across arbitrary clipboard apps remains unproven.

Reference: [optional async extraction](https://learn.microsoft.com/en-us/windows/win32/api/shldisp/nn-shldisp-idataobjectasynccapability).

Access Denied fix validation: 124 .NET tests passed; Release build and x64
publish passed. ZIP CRC, expected four archive entries and x64 PE checks passed.
The Mac ZIP is unchanged (SHA-256 verified against its original receipt).
Only the current Mac ZIP and replacement Windows ZIP remain in `releases/`.

`DeusKVM-Companion-win-x64-file-paste-fix-2026-09-22.zip` — 52406205 bytes;
SHA-256 `4b93c5e1f09687ad705564e2774515de0c11e0f6910ff23937824bad8a41d048`.

Native paste success remains pending a fresh user test.

## Larger-file investigation — 2026-09-22

Hardware feedback after the Access Denied fix: a small file pasted successfully.
A **1.9 MB** file showed a loader without apparent progress, and the user canceled
it. No Windows transfer error or timeout was reported for that attempt. This is
below the 10 MiB cap. Do not label it a confirmed protocol failure or timeout.

- [x] Record small-file native paste success without closing the larger-file,
  cancellation, clipboard-manager or input-regression hardware checks.
- [x] Add a 2 MiB + 37 byte stream/protocol regression covering 1 MiB consumer
  reads, 1 KiB file blocks, repeated wire sequence wrap and a partial final block.
  All 125 .NET tests passed; the resulting bytes exactly match the source.
- [ ] Receive the user's Windows diagnostics and determine whether transfer was
  advancing while Explorer awaited a large read, or had stopped for another reason.
- [ ] Reproduce/fix the confirmed cause and prepare a replacement build if needed.

The local test uses immediate simulated delivery; it does not measure Bluetooth
throughput, native Explorer progress or COM responsiveness. Production behavior
and release ZIPs are unchanged. User is collecting `file-paste.log`; wait for that
evidence before changing timeouts or replacing the transport.

## Supplied log analysis and progress instrumentation — 2026-09-22

Read `/Users/amadeus/Downloads/file-paste.log`. The small attempt started at
05:47:39.365 UTC and ended at 05:47:39.417 with S_OK. The larger attempt started
at 05:47:53.792 and ended at 05:48:17.951 with 0x800704C7 (user cancellation),
about 24.16 seconds later. Both used async extraction. Thus the earlier missing
async callback hypothesis does not explain these recorded attempts. A later
session-unavailable rejection at 05:49:12 is after the cancellation and is not
evidence of what caused the original apparent stall.

The log has no byte counts, block timing or stream request size. No measured
throughput, pre-cancel timeout or original failure cause can be inferred from it.

- [x] Interpret supplied lifecycle evidence and retain unknowns explicitly.
- [x] Add rate-limited byte progress, consumer read size/duration and bounded
  failure classification to Windows diagnostics; keep filenames/content out.
- [x] Test, publish, verify and commit the Windows-only measurement build.
- [ ] User repeats the 1.9 MB paste and supplies byte-progress diagnostics.
- [ ] Use the measurement to choose a targeted fix or transport improvement.

No timeout, Bluetooth framing, source reads, input behavior or paste trigger is
changed. The current Mac ZIP is retained unchanged.

Measurement build verification: 127 .NET tests passed; solution Release build
and self-contained x64 publish passed. ZIP CRC, four expected entries and PE
architecture checks passed. Mac ZIP SHA-256 remains unchanged. Only the latest
Windows measurement ZIP and existing Mac ZIP remain in `releases/`.

`DeusKVM-Companion-win-x64-file-paste-progress-2026-09-22.zip` — 52407682 bytes;
SHA-256 `935ffa0dbdb5ca38445cadf1b68e53ffbf0b905860a657dec5a073a1396daf0a`.

This is instrumentation, not a claimed fix for larger-file progress. Await the
user's next 1.9 MB attempt and log before changing production transfer behavior.


## User-confirmed flow and network follow-up — 2026-09-22

The user confirms Mac-to-Windows native paste works and wants the same interaction
with faster networking. This supersedes the pending Bluetooth measurement request;
no precise Bluetooth throughput was established. The full hardware checklist above
is still open. Track the authorized network implementation and new release receipts
in [NETWORK_FILE_PASTE_PLAN.md](NETWORK_FILE_PASTE_PLAN.md), with current user
instructions in [FILE_PASTE_CHECKPOINT.md](FILE_PASTE_CHECKPOINT.md).
