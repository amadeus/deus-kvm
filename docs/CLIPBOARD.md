# Clipboard sharing

Clipboard sharing is enabled by default in **Mac Settings → Share clipboard with Windows**. The toggle controls both directions. Both machines need this
build; the Windows service and signed-in desktop worker handle sharing even
when the tray window is closed. Keep the existing Bluetooth pairing.

## Text clipboard test

1. Install the current Windows ZIP from the visible `releases/` folder; its
   update flow replaces the service and desktop worker while preserving settings.
2. Quit the Mac app and launch the app from the current arm64 ZIP in `releases/`.
3. Copy a sentence on the Mac, cross to Windows and paste into Notepad. Copy a
   different sentence in Notepad, return to the Mac and paste into a text editor.
4. Repeat with emoji, accented characters, multiple lines and approximately
   20 KB of text. Check that typing and edge return stay responsive during
   transfer. BLE transfer time needs measurement on the actual hardware.
5. Make several successive copies in both directions. A newer local copy must
   not be replaced by an older transfer, and switching back and forth without
   copying must not cause clipboard feedback loops.
6. Disable sharing and copy fresh text; it must stay local. Re-enable and copy
   again. Test reconnect and lock/unlock with new text. The clipboard must not
   synchronize on the login/security screen.
7. Using disposable test text, check a password manager that marks its clipboard
   private. Confirm that private text, images and text over 64 KiB do not
   replace the other machine's clipboard.

The handoff-only Mac update is tested using isolated named pasteboards, including
native reads/writes and stale-write rejection; the user's clipboard is untouched.
Bluetooth timing, real handoffs and Finder/Explorer integration still require the
hardware checks above. Automated tests do not establish live end-to-end behavior.

## Behavior and limits

- Plain Unicode text crosses in both directions, up to **65,536 UTF-8 bytes after newline
  normalization**. Rich copies may supply their plain-text representation;
  formatting and images are not transferred. File-list clipboards are excluded
  from text sharing, even if they also contain a text representation.
- Mac copies stay local until switching to Windows. At handoff, DeusKVM reads
  the latest clipboard and offers its text/file metadata, including menu and
  right-click copies. Connecting alone does not publish the Mac clipboard.
  Windows offers continue to arrive through the companion; text is fetched when
  the Mac becomes active. File contents transfer only when pasted.
- Transfers run asynchronously; switching does not wait for the clipboard.
  The Mac has no repeating clipboard poll. It reads for initial session setup,
  handoff and validation of incoming writes; operation-specific retries stop
  after at most two seconds. Large text may take time before a paste sees it.
  Text over the cap is skipped rather than truncated.
- Empty text is supported. Embedded NUL and malformed Unicode are rejected.
  Newlines are LF on the wire and CRLF when written to Windows.
- Imported revisions are recorded and marked so they are not echoed back. A
  fresh local clipboard change cancels a pending import. A busy clipboard write
  is retried for up to two seconds; a newer local revision takes precedence.
- Only the Mac's selected, allowed PC can exchange clipboard messages. Sharing
  pauses on Windows lock/secure desktop/logout, Mac sleep/session deactivation
  or secure input, and when the user disables it. Old-session pending payloads
  are discarded. Copy again after reconnect/unlock to establish a fresh offer.
- Known private, transient and generated clipboard markers are inspected before
  reading text. This is metadata filtering, not content classification:
  **unmarked password text cannot be distinguished from ordinary text**.
- DeusKVM does not log or persist clipboard contents. Windows imports also
  opt out of cloud clipboard upload. The OS and other installed clipboard tools
  still govern their own history behavior.

## Implementation

`ClipboardTransfer.swift` and `ClipboardTransfer.cs` implement matching pure
state machines. HELLO's optional `clipboard:1` capability gates the extension;
older companions still switch normally without clipboard sharing.

The Windows service owns an epoch and advertises availability with CLIP_STATE.
The Mac acknowledges only after priming its local snapshot and checking its
sharing preference/session. Windows then primes its desktop snapshot before
handling a pending remote offer. This ordering prevents a first snapshot from
cancelling an import that started during connection setup.

All integers below are little-endian. Control messages fit one 20-byte frame.

| Message | Stream | Payload |
| --- | --- | --- |
| CLIP_GRAB (0x20) | 0 | epoch u32, copy sequence u32, byte count u32; 0xffffffff withdraws |
| CLIP_GET (0x21) | 0 | epoch u32, copy sequence u32, byte offset u32 |
| CLIP_DATA (0x22) | 1 | epoch u32, copy sequence u32, byte offset u32, 0–1024 bytes |
| CLIP_STATE (0x23) | 0 | epoch u32, availability/enabled u8 |

The receiver requests one 1 KiB block at a time; multi-frame blocks use the
existing CRC-32C framing. A missing block request retries after five seconds,
up to three times. Control traffic retains priority over bulk. No HID descriptor,
mouse/keyboard report logic, third stream or pairing change is involved.

The Windows BLE worker coordinates the transfer but does not access the
Session 0 clipboard. `DesktopClipboard` uses a dedicated STA thread within the
signed-in desktop worker, isolated from the Raw Input STA. `ClipboardPasteboard`
uses a dedicated Mac queue, isolated from the main/input run loops. Native
revision checks protect a newer local copy even if it happens before polling
notices it. Clipboard owners can delay rendering; these reads cannot stall the
input loop. The Mac pasteboard API does not provide an atomic compare-and-write,
so an external write exactly between its final revision check and replacement
remains a native API race.

Tests cover shared wire fixtures, Unicode/newline/empty payloads, block boundaries,
20 KB/64 KiB transfers, caps, corruption/malformed input, stale epochs, local-copy
cancellation, duplicate offers, retries, echo suppression, revision tracking,
privacy policy and the bounded desktop pipe envelope. They do not read either
machine's actual clipboard.

Privacy conventions: [NSPasteboard community types](https://nspasteboard.org/),
[Windows clipboard formats](https://learn.microsoft.com/en-us/windows/win32/dataxchg/clipboard-formats).
Native memory ownership follows
[SetClipboardData](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setclipboarddata).

## On-demand file paste

The sharing toggle supports **one regular file up to 2 GB (2,000,000,000 bytes)**
in both directions. Text and file metadata/authentication use Bluetooth. File
contents use authenticated local IPv4 networking only, in blocks up to 256 KiB.
There is no Bluetooth file fallback or pre-download. Mac → Windows keeps the
native Explorer virtual-file stream; Windows → Mac uses Finder Cmd+V with
DeusKVM progress/cancel UI, as authorized by the user. Other Finder paste commands
and destination apps are not intercepted.

`files: 1` and `fileNetwork: 1` remain in HELLO; optional `fileReceive: 1` negotiates
reverse metadata support. FILE_OFFER (0x24, bulk JSON, at most 4096 bytes) carries
epoch, source clipboard revision, text offer sequence, filename and size. On a Mac
source it also carries local addresses, ephemeral port and a per-offer key.
Old FILE_GET/FILE_DATA IDs (0x25/0x26) are reserved but no longer serve file contents.

Windows file copying reads CF_HDROP metadata for one regular, non-link file. Paste
on the Mac creates a temporary listener and sends FILE_ACCEPT (0x27, bulk JSON,
4096-byte maximum) with the original source identity and fresh Mac endpoint/key.
The Windows desktop worker validates the still-current source and connects out;
Windows never opens an incoming listener. The existing handshake derives separate
AES-GCM keys for connector and listener; in the reverse flow, the Mac listener
sends encrypted offset/count requests and the Windows connector sends responses.

Copying/metadata inspection never opens source contents. Mac destination writes
begin only after a valid authenticated response, to an exclusive hidden partial
file in the chosen Finder folder. Exclusive atomic rename finishes the paste and
cannot overwrite a same-name file. Failure/cancel removes the partial. The source
is never removed. The reverse file never becomes a downloadable temporary
clipboard URL.

Epoch, clipboard revision, local-copy precedence, disabled/locked/ownership gates
apply in both directions. File contents stay off input loops. See
[FILE_PASTE_CHECKPOINT.md](FILE_PASTE_CHECKPOINT.md) for setup and hardware checks,
and [BIDIRECTIONAL_FILE_PASTE_PLAN.md](BIDIRECTIONAL_FILE_PASTE_PLAN.md) for tests.
