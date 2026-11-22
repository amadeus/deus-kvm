# DeusKVM Windows companion

The service finds your Macs on the local network, connects to them, and owns
switching and plain-text clipboard coordination. It launches the desktop worker
and a SYSTEM secure-input worker that injects the Mac's input; the optional tray
configures the service. Closing the tray leaves switching and clipboard sharing
running. Bluetooth is not used.

Windows owns the control decision. The first connected, enabled DeusKVM Mac
becomes active. A later Mac is automatically disabled. Clicking **Enable DeusKVM**
on that Mac requests takeover: Windows asks the previous Mac to disable and
release held input, waits for its acknowledgement, then grants the new Mac.
Each active Mac supplies its own layout; clipboard sharing follows it and pauses
while Windows is locked or signed out. Update the Mac apps together with this
companion: Bluetooth builds of the Mac app cannot connect to it.

## Install and configure

1. Open DeusKVM on each Mac you want to use, on the same private network as
   the PC. Allow local network access if macOS asks.
2. Extract **DeusKVM-Companion-win-x64.zip** and open the
   **DeusKVM-Companion-win-x64-*date*.exe** inside it (or open a downloaded EXE
   directly). It unpacks and launches setup automatically; no adjacent files are
   needed. Approve the Windows administrator prompt to install or update. No
   scripts or .NET installation are needed. Existing service startup/running
   preferences are preserved.
3. Choose **Add Mac…**. It lists the DeusKVM Macs it finds on the network; pick
   your Mac's computer name and approve the administrator prompt. Repeat for each
   Mac. Adding another Mac does not replace a connected active Mac. The service
   connects only to Macs added this way. The window shows **Active: …** and
   **Disabled or not allowed: …** after service verification.
4. A few seconds later the PC appears in each Mac's **Setup → Devices**; turn on
   **Enable control** for it there.
5. On each Mac, choose the Mac exit edge and Windows display in **Configuration**.
   The active Mac's **Setup → Devices** reports **Windows edge return ready**;
   Windows uses the edge opposite that Mac's edge. Disabled Macs cannot enter
   remote control or share clipboard data. The Mac reports local permission/capture
   blockers as well as ownership state; a handshake alone is not reported as ready.
6. Click **Enable DeusKVM** on the other Mac to take control. The previous Mac
   becomes disabled. Disabling the active Mac alone leaves Windows idle if all
   other Macs are disabled. Returning the pointer locally does not relinquish
   ownership. A stalled handoff waits for release acknowledgement or the end of
   that Mac's connection; a heartbeat timeout alone cannot grant overlapping control.

**Updating from a Bluetooth build:** Macs saved by earlier builds are ignored,
because Macs and PCs are now known by network identities. Choose **Add Mac…**
again for each Mac, then turn on **Enable control** for the PC again on each Mac.
Old Bluetooth pairings are no longer used; remove them in Windows and macOS
Bluetooth settings if you like.

### Finding Macs

Each Mac listens on TCP port 53400 (another port if that one is busy) and
announces itself with Bonjour (`_deuskvm._tcp`). The service's link worker sends
Bonjour queries on every IPv4 interface and connects out to the Macs you added;
Windows needs no listening port or inbound firewall rule.

On networks without multicast, such as Tailscale, list the Mac in
`C:\ProgramData\DeusKVM\mac-hosts.txt`: one `host[:port]` per line (lines starting
with `#` are ignored), for example a Tailscale name. The Mac must still be one you
added with **Add Mac…**, which lists only Macs found with Bonjour, so add it once
while both computers share a network.

The link is encrypted and authenticated: each side has a P-256 identity key, and
a Mac is identified by its key, so another device cannot pose as an added Mac.
The PC's identity is kept in `C:\ProgramData\DeusKVM\identity.bin`, readable only
by SYSTEM and Administrators.

### Network input

When control passes to the PC, the desktop worker connects to an input listener on
the Mac, the same way file paste does, and receives the trackpad's pointer motion
and clicks at the pad's own rate, plus keys, media keys and scroll steps. By
default every step is handed to the SYSTEM secure-input worker, which injects it on
whichever desktop has input, so input also works on UAC prompts, the lock screen
and over elevated (administrator) windows. The desktop worker injects directly only
while that worker is unavailable. The Windows Mouse settings apply.

Keys arrive as whole keyboard states; the desktop worker turns them into key presses
and releases by scan code (so the PC's layout applies), repeats the newest held key
at the Control Panel delay and rate, raises Ctrl+Alt+Delete through the SYSTEM
worker (`SendSAS`, after setting the `SoftwareSASGeneration` policy to allow
services) and locks for Win+L (`LockWorkStation`). Held keys are released whenever
the stream or handoff ends.

The Mac switches to the PC only once this stream is up. There is no other input
path: if the PC cannot inject (rare), input is dropped until it can. Windows edge
return requests ride the stream's acknowledgements; if the Mac has not taken the
pointer back within 150 ms, they also go over the control link (at most once per
100 ms). The settings window's status adds a "Network pointer: up" line while the
stream runs.

The SYSTEM-worker relay on a UAC prompt and the elevated-window fix were confirmed
on hardware on 2026-11-11. Network edge return, keys and scrolling on the stream,
and the network-only link itself (Bonjour discovery, **Add Mac…**, the encrypted
control link) still need hardware validation.

### Multiple Macs

If both Macs are found when the service starts, it prefers the last active
enabled Mac, then stable id order. Disabled Macs stay disabled. Macs found
afterward are ordered as observed by the companion.

See [the two-Mac checkpoint](../docs/AUTOMATIC_MAC_CHECKPOINT.md) for the manual
validation sequence (current setup steps are above). Automatic selection and
reconnection require validation on Windows; successful cross-builds do not
prove that behavior.

### Service

The service runs as LocalSystem, including before login and after sign-out. Its
link worker (`--link-worker`) inherits that identity in Session 0, runs discovery
and the control links on a dedicated STA message loop, and is isolated so the
service can terminate it if it stalls. A Mac restart, sleep or
network change can end the link; the worker reconnects when the Mac is found again.
(Bluetooth builds confirmed control at the login screen after a Windows reboot on
2026-09-14; the network-only link has not yet been checked there.)

Windows may display its normal unsigned-app reputation prompt for this personal
build. The EXE is built from this repository; no installer downloads or
third-party servers are used at runtime.

## Package size

The Windows x64 package uses .NET Framework 4.8, already included with Windows
10 2004+ and Windows 11. It ships application code and small dependency DLLs,
without bundling the modern .NET runtime.
The download is one EXE with the compressed package embedded. It extracts into
a private temporary directory, runs the existing setup flow, then cleans up.
Installed dependencies stay under Program Files. Installation verifies the
complete payload and rolls back replaced files if the update fails.

The user confirmed both the small Framework app and the single-EXE launcher work
(2026-09-23); Windows CI exercises the EXE's install, update, reopen, temporary
cleanup and removal. Declining the UAC prompt and downgrade remain unchecked on
hardware; see [the launcher plan](../docs/WINDOWS_SINGLE_EXE_PLAN.md). Size
measurements are recorded in [the size plan](../docs/WINDOWS_SIZE_PLAN.md).

## Window and tray controls

- **Add Mac…:** choose a Mac found on the network and save it (administrator
  prompt).
- **Start Service:** start now.
- **Stop Service:** stop the service now. Its connections to the Macs close, and
  a Mac that was controlling the PC takes its input back. Mac hotkey return
  remains available.
- **Start automatically with Windows:** checked by default. Changes Automatic
  versus Manual startup for future boots without starting/stopping it now. A
  manually started service survives logout in either mode. An intentional Stop
  is respected until Start or the next boot with automatic startup enabled.
- **Show tray icon at sign-in:** open only the optional tray after users sign
  in. Enabled once on this upgrade, then preserved across updates. Independent
  of the service-startup checkbox; it does not start a stopped service.
- **Remove DeusKVM from this PC…:** remove the service/workers, startup entry,
  shortcut, settings/logs and installed files. Wait for the final result message.
  If it fails, reopen the downloaded EXE to retry. Windows execution history remains.
- **Quit Tray:** close the optional UI, leaving the service running.

Service/configuration changes request administrator permission. Opening settings
and viewing status do not. The tray's automatic-start checkbox reads actual
Windows service configuration, not a separate app preference.

## Signed-in edge checkpoint

Open the downloaded EXE to update. The update closes the old companion
automatically. Leave the service running and test:

- Cross at roughly the top, middle and bottom of the Mac edge. Placement on the
  selected Windows display should match; return should preserve that fraction.
- Reach the corresponding Windows edge to return immediately; no dwell or
  extra push is required. A shared border with
  another Windows display is not an exit; use an exposed part of the edge.
- Hold a key or mouse button while pushing back: control must stay on Windows.
  Release it, then move into the edge again. No key or button should remain stuck.
- Return to the Mac: the Windows cursor should disappear. Move or click the
  PC's own mouse: it should reappear immediately, and the first click should
  reach the app underneath. Crossing back to Windows also shows the cursor.
  Stop the service while the cursor is hidden and check that ordinary PC mouse
  use resumes. The tray window can remain closed throughout.
- Return using the Mac hotkey, then move the PC's own mouse: no delayed switch
  should occur. Quit the tray and repeat edge switching; it should still work.
- Restart the Mac app and confirm the PC reconnects without adding the Mac
  again. If the link drops while remote, the Mac restores local input.
- While controlling Windows, press Ctrl+Alt+Delete, then close the security
  screen and return through the Windows edge. Edge return resumes on the normal
  desktop without a hotkey round trip. While the security screen is open, use
  the Mac hotkey if needed; returning locally that way must prevent a stale
  Windows edge event from switching again after the security screen closes.

The clipboard worker runs only in the signed-in console user session and
accesses the clipboard only on the Default desktop. The local Mac hotkey
remains available when an application confines the PC cursor.

## Login handoff checkpoint

With the updated companion and Mac app, reboot Windows, cross from the Mac at
the login screen and sign in. Once **Setup → Devices** on the Mac reports
**Windows edge return ready**, move back through the Windows edge. The current
handoff should continue without a hotkey round trip or another entry, and the
cursor should stay where you left it when the desktop worker becomes ready.
Also check that using the Mac hotkey before the worker is ready prevents a late
return event after login. Amadeus confirmed login behavior works with the
Bluetooth builds; this checklist remains useful for regressions.

The tray can now auto-launch after login; the service and desktop worker still
operate without it. Login launch does not open a settings window.

Use **Open diagnostics** in the tray for `status.json` and `service.log` in
`C:\ProgramData\DeusKVM`. Logs include discovery results, connection errors,
desktop readiness, service identity and session transitions. They do not record
keys, mouse movement, clipboard contents or passwords. Logs rotate at 2 MiB with
one retained file. If the status says a Mac is not allowed, add it with
**Add Mac…**; if no Mac is found, check that DeusKVM is running on the Mac and
both computers share a network (or list the Mac in `mac-hosts.txt`).

## Text clipboard checkpoint

1. Open the downloaded EXE to update Windows. Quit and reopen the newly
   built Mac app. Keep **Configuration → Input settings → Share clipboard with
   Windows** enabled.
2. Copy text on the Mac, cross to Windows and paste. Copy different text on
   Windows, return to the Mac and paste. Repeat a few times with Unicode,
   multiple lines and a 20 KB block.
3. Check that edge switching and typing stay responsive during the large copy.
   Paste again after several round trips: imported text should not bounce back
   and replace a newer local copy.
4. Turn sharing off in Mac Settings and confirm new copies no longer cross.
   Turn it on, make a fresh copy and repeat. Lock/unlock and reconnect, then
   repeat with fresh text; clipboard sharing must stay off on the login screen.

Text sharing carries plain text, without rich formatting, over the encrypted
control link. Screenshot images cross separately over the local network; file
transfer is described below. Known private
clipboard markers are respected, but unmarked password text is indistinguishable
from ordinary text. Test privacy with disposable text, not real credentials.
Full behavior, limits and development checks are in [docs/CLIPBOARD.md](../docs/CLIPBOARD.md).
Basic two-way text sharing was user-confirmed with the Bluetooth builds; the
checks above remain useful for regressions.

## Open or update

Use **DeusKVM Companion** in the Start menu, or open the downloaded EXE again.
If the tray is already running, its window reopens. An identical package opens the
installed app without reinstalling. A different build updates the installation,
automatically closes the previous companion, then opens the new window.
Existing startup settings, added Macs and running/stopped state are
preserved (Macs saved by Bluetooth builds must be added again).
First installation starts the service with automatic startup enabled.
The UI runs with the permissions of the user who opened it; only installation
and service changes request administrator permission.

Use the **Remove DeusKVM from this PC…** button/menu for removal. No separate
uninstall script is needed. The Macs' lists of allowed PCs and the downloaded
EXE/ZIP remain unchanged.

## New Mac controls

The configured hotkey and **Switch to PC** button center the Windows pointer on
the selected display. Edge crossings retain proportional placement.

The Mac's **Configuration** tab and menu bar share **Disable DeusKVM / Enable DeusKVM**.
Disabling restores local control, stops input capture and clipboard sharing (the
PC sees the Mac as paused), and changes the menu icon to a pause symbol. This
state survives app restart. Enable to request control again. **Setup → Startup**
on the Mac also offers an opt-in **Launch DeusKVM at login** control.

For the complete test sequence, see `docs/POLISH.md` in the repository. Test
removal last because it deletes the installed app and its settings.

## Build

Use .NET 10 SDK. Cross-build from macOS:

```sh
dotnet build windows/DeusKVM.Companion.sln -c Release
dotnet test windows/DeusKVM.Companion.Tests -c Release -f net10.0
./windows/publish.sh win-x64
```

This Framework build currently targets `win-x64`. `DEUSKVM_DOTNET` can point at an isolated
SDK. On Windows, run the same test command with `-f net48` as well (Windows CI
runs both targets). The single EXE is written to the visible `releases/` folder
at the repository root as `DeusKVM-Companion-win-x64-<date>.exe`; the
repository-root `DeusKVM-Companion-win-x64.zip` carries the current one.
Core tests run on macOS; service, desktop workers, Raw Input and tray execution
must be verified on Windows.

References: [service isolation](https://learn.microsoft.com/en-us/windows/win32/services/interactive-services)
and [service access rights](https://learn.microsoft.com/en-us/windows/win32/services/service-security-and-access-rights).

Desktop worker API references: [CreateProcessAsUser](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-createprocessasuserw) and [Microsoft device instance property definitions](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/shared/devpkey.h).

## Desktop-launch correction (2026-09-13, historical)

The desktop worker starts outside the Session 0 worker's job (then the
Bluetooth worker, now the link worker), using explicit breakaway and the console
user's token. Its retained process handle and background pipe-disconnect monitor
preserve service-owned shutdown across sessions. This fixed a launch path that
could report "Access is denied" while the Mac link itself stayed connected.
Startup failures keep the operation and Win32 error in status.json rather than
replacing it with "Waiting for signed-in console desktop".

Windows job session constraint: [AssignProcessToJobObject](https://learn.microsoft.com/en-us/windows/win32/api/jobapi2/nf-jobapi2-assignprocesstojobobject).

## Installation identity

This build uses `DeusKVMCompanion` for the service and startup registry entry,
`C:\Program Files\DeusKVM Companion\DeusKVM.Companion.exe` for the installed
executable, and `C:\ProgramData\DeusKVM` for settings, identity and diagnostics.

For installations from before the DeusKVM rename, use that older build's cleanup
command first; there is no migration from the old application/service identities.
Updates from an existing DeusKVM build preserve service running/stopped state
and startup preferences. Macs saved by Bluetooth builds are ignored; add them
again with **Add Mac…**.

## On-demand file paste prototype

The current build transfers one regular file up to 2 GB (2,000,000,000 bytes) in
both directions, over networking only. Mac → Windows retains native Explorer
paste. Windows → Mac uses Finder Cmd+V with DeusKVM progress/cancel UI. File
metadata and authentication travel on the encrypted control link; contents
transfer only on paste, over a separate connection.
Windows connects out in both directions, so no inbound Windows firewall rule is
needed. See [the file paste checkpoint](../docs/FILE_PASTE_CHECKPOINT.md).
