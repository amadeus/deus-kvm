# DeusKVM Windows companion

One service does everything. It finds your Macs on the local network, connects
to them, owns switching and clipboard coordination, and starts two helpers it
manages itself, because a service cannot reach the screen directly:

- the **session agent**, SYSTEM in the console session whenever there is one (the
  login screen before anyone signs in, while signed in, while locked). It injects
  the Mac's keys, pointer and scroll on whichever screen has input, watches the
  edge for the return to the Mac on all of them, and places and hides the cursor;
- the **clipboard helper**, the signed-in user, only while someone is signed in:
  clipboard, file and image paste must run as that user.

The optional tray is a remote control for the service. Closing it leaves
everything running. Bluetooth is not used.

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
   your Mac's computer name; the running service saves it, with no administrator
   prompt. Repeat for each
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
Old Bluetooth pairings are no longer used: remove the Mac from **Settings →
Bluetooth & devices** once the network build works (the Mac has nothing to remove).

### Finding Macs

Each Mac listens on TCP port 53400 (another port if that one is busy) and
announces itself with Bonjour (`_deuskvm._tcp`). The service sends
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

When control passes to the PC, the session agent connects to an input listener on
the Mac, the same way file paste does, and receives the trackpad's pointer motion
and clicks at the pad's own rate, plus keys, media keys and scroll steps. It
injects each one as it arrives on whichever desktop has input: the user's desktop,
over elevated (administrator) windows, and the login screen, lock screen and UAC
prompts, where it also runs the edge test, so the edge returns to the Mac there
too. The Windows Mouse settings apply.

Keys arrive as whole keyboard states; the agent turns them into key presses
and releases by scan code (so the PC's layout applies), repeats the newest held key
at the Control Panel delay and rate, raises Ctrl+Alt+Delete through the service
process (`SendSAS`, which only a service may call, after setting the `SoftwareSASGeneration` policy to allow
services; Win+Alt+Delete counts too, for a PC with Ctrl and Win swapped). Keys carry
no extra info, so remappers such as PowerToys Keyboard Manager treat them like a real
keyboard's (it skips events with bit 0 of the extra info set). Held keys are released whenever
the stream or handoff ends.

The Mac switches to the PC only once this stream is up. There is no other input
path: if the PC cannot inject (rare), input is dropped until it can. Windows edge
return requests ride the stream's acknowledgements; if the Mac has not taken the
pointer back within 150 ms, they also go over the control link (at most once per
100 ms). The settings window's status adds a "Network pointer: up" line while the
stream runs.

The network-only link, keys, scrolling, PowerToys remaps and Ctrl+Alt+Delete were
confirmed on hardware on 2026-11-22 with the earlier two-worker build. The session
agent (one SYSTEM helper for every desktop, including edge return on the login and
lock screens and UAC prompts) still needs hardware validation.

### Multiple Macs

If both Macs are found when the service starts, it prefers the last active
enabled Mac, then stable id order. Disabled Macs stay disabled. Macs found
afterward are ordered as observed by the companion.

See [the two-Mac checkpoint](../docs/AUTOMATIC_MAC_CHECKPOINT.md) for the manual
validation sequence (current setup steps are above). Automatic selection and
reconnection require validation on Windows; successful cross-builds do not
prove that behavior.

### Service

The service runs as LocalSystem in Session 0, including before login and after
sign-out. Discovery and the control links run inside it on a dedicated STA message
loop; if that loop stops reporting, the service exits and Windows' service recovery
restarts it. A Mac restart, sleep or network change can end the link; the service
reconnects when the Mac is found again. While a Mac is active the service keeps the
session agent running in the console session (restarting it when the session
changes, for example after signing out) and the clipboard helper running for the
signed-in user. The earlier build was confirmed at the login screen after a reboot
on 2026-11-22; edge return there was then unavailable (it came from a helper in the
signed-in session), which the session agent fixes.

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

- **Add Mac…:** choose a Mac found on the network and save it.
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

The tray is a remote control for the service: it shows the service's status as
it changes and sends Add Mac, the two startup checkboxes and Stop to the service
over its local control pipe (`\\.\pipe\DeusKVM.Control`), and starts the service
through Windows (the installer lets signed-in users start and stop it), so none of
these ask for administrator permission. Only installing, updating, removing, and a
change made while the service is stopped or still an older build (which falls back
to one administrator prompt) do. The tray's automatic-start checkbox reads actual
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
- While controlling Windows, press Ctrl+Alt+Delete and return through the Windows
  edge while the security screen is open, then again after closing it. Using the
  Mac hotkey instead must not let a stale Windows edge event switch again later.

The clipboard helper runs only in the signed-in console user session and
accesses the clipboard only on the Default desktop. The local Mac hotkey
remains available when an application confines the PC cursor.

## Login handoff checkpoint

With the updated companion and Mac app, reboot Windows, cross from the Mac at
the login screen and sign in. Once **Setup → Devices** on the Mac reports
**Windows edge return ready**, move back through the Windows edge, both at the
login screen and after signing in. The handoff should continue across sign-in
without a hotkey round trip or another entry (the session agent keeps running in
the same session).

The tray can auto-launch after login; the service and its helpers operate
without it. Login launch does not open a settings window.

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
The UI runs with the permissions of the user who opened it; only installation,
updates and removal request administrator permission (settings go through the
service's control pipe).

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
Core tests run on macOS; the service, the session agent and clipboard helper, Raw
Input and tray execution must be verified on Windows.

References: [service isolation](https://learn.microsoft.com/en-us/windows/win32/services/interactive-services)
and [service access rights](https://learn.microsoft.com/en-us/windows/win32/services/service-security-and-access-rights).

Helper launch API references: [CreateProcessAsUser](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-createprocessasuserw) and [Microsoft device instance property definitions](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/shared/devpkey.h).

## Desktop-launch correction (2026-09-13, historical)

The desktop worker (now the session agent and clipboard helper) started outside
the Session 0 worker's job (then the Bluetooth worker; the link now runs in the
service itself), using explicit breakaway and the console user's token. Its retained process handle and background pipe-disconnect monitor
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
