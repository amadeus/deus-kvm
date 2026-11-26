# Clipboard sharing

Clipboard sharing is enabled by default in the Mac app's **Configuration → Input
settings → Share clipboard with Windows**. The toggle controls both directions:
text up to 64 KiB over the encrypted companion control link, and screenshot
images and one file up to 2 GB over separate local-network connections. Both
machines need this build; the Windows service and signed-in desktop worker handle
sharing even when the tray window is closed. Bluetooth is not used.

## Text clipboard test

1. Open the current Windows companion EXE (`releases/DeusKVM-Companion-win-x64-<date>.exe`,
   or the one inside `DeusKVM-Companion-win-x64.zip`); its update flow replaces the
   service and desktop worker while preserving settings.
2. Quit the Mac app and launch the app from the current `DeusKVM-mac-arm64.zip`.
3. Copy a sentence on the Mac, cross to Windows and paste into Notepad. Copy a
   different sentence in Notepad, return to the Mac and paste into a text editor.
4. Repeat with emoji, accented characters, multiple lines and approximately
   20 KB of text. Check that typing and edge return stay responsive during
   transfer.
5. Make several successive copies in both directions. A newer local copy must
   not be replaced by an older transfer, and switching back and forth without
   copying must not cause clipboard feedback loops.
6. Disable sharing and copy fresh text; it must stay local. Re-enable and copy
   again. Test reconnect and lock/unlock with new text. The clipboard must not
   synchronize on the login/security screen.
7. Using disposable test text, check a password manager that marks its clipboard
   private. Private text/images and text over 64 KiB must never be published;
   switching to that Mac must not leave another Mac's remote content available.
8. Give two Macs distinguishable text, images and files. With both still in
   PC-control mode, paste from each as its first input after the other has been
   controlling Windows. Only the triggering Mac's content may paste. Also take
   over during a slow transfer, then check Windows → Mac paste after returning.

Automated coverage includes isolated named pasteboards, ownership/revision rules
and wire fixtures; it does not touch the user's clipboard. macOS clipboard tests
that require desktop access remain pending for this build because the test
session reported Secure Input and denied pasteboard access. Real multi-Mac
handoffs, native Windows clipboard rendering and Finder/Explorer integration
remain hardware checks; earlier transfer results do not validate this change.

## Behavior and limits

- Plain Unicode text crosses in both directions, up to **65,536 UTF-8 bytes after newline
  normalization**. Rich copies may supply their plain-text representation;
  formatting is not transferred. Screenshot images are supported separately
  as described below. File-list clipboards are excluded
  from text sharing, even if they also contain a text representation.
- All enabled Macs can stay connected, but remote clipboard content comes only
  from the current controller. Actual input sent to Windows selects that Mac;
  a paste shortcut can itself take control. Background clipboard updates never
  change the controller. Connecting alone does not publish a Mac clipboard.
- Taking control requests a fresh typed snapshot: text, file, image, empty or
  unavailable. Windows revokes the previous source and prepares the new clipboard
  before delivering the triggering input. This readiness wait covers metadata,
  bounded text and native clipboard installation, not bulk image/file download.
  Missing readiness cannot fall back to another Mac's clipboard.
- While selected and in PC-control mode, the Mac checks pasteboard changeCount
  every 500 ms; it reads payloads only after a change. The subscription stops on
  return, ownership loss or disable. Setup, explicit capture and incoming writes
  also read the pasteboard; their operation-specific retries are bounded.
- Returning locally retains the last controller as the Windows clipboard
  partner, so Windows → Mac text and file paste still works. A controller change,
  or disabling/disconnecting the clipboard partner, invalidates the prior
  session's transfers.
  File contents remain on demand. Mac → Windows image rendering is also on
  demand; Windows → Mac images download asynchronously when accepted.
  Text over the cap is skipped rather than truncated.
- Empty text is supported. Embedded NUL and malformed Unicode are rejected.
  Newlines are LF on the wire and CRLF when written to Windows.
- Imported revisions are recorded and marked so they are not echoed back or
  offered as another Mac's content. Ownership changes clear old remote imports,
  including text, while preserving genuine Windows-local copies. A local copy
  defeats an older pending snapshot; a later distinct copy from the selected
  Mac can publish a new snapshot. Late replies from old ownership generations
  cannot install data or unblock input.
- Only the Mac's selected, allowed PC can exchange clipboard messages. Sharing
  pauses on Windows lock/secure desktop/logout, Mac sleep/session deactivation
  or secure input, and when the user disables it. Old-session pending payloads
  are discarded. Copy again after reconnect/unlock to establish a fresh offer.
- Known private, transient and generated clipboard markers are inspected before
  reading text or images. This is metadata filtering, not content classification:
  **unmarked password text cannot be distinguished from ordinary text**.
- DeusKVM does not log or persist clipboard contents. Windows imports also
  opt out of cloud clipboard upload. The OS and other installed clipboard tools
  still govern their own history behavior.

## Implementation

Update both applications: the current protocol requires `multiInput:1`,
`inputNetwork:3` and `clipboardSnapshot:1`. Older builds are not compatible with
this ownership model. `clipboard:1` and CLIP_STATE still negotiate clipboard
consent; disabling sharing leaves input control available.

The Windows service owns the clipboard epoch. A snapshot request also carries
the input ownership generation. `ClipboardSnapshot` represents a complete reply
with its source revision and explicit content kind. Text is inline; file/image
metadata points to the authenticated file source. `DesktopClipboard` is the sole
incoming installation authority and acknowledges readiness only for the matching
source, generation and epoch. Empty/private/unavailable replies finish explicitly
after old remote content has been invalidated.

All integers below are little-endian. Each message travels as one sealed frame
on the TCP control link (stream 0 control, 1 bulk), in order. The maximum control
payload is 128 KiB; snapshot JSON is bounded to 96 KiB, including base64 text.

| Message | Stream | Payload |
| --- | --- | --- |
| CLIP_GRAB (0x20) | 0 | epoch u32, copy sequence u32, byte count u32; 0xffffffff withdraws |
| CLIP_GET (0x21) | 0 | epoch u32, copy sequence u32, byte offset u32 |
| CLIP_DATA (0x22) | 1 | epoch u32, copy sequence u32, byte offset u32, 0–1024 bytes |
| CLIP_STATE (0x23) | 0 | epoch u32, availability/enabled u8 |
| CLIPBOARD_SNAPSHOT (0x52) | 0 request, 1 reply | JSON: epoch, ownership generation, request marker; reply adds revision, kind and optional text/file |

CLIP_GRAB/GET/DATA and the matching `ClipboardTransfer` state machines now serve
Windows → Mac text. The receiver requests one 1 KiB block at a time, retrying a
missing block after five seconds up to three times. Mac → Windows uses the typed
snapshot path. Input uses its separate stream, ordered behind ownership readiness
when the controller changes.

The Windows link worker coordinates the transfer but does not access the
Session 0 clipboard. `DesktopClipboard` uses a dedicated STA thread within the
signed-in desktop worker, isolated from the Raw Input STA; it wakes on clipboard
change notifications (`AddClipboardFormatListener`), service commands and session
lock/unlock, with a one-second desktop check and a 200 ms retry only while an
operation can still fail transiently (the old 200 ms poll remains as a fallback
if the listener cannot be registered). `RemoteClipboard` installs a live OLE
object for text, files and images; it is never flushed eagerly. The pipe thread
can revoke its source lease and cancel blocked network reads even while the
clipboard STA is rendering. `ClipboardPasteboard` uses a dedicated Mac queue,
isolated from the main/input run loops. Native
revision checks protect a newer local copy even if it happens before DeusKVM
notices it. Clipboard owners can delay rendering; those reads stay off input
threads, though a new controller must finish clipboard preparation before its
queued input is delivered. The Mac pasteboard API does not provide an atomic compare-and-write,
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

## Screenshot image clipboard

Both apps advertise `images: 1` in HELLO. An image uses a file descriptor with
`contentType: "image/png"` and the authenticated LAN transport: inside the typed
snapshot for Mac → Windows, and FILE_OFFER for Windows → Mac. Install the updated
Mac app and Windows companion; both must be reachable on the local network.

- Mac captures explicit PNG or TIFF clipboard data, including screenshots copied
  by CleanShot, and converts it to PNG. Explicit image data takes precedence when
  the same copy also offers a file URL, as CleanShot does, except for a Finder
  file copy: its image is only the file's icon (`com.apple.icns` or
  `com.apple.finder.noderef` present), so it stays a file offer. Windows captures registered `PNG` data
  or a native bitmap (including Windows' conversion from DIB/DIBV5).
- Windows installs PNG/CF_DIB metadata immediately and fetches image bytes when
  a consumer requests a format. macOS downloads an accepted image asynchronously,
  then publishes PNG and TIFF. PNG preserves transparency; the Windows DIB fallback uses a white
  background. Chromium/Electron reads registered PNG before bitmap fallback
  ([Chromium implementation](https://chromium.googlesource.com/chromium/src/+/refs/heads/main/ui/base/clipboard/clipboard_win.cc)).
- Windows also offers `Screenshot.png` through the same live OLE data object,
  so Explorer can paste it from the same copy.
  PNG/DIB and the [Shell file formats](https://learn.microsoft.com/en-us/windows/win32/shell/clipboard)
  coexist; source identity and revocation checks apply to every format and read.
- On Mac, Finder **Cmd+V** saves the cached screenshot in the current folder via
  the existing Finder paste handler. Native PNG/TIFF remain on the clipboard for
  Discord and other image apps. Finder Automation permission is required, as for
  ordinary file paste; Finder menu/right-click paste is not added. An existing
  destination filename is never overwritten.
- Images are limited to **32 MiB of encoded data** and **40 million decoded
  pixels**. A Windows paste consumer may wait for image data to arrive;
  the Mac publishes the image only after download completes.
  No screenshot file is saved until a file paste is requested. Image paste into
  applications uses the native clipboard; Finder Cmd+V uses the handler above.
- The sharing preference, privacy markers, clipboard revisions, selected peer,
  and session/lock checks also apply to images. A newer local copy or remote
  offer invalidates an in-flight image. Unavailable LAN never publishes a partial
  image; copy again to retry.
- File-only copies in Finder/Explorer still use the existing file-paste behavior;
  a Mac file URL alone is not converted into an image.

The user reported the combined image/file workflow working on hardware
(2026-10-20); the individual checks below were not enumerated in that report and
remain open for broader validation: copy a CleanShot screenshot on Mac and paste
into both Discord and Explorer on Windows; use Windows **Snipping Tool** to copy
a screenshot and paste into both Discord and Finder (Cmd+V) on Mac.
Confirm each Discord paste opens an image attachment preview
(sending a message is unnecessary). Also test transparency, rapid successive
copies, a local copy during download, clipboard sharing off, lock/reconnect,
unavailable LAN, and existing text/Finder/Explorer paste. See
[IMAGE_CLIPBOARD_PLAN.md](IMAGE_CLIPBOARD_PLAN.md) for the verification record.

## On-demand file paste

The sharing toggle supports **one regular file up to 2 GB (2,000,000,000 bytes)**
in both directions. Text and file metadata/authentication use the control link.
File contents use a separate authenticated local IPv4 connection, in blocks up to
256 KiB. There is no pre-download. Mac → Windows keeps the
native Explorer virtual-file stream; Windows → Mac uses Finder Cmd+V with
DeusKVM progress/cancel UI, as authorized by the user. Other Finder paste commands
and destination apps are not intercepted.

`files: 1`, `fileNetwork: 1` and `fileReceive: 1` remain in HELLO.
FILE_OFFER (0x24, bulk JSON, at most 4096 bytes) carries reverse metadata;
Mac source metadata is embedded in CLIPBOARD_SNAPSHOT. Both descriptors carry
epoch, source clipboard revision, clipboard sequence, filename, size and, for a
screenshot, `contentType`. A Mac source also includes up to four private or
link-local IPv4 addresses (wired interfaces before Wi-Fi), an ephemeral port and a
per-offer key; Windows tries the addresses in order.
Old FILE_GET/FILE_DATA IDs (0x25/0x26) are reserved but no longer serve file contents.

Windows file copying reads CF_HDROP metadata for one regular, non-link file. Paste
on the Mac creates a temporary listener and sends FILE_ACCEPT (0x27, bulk JSON,
4096-byte maximum) with the original source identity and fresh Mac endpoint/key.
The Windows desktop worker validates the still-current source and connects out;
Windows never opens an incoming listener. The existing handshake derives separate
AES-GCM keys for connector and listener; in the reverse flow, the Mac listener
sends encrypted offset/count requests and the Windows connector sends responses.

Both directions use blocking sockets on dedicated threads with 4 MiB socket
buffers: BSD sockets on the Mac (`FileNetworkServer`, `FileNetworkReceiver`,
shared `LocalSocket` helpers) and synchronous .NET sockets on Windows
(`NetworkFileReader`, `NetworkFileSender`). The requester keeps up to 16 block
requests in flight; replies stay in request order. The Windows receiver also asks
the stack to acknowledge every segment so 256 KiB blocks do not wait on delayed
ACK. Network.framework and the .NET Framework async socket path held pastes to a
few MB/s and are no longer used for file contents (2026-11-10). Each finished
transfer logs byte counts and timings only.

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
