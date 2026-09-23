# On-demand file paste over the local network

Update **both apps** using the two ZIPs in the visible `releases/` folder:

- `DeusKVM-mac-arm64-file-paste-network-2026-09-22.zip`
- `DeusKVM-Companion-win-x64-file-paste-network-2026-09-22.zip`

Quit the old Mac app, extract the new Apple Silicon app and launch it. On Windows,
close the old tray app, extract into a fresh folder and launch
`DeusKVM.Companion.exe`; its update flow replaces the installed service/worker.
Keep existing pairings and **Settings → Share clipboard with Windows** enabled.
Keep both computers on the same local network (Wi-Fi or Ethernet). Allow DeusKVM
local-network/incoming access on the Mac if macOS prompts, then copy the file again.
No new setting or Windows inbound firewall rule is needed.

## First test

1. Copy the **1.9 MB file itself** in Finder with Cmd+C.
2. Switch to Windows and press Ctrl+V in an ordinary Explorer folder.
3. The same native paste should complete substantially faster when LAN is
   reachable. Open the result and check its contents. Re-copy before each retry.
4. If it is still slow, send `%LOCALAPPDATA%\DeusKVM\file-paste.log` (and `.previous`
   if rotated). `network-block` means LAN was used;
   `network-unavailable fallback=bluetooth` means connection setup failed and
   the existing slower Bluetooth path was used. Logs omit names, paths, keys and
   contents.

## Follow-up checks

- Compare SHA-256: `shasum -a 256 /path/to/file` on Mac and
  `Get-FileHash -Algorithm SHA256 'C:\path\to\file'` in Windows PowerShell.
- Copy without pasting: no contents should transfer. Metadata inspection does
  not fetch bytes. A third-party clipboard manager explicitly reading the native
  file stream can trigger transfer, as with the prior build.
- Try right-click Paste, an empty file, then normal text copy/paste both ways.
- Check mouse, typing, edge return and hotkey return during a transfer.
- Cancel Explorer's copy; re-copy and retry. Change/delete the source or disable,
  lock, disconnect or change Mac ownership during transfer: paste must stop, not
  silently finish with mixed bytes. Remove any incomplete Explorer destination.
- Copy something locally on Windows: it must supersede the Mac offer.
- With Bluetooth still connected, temporarily disconnect the local network,
  re-copy a **small** file and paste. Bluetooth fallback should still work.

## Scope and behavior

Same **Mac → Windows Explorer**, one regular file, **10 MiB maximum**. No folders,
symlinks, multi-selection, cut/move or reverse file paste. Use a Windows-compatible
filename. The source must remain unchanged, available and on the Mac clipboard.

Copying sends metadata and a fresh secret through encrypted Bluetooth. Content
reads connect to the Mac over private/local IPv4 TCP, using authenticated encryption
and blocks up to 256 KiB. No contents are pre-downloaded or staged by DeusKVM.
Explorer owns the destination and native copy UI. Bluetooth still handles input,
ownership and text clipboard sharing.

If network setup fails before an authenticated response, the existing 1 KiB
Bluetooth transfer is used. If an active network transfer fails, that paste fails;
copy again to retry. A newly allowed network or changed address may require copying
again. Guest Wi-Fi isolation, blocked incoming connections and IPv6-only networks
can prevent this first version's LAN path.

Local automated checks passed; **real Mac-to-Windows LAN performance, macOS
permission prompts and the hardware follow-up checks remain pending**. The user
confirmed the previous Bluetooth native paste interaction works, but is slow.
