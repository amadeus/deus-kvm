# CPU and edge-return checkpoint — 2026-09-23

Use the two current ZIPs in the visible `releases/` folder:

- `DeusKVM-mac-arm64-idle-events-2026-09-23.zip`
- `DeusKVM-Companion-win-x64-cpu-edge-2026-09-23.zip`

This idle-events update needs only the Mac app replaced if the cpu-edge Windows
companion is already installed.

Quit DeusKVM on the Mac, extract the new app and replace your existing copy before
launching it. On Windows, extract the new ZIP and launch its EXE; approve the
normal update prompt so it replaces the installed service/workers. Closing the
tray alone does not stop those workers. Keep Bluetooth pairings and layouts.

## First: edge return

Cross to Windows and back repeatedly using the same trackpad, initially without
copying files. Try after an app reconnect and with both Macs connected. Test the
hotkey too. Hold a key/button at the edge, release, and move outward again: held
input should prevent a premature switch, then normal return should work.

This build fixes a cached failed Windows mouse-identity lookup that previously
required a device change to recover. It is not yet confirmed as the cause of the
reported intermittent failure. If return sticks again, use the hotkey, note the
time and collect `%LOCALAPPDATA%\DeusKVM\edge-return.log` (and `.previous` if
present). Mac Capture logs record matching return rejections and link status.
Logs contain aggregate state/counts, not input contents or pointer coordinates.

## Then: CPU comparison

Use the same pointing device and settings-window visibility for each comparison.
Give Activity Monitor several refreshes per state:

1. Pointer on Mac, stationary.
2. Pointer on Mac, continuous movement.
3. Pointer on Windows, stationary.
4. Pointer on Windows, continuous movement.

The input timer now sleeps when no handoff/probe is pending; motion skips an
unnecessary cursor warp when already parked. Forwarding rate/report translation
are unchanged. CPU improvement and cursor behavior require this hardware check;
unit tests and cross-builds do not establish the actual reduction.

Finish with a small file and plain text in both directions to check the working
clipboard baseline. Full file-transfer instructions remain in FILE_PASTE_CHECKPOINT.md.

## Idle-events follow-up

Ready local capture now uses notifications instead of a repeating status timer.
Heartbeat expiry and text-transfer retries are one-shot deadlines. The permission
view stops polling once authorized. Clipboard change-count monitoring remains
while sharing is enabled; remote safety and blocked-capture recovery retain
bounded checks.

Compare idle CPU with the settings window closed, then open. Also toggle clipboard
sharing off briefly to distinguish clipboard observation from other idle work.
Re-enable sharing and verify text and a small file both ways. Check edge/hotkey
return, disable/enable, reconnect and permission/secure-input recovery. These
hardware results are pending; local unit tests do not measure CPU improvement.
