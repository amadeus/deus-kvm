# On-demand file paste test

Use these two builds from the visible `releases/` folder:

- `DeusKVM-mac-arm64-file-paste-2026-09-22.zip`
- `DeusKVM-Companion-win-x64-file-paste-progress-2026-09-22.zip`

For the transfer-progress diagnostics, update **Windows only**; keep the existing file-paste
Mac build. For a first installation, quit the old Mac app and launch the new
arm64 app. On Windows, close the old
tray UI, extract the ZIP into a fresh folder and launch `DeusKVM.Companion.exe`;
its update flow replaces the installed service/worker. Keep existing pairings.
The Mac build is Apple Silicon only. Keep **Settings → Share clipboard with
Windows** enabled.

## First test

1. In Finder on the enabled Mac, copy **one small ordinary file** (for example,
   a 1–10 KB text file or a small PNG) with Cmd+C. Copy the file itself, not text
   inside it. No contents should transfer yet.
2. Switch to Windows and open an ordinary folder in File Explorer. Press Ctrl+V
   once. This is the first native integration check: the file should transfer
   into that folder as part of the paste, with no preliminary download step.
3. Open the result and check its contents. Repeat with Explorer's right-click
   Paste command. Note any error text or whether Paste is disabled.
4. Copy a different small file and leave it unpasted; copying alone must not
   fetch its contents. Then copy text and confirm normal text paste still works.

## Follow-up checks

- Compare SHA-256 on a binary file: `shasum -a 256 /path/to/file` on Mac and
  `Get-FileHash -Algorithm SHA256 'C:\path\to\file'` in Windows PowerShell.
- Try an empty file, then a larger file. Bluetooth will be slow: start small.
- Check mouse, typing, edge return and hotkey return during a transfer.
- Cancel Explorer's copy. A canceled/failed offer may require copying the source
  again before retrying. Check that input and subsequent text/file copies recover.
- Change/delete the source after copying it, or disable the Mac/disconnect/change
  ownership during paste. The copy must fail, never silently complete with mixed
  or missing bytes. Remove any incomplete destination left by Explorer.
- Copy something locally on Windows before pasting; it must supersede the remote
  offer. Check with any clipboard-history utility you normally run that copying
  alone does not start a transfer. Both synchronous and asynchronous stream reads are supported. Metadata
  inspection does not fetch contents, but a third-party clipboard manager that
  explicitly reads file contents can trigger a transfer, just like a paste.

Prototype limits: **Mac → Windows Explorer only**, one regular file, **10 MiB
maximum**, no folders, symlinks, multi-selection, cut/move, or reverse file paste.
Use a Windows-compatible filename. Unsupported file copies are not offered.
Copying sends metadata only. Contents are requested in 1 KiB blocks over the
existing paired Bluetooth channel, with a 15-second per-block timeout. There is
no temporary pre-download folder, network listener, or cloud service. The source
must remain available, unchanged, and on the Mac clipboard until paste completes.
Explorer owns the destination and its native copy UI.

**Hardware validation is pending.** Passing unit tests/builds does not establish
that Explorer calls the asynchronous COM interfaces in the expected order, that
its progress/cancel UI behaves correctly, or that the radio performance is good.

## Access Denied follow-up

The initial prototype rejected stream reads that were not preceded by the
optional asynchronous-start callback. The replacement accepts synchronous native
reads as well. Clipboard ownership is checked on stream callbacks using the
published clipboard owner window; OLE identity checks remain on the clipboard
STA. Session, disable, lock, clipboard replacement and source validity checks
remain in place. The exact original rejection was not captured on hardware.

Copy the file again on the Mac after updating Windows; do not retry the stale
clipboard entry. If it still fails, the fixed-event diagnostics are in
`%LOCALAPPDATA%\DeusKVM\file-paste.log`. They contain no file names, paths or
contents. Report the entries for that attempt and the exact Windows error.

## 1.9 MB transfer diagnostics

The supplied first log shows native success for the small file and cancellation
roughly 24 seconds into the larger attempt. It contains no byte counts, so it
cannot establish throughput or prove the larger transfer stalled. The latest
Windows build adds received-byte totals, per-block timeout/source failure reasons,
and the sizes and durations of Explorer's stream reads. Normal progress events
are rate-limited; names, paths and contents are not recorded.

After updating Windows, copy the 1.9 MB file again in Finder and paste once into
an ordinary Explorer folder. Keep the source file/clipboard unchanged and leave
DeusKVM enabled on that Mac. Allow about **60 seconds** unless Windows reports
an error sooner, then cancel if it still shows no useful progress. Send the new
`%LOCALAPPDATA%\DeusKVM\file-paste.log` (and `.previous` if rotated). This is a
measurement build; the transport, block size and timeout have not been changed.
