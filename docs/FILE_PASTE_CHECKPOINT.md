# Network-only file paste in both directions

Update **both apps** from the visible `releases/` folder:

Use the current `DeusKVM-mac-arm64-*.zip` and
`DeusKVM-Companion-win-x64-*.zip` packages. Later CPU/edge checkpoints retain
this file-transfer behavior.

Quit the old Mac app, extract and launch the new Apple Silicon app. Close the old
Windows tray app, extract the Windows ZIP into a fresh folder and launch
`DeusKVM.Companion.exe`; its normal update flow replaces the installed components.
Keep pairings and **Settings → Share clipboard with Windows** enabled. Both
computers must be reachable on the local network. Allow Mac local-network access
if prompted. No Windows listening port or new inbound firewall rule is required.

## First test: Windows → Mac

1. Copy **one small regular file** in Windows Explorer with Ctrl+C.
2. Return to the Mac, open the destination folder in Finder, and press **Cmd+V**.
   Keep keyboard focus on the folder, outside its search/rename fields.
3. On the first attempt, allow DeusKVM to control Finder if macOS asks. This
   permission is used to locate the destination folder. If needed, enable it in
   System Settings → Privacy & Security → Automation → DeusKVM → Finder, then
   press Cmd+V again. Existing Accessibility permission is also required.
4. DeusKVM shows progress and Cancel while copying directly into that folder.
   Open the result and check it. A same-name destination is rejected, never replaced.

This first reverse implementation uses **Finder Cmd+V**, as agreed. Finder's
Edit-menu/right-click Paste, other Mac apps, and Option+Cmd+V move are not handled.
No pre-download occurs. A hidden partial file exists in the destination only while
paste is running; it is renamed on success and removed on cancellation/failure.

## Then verify Mac → Windows and larger files

- Finder Cmd+C → Explorer Ctrl+V or right-click Paste remains the native flow.
- Test the known 1.9 MB file, then a file larger than the old 10 MiB limit, then
  a large file approaching **2 GB (2,000,000,000 bytes)** in both directions.
- Compare SHA-256: `shasum -a 256 /path/to/file` on Mac and
  `Get-FileHash -Algorithm SHA256 'C:\path\to\file'` in Windows PowerShell.
- Try an empty file and normal text copy/paste both ways.
- Copy without pasting: no file contents should transfer. Third-party Windows
  clipboard tools that explicitly read the virtual file stream can act as consumers.

## Cancellation and regression checks

- Cancel a large transfer, then re-copy and retry. No incomplete destination
  should remain from the Mac receiver; Explorer manages its own failed output.
- Replace the source clipboard, modify/delete the source, disable DeusKVM, lock,
  disconnect or change ownership during transfer: it must stop without reporting
  a successful mixed/truncated copy.
- Disconnect networking but retain Bluetooth: plain text still works; file paste
  fails without falling back to Bluetooth. Restore networking and copy again.
- Check mouse, typing, edge switching and hotkey return throughout.

One regular file at a time, maximum 2 GB. No folders, symlinks, multiple selection,
cut/move or arbitrary destination apps. Keep the source unchanged and copied until
paste completes. File contents are authenticated/encrypted LAN traffic; text,
file metadata and transfer authentication stay on encrypted paired Bluetooth.
Local IPv4 reachability is required; guest isolation and IPv6-only networks fail.

Both directions passed full 2 GB socket transfers with matching SHA-256 locally.
**Real Windows clipboard capture, Finder Cmd+V/Automation prompts and hardware
regressions remain pending.** The prior Mac → Windows network flow was confirmed
fast by the user. Windows diagnostics remain in
`%LOCALAPPDATA%\DeusKVM\file-paste.log`, without names, paths, keys or contents.
