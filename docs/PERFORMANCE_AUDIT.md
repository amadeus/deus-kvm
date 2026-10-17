# Performance audit — 2026-10-16

Whole-codebase audit of the Mac app and Windows companion: input path, Bluetooth
companion channel, clipboard/file transfer, idle work and UI. Findings were
traced in source; costs were measured on this Mac where possible. Hardware
behavior is not established by these local checks.

## Changed

Mac:

- [x] Pending mouse motion under Bluetooth backpressure is summed (saturating at
  ±127 per axis) instead of keeping only the newest delta. Fast or high-rate
  pointing devices lost travel whenever CoreBluetooth's queue was full. No extra
  packets; key/button/scroll ordering is unchanged. Capture health logs now add
  `hidPending`, `hidRejected` and `hidMerged` counts for each interval.
- [x] Companion bulk frames are negotiated through HELLO `chunk` (as PLAN.md
  §3.3 specified): the smaller of both sides' ATT value limits, 20–244 bytes.
  HELLO and control frames stay 20 bytes; older peers advertise 20 and keep it.
  A 1 KiB clipboard block drops from 59 frames to 5. Windows → Mac bulk frames
  are acknowledged writes, so this was estimated at 0.6–1.2 KB/s before.
- [x] Both protocol decoders reassemble in place (each copied the whole partial
  message per frame).
- [x] Unchanged settings values no longer save. Only Windows edge/display
  changes resend CONFIG; previously every corner-slider step sent a CONFIG JSON
  (about 8 frames) over Bluetooth, enough to approach the 512-frame queue limit.
- [x] Unchanged `isRemote`, disabled status, display list and keyboard LED
  values no longer publish and re-render the app scene.
- [x] The Finder paste key tap is disabled while controlling Windows. With a
  Windows file offer pending it ran on the main thread ahead of InputTap for
  every forwarded key. Its focused-element Accessibility query now uses the
  0.1 s timeout too (the default is about 6 s).
- [x] The cursor parking point uses whole points. Odd-sized displays such as
  1728 × 1117 have a half-point center, which the exact parked-position check
  could treat as drift on every motion event.
- [x] Windows → Mac file paste keeps four 256 KiB requests in flight. Both Mac
  file listeners use TCP no-delay.

Windows:

- [x] Mac → Windows file paste reads two blocks ahead for sequential access.
  A seek still reads already-requested replies in order (record counters).
- [x] File send no longer builds each 256 KiB reply with LINQ Concat/ToArray,
  which copies byte by byte on .NET Framework and allocates large-heap arrays.
  One frame buffer is reused and sealed in place. Clipboard text blocks are
  copied directly instead of Skip/Take from the start of the text.
- [x] status.json is written when the status changes, plus every 15 s (tray
  freshness allows 45 s), instead of every 2 s. Process identity and session
  are read once: on .NET Framework, `Process.SessionId` snapshots every process.
  A transient status write failure no longer exits the service.
- [x] The BLE worker's 250 ms loop reparses settings.json only after its write
  time changes. Folder paths are resolved once.
- [x] The desktop worker starts when a Mac becomes active instead of at the next
  three-second tick; until it reports, ENTER placement and edge return wait.
- [x] The tray no longer refreshes a closed Settings window every 2 s. With no
  window open it refreshes every 10 s and when its menu opens.

## Measured locally

- Loopback file transfer, same Mac, .NET 10 harness, 2,000,000,000 bytes:
  Windows → Mac 4,944 → 2,975 ms; Mac → Windows 4,408 → 2,764 ms. Loopback has
  almost no round-trip time; Wi-Fi/LAN should gain more. The .NET Framework
  LINQ removal is not measurable in this harness.
- `AXIsProcessTrusted`, `CGPreflightPostEventAccess`, `IOHIDCheckAccess` and
  `CBManager.authorization` cost about 1 µs each in a signed app launched by
  LaunchServices, so the refresh's permission checks are not a latency source.
  (From a terminal-launched tool the last three took about 5 ms each.)

## Not changed

- SwiftUI: the settings window's views still observe the whole coordinator, so
  with that window open a crossing re-lays out its Form. Splitting the
  coordinator's published state would narrow this. (The App-level observation
  is fixed below. Since the threaded build input no longer waits for it.)
- Windows raw input is registered with INPUTSINK permanently, so the desktop
  worker wakes for every mouse packet system-wide, including the PC's own mouse
  (notable with 1–8 kHz mice). Registering INPUTSINK only while a handoff is
  active or the cursor is hidden needs a Windows check that device-change
  notifications still arrive and do not clear the Mac mouse match.
- The desktop worker sends STATE every second (forwarded to the Mac over BLE),
  and its clipboard thread polls every 200 ms. Both are small; changing them
  touches worker liveness and clipboard sequencing.
- Further Bluetooth throughput: write-without-response on the PC → Mac bulk
  characteristic, and pacing bulk frames while HID input is active.
- Windows startup: no NGEN; System.Text.Json uses reflection on .NET Framework.
- Finder paste's AppleScript runs on the main thread without a timeout.
- Known-Mac reconnect retries every 30 s indefinitely, logging each failure.

## Hardware checks (pending)

2026-10-17: the user reports the test builds work in general use. The items
below were not individually reported and remain open.

- [ ] Both builds updated: time 20 KB and 64 KiB text in both directions; check
  typing and edge return stay responsive. One side older: text still crosses.
- [ ] Fast movement while controlling Windows (trackpad and a high-rate mouse):
  pointer travel/feel, and `hidRejected`/`hidMerged` in Capture logs.
- [ ] Drag the corner slider: no companion disconnect. Change Mac edge and
  Windows display: edge return follows.
- [ ] Large file paste both ways on Wi-Fi and wired: speed, cancel, completed
  file matches. Explorer copy with a seek (for example a second copy).
- [ ] Copy a file on Windows, type while remote, return, Finder Cmd+V: forwarded
  keys unaffected; paste works.
- [ ] Enable/takeover a Mac: edge return becomes ready without the old delay.
- [ ] Windows tray/Settings status, tooltip and menu; status.json freshness.
- [ ] Idle CPU on both machines versus the previous builds.
- [ ] Follow-up Mac build: menu bar icon and menu still update when crossing;
  typing and scrolling on Windows behave normally. Capture logs from normal use
  will show whether rejections and post-crossing spikes dropped.

## Test builds

Windows from `51d3f19` (`windows/publish.sh`); Mac follow-up from `c5238dd`
(`scripts/publish-mac.sh`). Both apps need these for larger companion frames;
a mixed pair keeps 20-byte frames.

- Mac: `releases/Previous/DeusKVM-mac-arm64-2026-10-17-main.zip` (1,079,387
  bytes; SHA-256 `17500623196b2efddfc329398f055c05b2a035547b65361394873f78cedc2234`;
  it was `releases/DeusKVM.zip` until the threaded build below replaced it).
  Version 0.0.4 (4). Strict signature verification passes with the existing
  Apple Development identity, hardened runtime, no debugger entitlement; the
  executable contains only arm64. Quit DeusKVM and replace the installed app.
  The first performance build is `releases/Previous/DeusKVM-mac-arm64-2026-10-16.zip`
  (the one the real-use data below came from).
- Windows: `releases/DeusKVM-Companion-win-x64-2026-10-16.exe`, 912,384 bytes;
  SHA-256 `668c5ad0e4728b7b6cf635f35dad16ee17b46c1d6a24dc4103122a848c7c4aec`.
  x64 launcher; package verification passes (14 runtime files, embedded ZIP
  and hash, OS-only references). Open it on the PC to update the service. The
  prior EXE is retained under `releases/Previous/`. Unchanged by the Mac
  follow-up; reinstalling it is unnecessary.

Capture health lines (one per second while controlling Windows) include the new
backpressure counts: `log stream --predicate 'subsystem == "io.github.amadeus.deuskvm" && category == "Capture"'`.
The installed apps on either machine were not replaced.

## Real use, 2026-10-16 to 10-17

From the installed test build's Capture logs: 707 one-second samples while
controlling Windows and 58 crossings. Read them with `/usr/bin/log show`; zsh's
`log` builtin shadows the system command.

- 58 of 58 returns came through the Windows edge. Local restore took 2 ms
  median (7 max); entry setup 3 ms median (9 max). Cursor hidden and parked in
  every sample.
- Bluetooth backpressure (`hidRejected`) appeared in 46% of seconds, mostly
  during sparse input: 0.89 rejections per event below 20 events/s versus
  0.004–0.017 above 50/s. 99 motion reports were merged that the old queue
  would have dropped.
- Input dispatch delay was 0 ms median. All 43 samples of 8 ms or more (25 ms
  max) fell within two seconds after a crossing.

Follow-ups from this data, implemented 2026-10-17 (Mac only):

- [x] One notification per key and scroll event (`1c15297`). Key events also
  sent a boot-protocol keyboard copy, and each scroll step a trailing zero
  report; both came from BTRemote. Report-protocol hosts now get the report
  keyboard only, and cached mouse reports keep buttons only, so a read or
  resubscription never replays relative motion or scroll.
- [x] The App no longer observes the shared objects (`c5238dd`). Only the menu
  bar icon and menu observe the coordinator, so a crossing no longer
  re-evaluates every scene and rebuilds the menu from the App body.

First minutes with the follow-up build (31 samples, 8 crossings):

- Bluetooth rejections fell from 0.28 to 0.009 per input event overall, and
  from 0.48 to 0.05 at 20–50 events/s. Little sparse typing/scrolling yet.
- Remaining post-crossing spikes (16–22 ms on 3 of 6 crossings) occur with the
  settings window open. A 10 s profile put about two-thirds of the main
  thread's busy time in ControlsSettingsView re-rendering (it shows
  Local/Remote) and AppKit re-measuring the whole form for
  `.windowResizability(.contentSize)`; forwarding input was about a seventh.
  The menu bar icon view re-rendered as expected. The user rarely keeps the
  window open, so this is left as is; the fix would move remote state out of
  the coordinator's publications. Profiling at 1 ms intervals itself stalled
  input up to 431 ms, so avoid it during real use.

## Off the main thread — 2026-10-17 (branch `threaded`)

CoreBluetooth delivered every callback on the main thread, and remote input
went from the event tap through the main thread before becoming a HID report,
so any UI work there (a settings window render, a menu rebuild) delayed input.
Commits `49f75b1` and `a3f2e2d`:

- [x] CoreBluetooth runs on a dedicated serial queue (`BluetoothTransport`,
  user-interactive QoS). The queue also sends HID reports and holds their
  backpressure queue, sends companion frames, and translates captured input.
  The event tap forwards remote input straight to it.
- [x] The main thread keeps host selection, GATT services, published UI state
  and companion protocol decoding. It drives the queue in issue order: the
  route to the selected host, EXIT, release reports, retargeting and
  resubscription replays. Callbacks return to the main thread in order and are
  dropped once the manager is replaced (power-alert restart).
- [x] Input captured at a crossing waits on the queue (`InputGate`) until the
  coordinator approves the crossing and queues ENTER, so it still follows
  ENTER and is discarded for a refused crossing. Waiting motion and scroll are
  capped at 512 events; keys and buttons always wait.
- [x] Returning ends the tap session before EXIT, so no input for the session
  follows EXIT. Quitting waits for the queued EXIT and release reports, as it
  did when they were sent inline.
- [x] HID reads and keyboard LED writes are answered on the queue. Companion
  reads and writes still wait for the main thread, which owns the protocol
  state (as before, when the callback itself waited for it).
- `maxDispatchMs` in capture health lines now measures tap → Bluetooth queue
  delay, including the wait for a crossing's approval.
- The deployment target is macOS 13, so the queue confinement is a runtime
  discipline (an `@unchecked Sendable` class touched only on its queue). A
  custom actor executor would let the compiler check it but needs macOS 14.
- Windows and the companion protocol are unchanged; the 2026-10-16 Windows EXE
  stays.

Test build (Mac only), from `a3f2e2d` with `scripts/publish-mac.sh`:

- `releases/DeusKVM.app` (also `releases/DeusKVM.zip`, 1,104,699 bytes;
  SHA-256 `945d97efd710b4f47975b2cf5544aff66f3058665ab019f267ed5c8633c73b7e`).
  Version 0.0.4 (4). Strict signature verification passes with the same
  identity and hardened runtime; entitlements are Bluetooth and Apple Events
  only (no debugger entitlement); the only executable is arm64.
- Rollback: the main-branch follow-up build above,
  `releases/Previous/DeusKVM-mac-arm64-2026-10-17-main.zip`.

Hardware checks (pending), in normal use:

- [ ] Crossing to Windows and back by edge and by hotkey: no lost first
  keystroke or click after entering; no Windows cursor movement after return.
- [ ] Typing (modifiers, Caps Lock), clicking, dragging, scrolling and media
  keys on Windows.
- [ ] Disable and re-enable from the menu, and Windows taking control of a Mac
  (if used): no stuck keys or buttons.
- [ ] Quit and reopen DeusKVM while controlling Windows: nothing stays
  pressed; Windows reconnects.
- [ ] Mac Bluetooth off and on: the app recovers and Windows reconnects.
- [ ] Clipboard text and file paste in both directions.
- [ ] Switching Windows between two Macs (if used).
- [ ] Menu bar icon and menu still update on crossings.
- [ ] Capture logs over a day: dispatch delay and backpressure versus the
  follow-up build.

## Local verification

All 98 Mac tests and 176 Windows core tests pass (2 CNG GCM tests skip off
Windows); strict SwiftLint and SwiftFormat pass; the .NET Framework companion
builds with zero warnings. New tests cover motion summing and saturation,
whole-point parking, negotiated frame sizes, and shared wire vectors at 64 and
244 bytes (generated by an independent encoder that also reproduces every
20-byte vector).
Both loopback file suites pass: lazy metadata, wrong key, multi-block bytes,
seek, changed source, revocation, cancellation, empty, collision, stale and
2 GB transfers in each direction.

Mac follow-up: all 100 Mac tests pass, including one report per scroll step,
buttons-only replay state and the boot keyboard decision; strict SwiftLint and
SwiftFormat pass; each follow-up commit builds and tests on its own.

Threaded: all 105 Mac tests pass, including five InputGate cases (release in
capture order on approval, discard on stop or refusal, older sessions never
replace newer input, approving an older session keeps newer waiting input, the
motion cap never drops keys). Debug and Release builds have no concurrency
diagnostics under Swift 6 complete checking; strict SwiftLint and SwiftFormat
pass. CoreBluetooth on the queue is covered only by the hardware checks above.
