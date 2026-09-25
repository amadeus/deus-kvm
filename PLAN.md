# DeusKVM: implementation plan

Current follow-up: [automatic Mac selection on Windows](docs/AUTOMATIC_MAC_PLAN.md)
tracks the 2026-09-21 implementation and user hardware checkpoints.

The [macOS settings organization plan](docs/MAC_SETTINGS_PLAN.md) tracks the
Setup/Controls reorganization and its pending user review.

The [Windows settings sizing plan](docs/WINDOWS_SETTINGS_PLAN.md) tracks DPI,
content-based window sizing, and the pending 125% Windows checkpoint.

Goal: keep the Mac as a Bluetooth LE HID keyboard/mouse for the Windows PC, but
make it behave like Across / Deskflow / Universal Control: push the Mac cursor
past a chosen display edge to control the PC, push the PC cursor against a
chosen edge to come back, share the clipboard. A small Windows companion app
handles the PC side (edge hits, cursor placement, clipboard, config).

Approach, in one paragraph: reuse the existing app's Bluetooth architecture as
it is; it already pairs, presents the Mac as a keyboard and mouse, and forwards
input. Remove what is not needed (Classic mode, the iOS target, the sandbox).
Add the interactions that are missing: pushing the cursor past a chosen Mac
edge switches to the PC, and a customizable hotkey toggles between machines.
Then add a small Windows companion, which is what makes the return trip by edge
possible (the Mac cannot see where the PC cursor is), places the PC cursor at
the matching spot on entry, and carries the clipboard.

**Implementation boundary.** Amadeus has tested the existing remote control
and found it works well. Preserve that working behavior. The first edge-switch
checkpoint adds Mac edge activation and a local return hotkey around the existing
capture and HID forwarding path. Do not proactively rewrite notification queues,
merge movement, change key mappings, tune scrolling, widen reports, or alter the
HID descriptor. Make only the ownership, tap-lifecycle and cursor changes needed
for those interactions. Changes to core input/Bluetooth behavior must address a
problem reproduced at a manual checkpoint, or a later explicitly requested
capability; document that reason and keep the change narrow. Source observations
below are not a backlog of fixes to implement automatically.

**Post-checkpoint exception (2026-09-13):** Amadeus reported that horizontal
trackpad scrolling did not reach Windows and requested support. Capture now
forwards both scroll axes, and mouse report ID 1 adds signed relative Consumer
AC Pan (usage 0x0238) as its fifth payload byte. Boot-mouse reports remain three
bytes. Existing per-event clamping, queue behavior, and vertical scrolling are
preserved. Windows may need one re-pair to refresh its cached HID descriptor;
live horizontal/diagonal scrolling validation is pending.

Everything below is based on reading the code plus a research pass whose briefs
are summarised in "Facts the design rests on". Items marked **unverified** need
the spike named next to them before anything is built on top of them.

---

## 0. How this project is run

**Working model.** The implementing agent does the work end to end: reads,
edits, builds, lints, tests, stages and commits on the `big-hacks` branch in
this worktree. Amadeus does not type code; he tests at the points marked
**manual checkpoint** below and answers nothing else unless a checkpoint fails.
No pull requests, no pushes unless asked.

**Commits.** One commit per task bullet (or smaller), short lowercase
imperative subject in the style of the existing history (`delete classic
backend`, `add companion service`), body only when the why is not obvious.
Commit only when the build is green; commit after each M0 step separately so
regressions bisect.

**Commands** (run from the worktree root; prefix every `xcodebuild` with
`DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer` until
`xcode-select` is switched):

```
xcodegen generate                                             # after any project.yml change
swiftformat --lint . && swiftlint lint --strict               # must be clean before commit
xcodebuild -project DeusKVM.xcodeproj -scheme DeusKVM -configuration Debug \
  -destination "platform=macOS" -derivedDataPath .build/DerivedData build
xcodebuild ... test                                           # once DeusKVMTests exists (M0)
open .build/DerivedData/Build/Products/Debug/DeusKVM.app     # for manual checkpoints
```

**Signing.** Set `DEVELOPMENT_TEAM: UHD99KF9X7` and `CODE_SIGN_STYLE:
Automatic` in `project.yml` (identity "Apple Development: Amadeus Demarzi" is
already in the login keychain). Do **not** pass `CODE_SIGNING_ALLOWED=NO` for
builds that will be run: an ad-hoc signature changes every build and macOS
revokes the Accessibility grant each time.

**Rules the agent follows without asking.** §3.5 invariants; §3.3 is the only
source of protocol values; `HIDProfile.reportMapData` and the HID
characteristics remain unchanged unless checkpoint evidence or an explicitly
requested capability requires a scoped change; strings via `L10n` + `Localizable.xcstrings`
(manual/translated entries, new namespaces in new files, never `L10n.swift`);
settings keys in `AppSettings` as `"DeusKVM.camelCase"`; Swift 6 strict
concurrency (`@MainActor` classes, `nonisolated static` C callbacks, no
main-actor hop on the tap hot path); lowercase comments, no `// MARK:`; unit
tests for anything pure (protocol framing against `docs/protocol-vectors.json`,
edge geometry, key map). When a spike or checkpoint contradicts this document,
update the document in the same commit.

**Edge behavior.** Switch immediately on edge arrival with outward movement.
No dwell, extra push threshold or double tap. Legacy wire fields `pushCounts`,
`switchDelayMs` and `doubleTapMs` remain zero for compatibility.
Other defaults: `cornerPx` 0 (off), heartbeat 3 s with 3 misses,
parking point = center of the configured display. Keep them in
`AppSettings` defaults so tuning is a one-line change.

---

## 1. What the app is today (verified by reading the code)

| Piece                     | Where                                                       | What matters for us                                                                                                                                                                                                                                                                                                                     |
| ------------------------- | ----------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| BLE HID peripheral        | `DeusKVM/LowEnergy/HIDPeripheral.swift`                    | Builds Battery → Device Info → HID services in `didAdd` chain, then advertises only the HID UUID. Report IDs: 1 mouse (buttons, int8 X/Y/wheel), 2 keyboard, 3 LEDs, 4 battery, 5 system, 6 consumer.                                                                                                                                   |
| Report map                | `DeusKVM/LowEnergy/HIDProfile.swift`                       | 239 of 512 allowed bytes. Any change forces Windows users to unpair/re-pair.                                                                                                                                                                                                                                                            |
| Notification backpressure | `HIDPeripheral.updateValue` / `pendingBroadcast`            | One global latest-wins slot. A queued keyboard report can be overwritten by a mouse report, and mouse deltas are dropped (not summed) when the queue is full. Wrong for a byte stream.                                                                                                                                                  |
| Service Changed hack      | `HIDPeripheral.scheduleServiceChanged`                      | Adds/removes a throwaway service so a host with a stale GATT cache re-discovers. The code base already relies on bluetoothd emitting Service Changed.                                                                                                                                                                                   |
| Direct input              | `DeusKVM/DirectInputController.swift` (macOS half)         | Active `CGEventTap` at session level swallowing all input, `NSCursor.hide()`, `CGAssociateMouseAndMouseCursorPosition(false)`, any Ctrl+Alt event releases (to be replaced, §3.2). Needs Accessibility. Owned by `ContentView` as a `@StateObject`; `SetupView.onDisappear` stops it. Shows its own red `NSStatusItem` while capturing. |
| Input mapping gaps        | `DirectInputController` key table                           | No left/right modifier distinction, KeypadEnter mapped to Return (0x4C → 0x28, should be 0x58), no nav cluster / keypad / F13-F20, no media keys, scroll clamps per event, no horizontal scroll (needs a report-map field).                                                                                                             |
| HID facade                | `DeusKVM/HIDInput.swift`                                   | Value snapshot rebuilt on every `App` body evaluation. `isConnected` is true for _any_ connected central, not specifically the PC.                                                                                                                                                                                                      |
| App shell                 | `DeusKVMApp.swift`, `ContentView.swift`, `SetupView.swift` | One `WindowGroup`; backends start from the window's `.onAppear`; iOS and macOS share views behind `#if os(...)`.                                                                                                                                                                                                                        |
| Classic (HIDP) backend    | `DeusKVM/Classic/`                                         | Cannot reach Windows at all (README, and confirmed in code: outbound only). To be deleted in M0.                                                                                                                                                                                                                                        |
| Build                     | `project.yml` (xcodegen), `build.sh`                        | Swift 6 strict concurrency, swiftformat + `swiftlint --strict`. Baseline (2026-09-13): unsigned Debug macOS build succeeds; lint fails only on `L10n.swift` being 607 lines (limit 600). No tests.                                                                                                                                      |

---

## 2. Facts the design rests on

### Verified (primary docs and, where marked, real-world reports)

1. **Windows can talk to a custom GATT service next to HID.** Windows enumerates
   each primary GATT service of a bonded LE device as its own device node; only
   the HID service (0x1812) returns `AccessDenied` to apps. An unpackaged .NET 8
   (`net8.0-windows10.0.19041.0`) or C++/WinRT app can find the already-paired
   Mac (`BluetoothLEDevice.GetDeviceSelectorFromPairingState(true)` →
   `FromIdAsync`), open the custom service, subscribe to notifications and
   write, with no manifest capability or consent prompt. Pairing itself must be
   done in Windows Settings (`PairAsync` is unsupported in desktop apps).
2. **Windows caches the GATT table of bonded devices**; the cache is invalidated
   only by a Service Changed indication or by unpairing.
   `BluetoothLEDevice.GattServicesChanged` fires on the companion side.
3. **Absolute-pointer HID is a dead end as the primary design.** Windows maps
   absolute mice to the primary monitor only (mouhid never sets
   `MOUSE_VIRTUAL_DESKTOP`; KB5003637 re-broke multi-head in 2021;
   PiKVM/NanoKVM/JetKVM/deskhop all fall back to relative). Across also confirms
   drag-back without a client only works in its absolute modes with manually
   entered resolution. Keep relative HID; optional single-monitor fallback
   later.
4. **Raw Input keeps delivering relative deltas while the Windows cursor is
   clamped at an edge**, unaffected by pointer speed (`MOUSE_MOVE_RELATIVE`
   only). Microsoft's DirectXTK, Chromium pointer lock and SDL rely on it.
   `WH_MOUSE_LL` coordinates are clamped-ish and unreliable as a push signal;
   Microsoft itself says prefer Raw Input.
5. **Blind states on Windows are real.** A medium-integrity companion gets no
   hook/raw-input while an elevated window is foreground (UIPI), and nothing on
   the secure desktop (UAC prompt, lock screen). `GetCursorPos`/`SetCursorPos`
   need the thread on the input desktop. BLE HID input itself still works there,
   so the Mac must keep two ways back that do not involve the companion: the
   automatic releases in §3.2 and an optional user-mapped toggle hotkey.
6. **Deskflow never detects edges on the controlled machine**: the server
   dead-reckons the remote cursor from the deltas it sends and places it with
   absolute coordinates. That model does not transfer to relative BLE HID with
   Windows pointer ballistics. Hence the companion owns edge detection on the
   PC.
7. **CoreBluetooth notification limits**: values are truncated to
   `CBCentral.maximumUpdateValueLength`; `updateValue` returns false when the
   queue is full and `peripheralManagerIsReady` is the only resume signal.
   Windows 11 requests ATT MTU 527; the negotiated value must be logged, expect
   20–512 bytes per notification and roughly 5–25 kB/s shared with HID traffic.
8. **LAN option, if ever needed**: on macOS 15+ _listening_ for inbound TCP
   needs no Local Network prompt (Bonjour and outgoing local connections do).
   With the App Sandbox dropped (M0) no entitlement is needed; if the sandbox
   were kept, only `com.apple.security.network.server` when the Mac listens and
   Windows connects out. TLS-PSK is not usable (Apple: TLS 1.2 only; .NET:
   none), so pin a self-signed cert instead.
9. **Prior-art UX to match** (Deskflow `Server::isSwitchOkay`, Across, Mouse
   Without Borders): 1-px jump zone with immediate switching,
   corner exclusion (mask + size), lock-to-screen toggle,
   jump/return hotkeys, entry point = proportional position along the shared
   edge (`mapToFraction`), inset 1–3 px so it does not immediately re-trigger,
   release all keys on leave, clipboard pushed on switch when dirty and
   immediately when the remote side is active, size cap.

### Unverified and load-bearing (spike before building on them)

- **U1 Hiding/freezing the Mac cursor while the app is _not_ frontmost.** Apple
  documents `CGDisplayHideCursor` and `CGAssociateMouseAndMouseCursorPosition`
  as foreground-only. Deskflow/Barrier solve it with the private
  `CGSSetConnectionProperty("SetsCursorInBackground")` before
  `CGDisplayHideCursor`. This is a personal build, so that trick is on the
  table and is the first thing to test; Apple DTS notes it is blocked while the
  Dock would own the cursor, and nobody has confirmed it on macOS 26. Public
  fallbacks suggested by DTS: a non-activating panel at `.screenSaver` window
  level plus `NSCursor.hide()` on a timer, or parking the cursor with
  `CGWarpMouseCursorPosition` on every swallowed event. Today the toggle is
  flipped inside the app window, so the app _is_ frontmost when capture starts;
  with edge switching it will not be.
- **U2 `kCGMouseEventDeltaX/Y` keep flowing while the cursor is pinned at an
  outer display edge** with association on. Inferred from the field semantics
  and Deskflow; not stated by Apple.
- **U3 bluetoothd sends Service Changed when an app adds a service, and Windows
  then discovers the new service without a re-pair.** Strongly implied by
  Apple's Accessory Design Guidelines and by the existing hack, but not traced.
  Fallback is a one-time re-pair.
- **U4 Report-map changes require a Windows re-pair.** Universal
  firmware-community practice, no Microsoft statement.
- **U5 `SetCursorPos` from a medium-integrity process while an elevated window
  is foreground.** `SendInput` is documented as UIPI-blocked; `SetCursorPos` is
  not documented either way.
- **U6 Exact `RIDI_DEVICENAME` string** for the Mac's HOGP mouse on Windows
  (expected to contain `{00001812-…}` and `VID&01ffff_PID&0001` from the PnP ID
  in `HIDProfile.pnpIDValue`).

---

## 3. Target architecture

### 3.1 Components

**Mac (existing app, macOS-only after M0)**

- `EdgeSwitchCoordinator` (`@MainActor final class … ObservableObject`, owned by
  `DeusKVMApp` as a `@StateObject`): the single source of truth for who has
  control. States: `local` (tap passes through), `remote` (tap swallows and
  forwards HID), `returning`. "Switch to remote" is what the Direct Input
  toggle does today; the edge trigger and the hotkey are new ways to invoke it,
  and the companion is only *told* about it. Each handoff gets a `switchId`
  (u8, incrementing); ACK/LEAVE carrying another id are ignored. Owns
  `InputTap`, `EdgeGeometry`, `CursorConcealer`, drives `DirectInputController`, talks
  to the companion link, runs the clipboard watcher.
- `InputTap`: **one** always-installed active session tap (`.defaultTap`, mask
  = the existing keyboard, mouse and scroll events), running on its own thread
  with its own `CFRunLoop` (Deskflow's model). While `local` the callback does
  the edge check inline and returns the event untouched; while `remote` it
  returns `nil`. The mode is one atomic flag flipped inside the callback, so no
  event can leak in the handoff, and the toggle hotkey is matched in the same
  place in both modes. The callback never hops to the main actor; it reads an
  immutable geometry snapshot and posts state changes out. (This replaces the
  earlier listen-only + active two-tap idea, which had a race window between
  the two taps.)
- `EdgeGeometry`: CG global space (`CGGetActiveDisplayList`,
  `CGDisplayBounds`); chosen display persisted as
  `CGDisplayCreateUUIDFromDisplayID` string; outer-edge segments recomputed on
  `CGDisplayRegisterReconfigurationCallback` and published to the tap as a new
  snapshot. Trigger = cursor on the last pixel column/row of the chosen edge
  **and** outward delta, then the gates from §2.9.
- `CursorConcealer`: hides the cursor **and guarantees it cannot hover
  anything** while remote. Fixed sequence on switch-out: (1) warp the cursor to
  a parking point (center of the configured display, away from Dock and menu
  bar); (2) order in a 1×1 non-activating `NSPanel` at `.screenSaver` level at
  that point, so the cursor is technically over our window and everything else
  gets one `mouseExited` and nothing more (Deskflow's Windows-side "hider
  window"); (3) freeze with `CGAssociateMouseAndMouseCursorPosition(false)` so
  the position never changes and no tracking-area, cursor-rect or hover logic
  anywhere can fire; (4) hide via the strategy chosen in S1: Deskflow's
  `CGSSetConnectionProperty("SetsCursorInBackground")` + `CGDisplayHideCursor`
  (private API, fine here) or `NSCursor.hide` with a re-hide timer; (5)
  belt-and-braces: on every swallowed motion event re-warp to the parking point
  with warp-delta compensation, so even if the freeze silently fails in the
  background the cursor snaps back before anything can react. Reverse order on
  switch-in.
- `DirectInputController` (existing): preserve its input translation and HID
  report generation. Adapt tap ownership/lifecycle only as needed for the single
  tap and coordinator; a separate `HIDForwarder` extraction is not required.
  Replace the hardcoded Ctrl+Alt release with the configurable toggle hotkey,
  and move its status indication into the app's menu-bar UI. Keep existing key
  mappings, modifier encoding, mouse clamping and scroll behavior for M2. The
  coordinator handles permission checks and the Secure Input release. The hotkey
  is checked locally in the same tap in both modes.
- `CompanionService` (`DeusKVM/LowEnergy/CompanionService.swift`): one custom
  128-bit primary service added in `HIDPeripheral`'s `didAdd` for
  `HIDProfile.hidService`, **before** `startAdvertisingNow()`, never removed at
  runtime, never advertised. Characteristics, all encryption-required so the
  existing HID bond covers them: `ctrl` notify (Mac→PC, single-chunk hot path),
  `ctrlW` write-without-response (PC→Mac hot path), `bulk` notify and `bulkW`
  write (clipboard/config chunks), `status` read.
- `CompanionLink` (M3 onward): framing, reassembly, per-stream FIFO,
  HELLO/PING state, and the message types below. Add queueing for the new
  companion traffic, with ctrl ahead of bulk, while preserving existing HID
  report behavior. Integrate notification readiness narrowly: drain eligible
  companion chunks until empty or `updateValue` returns `false`, then resume
  from `peripheralManagerIsReady`. Test simultaneous HID and companion traffic
  at M3/M4. A HID queue rewrite or movement-merging policy is not part of M2;
  change the existing send path only if a reproduced issue requires it.
- `ClipboardPasteboard`: polls `NSPasteboard.general.changeCount` every 200 ms
  on a dedicated queue while sharing is available. Records imported revisions
  for echo suppression and skips private/generated/file clipboard markers.
  `MacClipboardSync` owns availability, handoff and transfer coordination.
- App shell: a status-bar agent with no Dock icon. `LSUIElement = true` in
  `Info.plist` (never `LSBackgroundOnly`, which breaks active event taps on
  macOS 15+), a `MenuBarExtra` whose icon shows local/remote/blind and whose
  menu opens the `Settings` scene (both macOS 13+), optional
  `SMAppService.mainApp.register()` behind an explicit toggle. New
  `LayoutSettingsView` pane.

**Windows companion (`windows/` in the same repo, C# .NET, installed service
plus desktop worker and optional WinForms tray UI)**

**Required lifecycle:** start automatically at Windows boot, before anyone signs
in, and remain available after sign-out and while locked. Closing the tray UI
must not stop the companion. This is Windows sign-in-screen support; firmware
and pre-boot disk-unlock screens are outside the Windows service's lifetime.

- `DeusKVM.Companion.Core` (UI-free): protocol framing, reconnect state machine,
  selected paired endpoint, and validated configuration. Keep the working BLE
  HID input path; the companion provides connection recovery and coordination.
- `DeusKVM.Companion.Service`: installed with the Service Control Manager,
  automatic startup and recovery on failure. Own the BLE connection, custom
  GATT subscriptions, HELLO/PING/STATE, and reconnect/service rediscovery.
  Handle Bluetooth readiness, device changes, power changes, and console-session
  logon/logoff/lock/unlock transitions without relying on Explorer or a user
  startup entry. Store machine configuration in an ACL-protected `%ProgramData%`
  directory. Initial pairing and device selection happen during setup; subsequent
  boot/reconnect must not require user consent dialogs or a logged-in account.
- **Service-account BLE access is a pass/fail spike, not an assumption.** Test
  paired-device enumeration, uncached discovery, notifications and writes under
  the actual service identity before first login and after logout. Start with
  the tested WinRT GATT path; evaluate native `BluetoothGATT*` APIs if service
  context prevents it. Select the service identity/privileges from those results;
  do not require a saved personal account password or an interactive login.
- `DeusKVM.Companion.DesktopWorker`: service-managed worker in the active
  physical console session, with the desktop access needed for `EdgeMonitor`
  and `CursorPlacer`. Services run in Session 0; `SetThreadDesktop` alone does
  not move a service into the console session. Prove worker launch and desktop
  access at Winlogon before login, after sign-out, while locked, and during UAC.
  Use Raw Input on a worker-owned window, filter to the Mac device, and retain
  the planned pinned-cursor, push, corner/button and ClipCursor gates. Place
  via `SetCursorPos` on the input desktop and verify the resulting coordinates.
  Recreate desktop-bound workers/windows when required; discard stale ENTER,
  LEAVE and placement results across session/desktop changes. Only the active
  console worker may influence control; fast user switching must not leave an
  old worker controlling the new session. Report actual capability failures as
  blind; a running service does not by itself prove desktop access works.
- `DeusKVM.Companion.App`: optional, unprivileged WinForms `NotifyIcon` and
  settings UI for device/monitor selection, thresholds, clipboard and status.
  Local, session-scoped named-pipe IPC with explicit ACLs connects the UI and
  workers to the service; privileged requests are validated. Install/uninstall
  requires elevation; service startup and ordinary status/settings viewing need
  no UAC prompt. Administrative changes may elevate the specific operation.
- Tray service controls: **Start Service**, **Stop Service**, current SCM status,
  and **Start automatically with Windows** (checked by default). The checkbox
  selects Automatic versus Manual startup; it does not start/stop the running
  service. Manual Start runs until explicitly stopped or Windows shuts down,
  including through logout. Manual Stop stays stopped until Start or the next
  boot with Automatic enabled; failure recovery must not undo an intentional
  stop. **Quit Tray** only closes the UI. Service control and startup changes
  use Windows service permissions, elevating only the operation when needed;
  do not grant general service-configuration rights to unprivileged users.
- The service owns switching state and clipboard coordination. Desktop workers
  perform session-bound operations on its behalf; the tray owns neither.
- `ClipboardWatcher` (M4) runs in the logged-in user session, never on Winlogon
  or a secure desktop. Suspend sync while locked/logged out and clear queued
  clipboard payloads on session changes. Keep user clipboard data out of the
  machine-wide service configuration and isolate different users' sessions.
- Package the service, worker and UI as one EXE. Opening it installs or updates
  when needed and opens the settings window, with Start/Stop/Automatic controls
  in both the window and tray. No setup scripts are needed. Updates preserve
  configuration and service startup/running state. The existing HKCU Run-only proposal is
  superseded; an optional tray startup entry is not responsible for availability.

References: [Microsoft service/desktop isolation](https://learn.microsoft.com/en-us/windows/win32/services/interactive-services),
[SetCursorPos desktop requirements](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setcursorpos),
and [native GATT discovery](https://learn.microsoft.com/en-us/windows/win32/api/bluetoothleapis/nf-bluetoothleapis-bluetoothgattgetservices).
These document constraints and candidate APIs, not a verified pre-login prototype.

### 3.2 Control flow

```
Mac local: tap passes events through; edge check inline
  cursor reaches configured edge + gates pass, in the tap callback:
    → precondition (existing state): the target central is subscribed to HID;
      otherwise nothing happens and the edge is inert
    → that event is swallowed, mode := remote, switchId += 1                (suppression starts here)
    → CursorConcealer parks, freezes, hides
    → first HID reports: keyboard and mouse all-up; normal handoffs only commit
      once physical keys, modifiers and mouse buttons are released
    → from now on every event is swallowed and forwarded to the PC as HID immediately
    → if a companion is present: send ENTER{switchId, edge, frac}, fire and forget
  ENTER_ACK{switchId, ok, x, y, blind}, if it ever arrives: companion did SetCursorPos;
    Mac only updates the blind indicator. Nothing waits on it.
Mac remote: companion sees the Mac's raw deltas pushing into the configured PC edge, gates pass
    → LEAVE{switchId, edge, frac}; Mac ignores it if switchId is stale or physical
      input is held; when blocked, a fresh edge request after release is required
    → Mac: mode := returning; all-keys-up + all-buttons-up reports; warp to mapped point 2 px inside
      the edge; associate(true); drop the next delta; un-conceal; mode := local (tap passes through)
Companion heartbeat STATE{blind, desktop, macMousePresent} every 3 s; menu bar shows blind state
```

**Handoff rules.**

- *Suppression begins when the handoff commits.* An eligible outward motion
  event is swallowed by the callback that commits the edge switch. If input is
  still held, control remains on the current machine and events keep their
  existing routing until released. The final release must reach that machine
  before the handoff commits; never swallow that release to trigger a switch.
- *Nothing waits on the companion.* The switch itself is the existing Direct
  Input behaviour and depends only on state `HIDPeripheral` already tracks
  (`subscribedCentrals`, notification readiness). ENTER is sent alongside if a
  companion is connected; its ACK only places the PC cursor and reports blind
  state. Motion sent before the ACK moves the PC cursor from wherever it was,
  then `SetCursorPos` places it once and deltas continue from there. A blind or
  absent companion therefore never blocks going to the PC (a UAC prompt or lock
  screen is exactly when you want to type there), and the return guarantees
  (§3.5) do not depend on it. A late ACK for an old `switchId` is ignored.
- *Release before switching.* Track physically held keys, modifiers and mouse
  buttons in the Mac tap in both modes, including keys with no HID mapping.
  Seed/check the held state when enabling capture so keys pressed before the
  watcher started are not missed. Caps/Num Lock's latched state is not a held
  modifier. Normal edge, hotkey and manual handoffs wait for all physical input
  to be released. While waiting, key-up, modifier changes, button-up and
  autorepeat continue to the current machine through the existing input path.
  Commit only after the final release has passed through locally or has been
  handed to the existing HID send path remotely. No synthetic key reconciliation
  or HID queue rewrite is required for this gate; verify release behavior at M2.
- *Pending requests.* A Mac edge request remains eligible only while the cursor
  stays at the configured edge and its gates remain valid; moving away cancels
  it. For PC edge-return, ignore LEAVE while physical input is held and require
  a fresh edge request afterward, rather than acting on a stale PC position.
  A hotkey/manual request latches one switch and commits after release without
  requiring an edge. Consume the toggle key's down, repeats and matching up
  locally; continue routing modifier releases to the current machine. Holding
  the shortcut must never toggle repeatedly. Clear pending requests on return,
  link loss, or configuration changes that invalidate them.
- *Automatic releases bypass the held-input gate.* Link loss, tap disable,
  Secure Input and normal quit still restore the Mac without waiting for key-up
  events that may no longer arrive. All-up reports remain best-effort on these
  failure paths. Hotkey return waits only for the user's physical releases,
  never for a PC or companion response.

**Return paths.** Edge detection on the PC is the normal path. The toggle
hotkey is the escape hatch when something on the PC prevents edge-return
(for example, a UAC prompt, lock screen, or an unavailable companion).

1. Companion `LEAVE` (normal path).
2. **Toggle hotkey**: one combination that focuses the other machine.
   In `local` it hands control to the PC and centers the cursor on the selected
   Windows display (planned M5 behavior, also used by Switch to PC buttons); in `remote` it hands
   control back and warps the Mac cursor to where it was when control left,
   not to an edge (Deskflow's jump-cursor-pos behaviour). Fully user-mapped in
   Layout settings (any key with any modifiers, or disabled). Default:
   Fn/Globe + Escape, because the Fn key has no HID usage and is never
   forwarded, so the default cannot collide with a Windows shortcut. Matched
   locally on the Mac on `keyDown` of the full combination, before HID
   forwarding. Latch the request on key-down and switch after the shortcut
   and other held input are released; returning never needs a response from
   the PC or companion.
3. Automatic release: BLE link to the PC drops or the PC unsubscribes; an
   established companion heartbeat is missed 3 times (9 s) *and* the user is
   still moving the mouse; the active tap is disabled by the system; display
   configuration changes remove the configured edge; or the app quits
   normally. Each path restores local input and best-effort sends all-keys-up
   and all-buttons-up reports. No companion heartbeat is expected in M2 or
   when no companion session has been established.

Entry-point mapping both directions = Deskflow's:
`t = (pos - edgeStart + 0.5) / edgeLength` on the departing edge, mapped through
the configured span onto the arriving edge, inset 1–3 px.

### 3.3 Wire protocol (BLE-first; the same messages ride TCP later with a length prefix)

**Current M3 control-channel scope.** Use 20-byte chunks throughout this first
edge-return build. Malformed/sequence-gap/CRC failures reset the companion link
and require a new HELLO; NACK/replay and larger negotiated chunks are deferred
until bulk clipboard traffic. No corrupted or incomplete message is acted on.

**Source of truth.** `DeusKVM/Companion/CompanionProtocol.swift` (pure Swift,
no AppKit, unit-tested) defines every constant, enum and encoder below.
`windows/DeusKVM.Companion.Core/Protocol.cs` mirrors it by hand. Both test
suites decode the same golden vectors in `docs/protocol-vectors.json`
(hex-encoded frames with their decoded meaning); a change to one side that
breaks the vectors fails the other side's tests. All integers little-endian.
Protocol version `1`; either side rejects a HELLO with a different major.

**GATT identifiers.** Base UUID `d5dfc674-fd35-4b5c-8fc9-39dd1c43cb1d`; the
third and fourth hex digits of the first group carry a short id, Nordic-UART
style, so `d5df0001-…` is the service and `d5df000N-…` the characteristics.

| Short id | Role                                          | Properties                                  |
| -------- | --------------------------------------------- | ------------------------------------------- |
| `0001`   | Companion service (primary, never advertised) |                                             |
| `0002`   | `ctrl` Mac→PC, single-chunk hot path          | notify, encryption required                 |
| `0003`   | `ctrlW` PC→Mac, single-chunk hot path         | write without response, encryption required |
| `0004`   | `bulk` Mac→PC, chunked streams                | notify, encryption required                 |
| `0005`   | `bulkW` PC→Mac, chunked streams               | write (with response), encryption required  |
| `0006`   | `status` snapshot for debugging               | read, encryption required                   |

**Chunk framing** (one notification or write; `chunkSize = min(maximumUpdateValueLength, MaxPduSize−3, 244)`, floor 20):

```
byte 0   seq      u8, independent per characteristic and direction, wraps at 255
byte 1   flags    bit0 FIRST, bit1 LAST, bit2 ACK_REQ (reserved, never set in v1), bits4–7 stream
                  stream 0 = ctrl (always FIRST|LAST, fixed binary, ≤ 13 payload bytes)
                  stream 1 = meta (JSON: HELLO, SCREEN_INFO, CONFIG)
                  stream 2 = clip
                  stream 3 = file (v2)
FIRST:   byte 2   msgType u8; bytes 3–6 total payload length u32   (7-byte header → 13 payload bytes at chunk 20)
payload
LAST of a multi-chunk message: CRC-32C (Castagnoli) of the reassembled payload, u32
```

Bootstrap: until both HELLOs have been exchanged every chunk is 20 bytes (the
floor), which is why HELLO rides the meta stream as a multi-chunk message.
After HELLO each side uses `min(own chunk, peer chunk)`.

**Message types and fixed-binary payloads** (ctrl stream unless noted):

| Type   | Name        | Direction | Payload                                                                                  |
| ------ | ----------- | --------- | ---------------------------------------------------------------------------------------- |
| `0x01` | HELLO       | both      | meta stream, JSON, see below                                                             |
| `0x02` | PING        | both      | `ms u32` sender monotonic clock                                                          |
| `0x03` | PONG        | both      | `ms u32` echoed                                                                          |
| `0x04` | SCREEN_INFO | PC→Mac    | meta stream, JSON                                                                        |
| `0x05` | CONFIG      | Mac→PC    | meta stream, JSON                                                                        |
| `0x11` | ENTER       | Mac→PC    | `switchId u8, edge u8, frac u16`                                                         |
| `0x12` | ENTER_ACK   | PC→Mac    | `switchId u8, ok u8, x i16, y i16, blind u8` (x,y = physical px where the cursor landed) |
| `0x13` | LEAVE       | PC→Mac    | `switchId u8, edge u8, frac u16` (id of the ENTER being returned from)                   |
| `0x15` | EXIT        | Mac→PC    | `switchId u8`; cancel the current PC edge detector after any Mac-local return             |
| `0x16` | RESUME      | Mac→PC    | `switchId u8, edge u8`; restore current ownership without cursor placement; capability-gated |
| `0x17` | ENTER_CENTER | Mac→PC | `switchId u8, edge u8`; center the selected display for explicit hotkey/button entry; PC HELLO `center:true` required |
| `0x14` | STATE       | PC→Mac    | `blind u8, desktop u8, macMousePresent u8` every 3 s                                     |
| `0x20` | CLIP_GRAB   | both      | `epoch u32, seq u32, bytes u32`; 0xffffffff withdraws an offer                       |
| `0x21` | CLIP_GET    | both      | `epoch u32, seq u32, offset u32`; request one 1024-byte block                         |
| `0x22` | CLIP_DATA   | both      | bulk stream: `epoch u32, seq u32, offset u32` then 0–1024 UTF-8 bytes                                        |
| `0x23` | CLIP_STATE  | both      | `epoch u32, enabled u8`; PC offers availability, Mac acknowledges its preference |
| `0x7E` | ACK         | both      | `stream u8, seq u8` (only in reply to ACK_REQ; unused in v1)                             |
| `0x7F` | NACK        | both      | `stream u8, expectedSeq u8` (drop partial, resend from FIRST)                            |

**Enumerations and scales.**

| Field     | Values                                                                                                                                                              |
| --------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `edge`    | `0` left, `1` right, `2` top, `3` bottom. v1 has exactly one link, so the edge identifies the side; the monitor comes from CONFIG.                                  |
| `frac`    | `0…65535` = position along the edge as a fraction: from the top for left/right edges, from the left for top/bottom. `round((pos − start) / (length − 1) × 65535)`.  |
| `ok`      | `0` failed (cursor not moved), `1` placed                                                                                                                           |
| `blind`   | `0` none, `1` secure desktop (UAC / lock / Ctrl+Alt+Del), `2` elevated window in foreground, `3` Mac mouse not present in Raw Input, `4` input desktop inaccessible |
| `desktop` | `0` Default, `1` Winlogon, `2` other                                                                                                                                |
| `formats` | bitmask: `0x0001` UTF-8 text, `0x0002` HTML, `0x0004` RTF, `0x0008` PNG, `0x0010` file list. v1 uses `0x0001` only.                                                 |
| `format`  | one bit of `formats`                                                                                                                                                |
| `stream`  | as in the flags nibble                                                                                                                                              |

`desktop` reports the current desktop independently of `blind`. Once a desktop
worker is supported there, Winlogon or an elevated foreground app is not by
itself a blind condition; send nonzero `blind` only when detection/placement is
actually unavailable. Validate these semantics in the M3 desktop-transition spike.

**JSON shapes** (meta stream; unknown keys ignored, missing keys take the default shown).

```json
HELLO        {"v":1,"role":"mac"|"pc","name":"Mac Studio","chunk":20,"clipboard":1}
SCREEN_INFO  {"monitors":[{"id":"\\\\.\\DISPLAY1","x":0,"y":0,"w":2560,"h":1440,"dpi":96,"primary":true}]}
CONFIG       {"edge":1,"monitor":"\\\\.\\DISPLAY1","span":[0.0,1.0],"pushCounts":0,
              "switchDelayMs":0,"doubleTapMs":0,"cornerPx":0,"heartbeatS":3}
```

- Each side sends HELLO once after the PC subscribes to `ctrl` and `bulk`;
  nothing else is sent until both HELLOs are in. `chunk` is what the sender can
  receive per chunk (Mac: `maximumUpdateValueLength`; PC: `MaxPduSize − 3`,
  capped at 244); each side then uses `min(own, peer)`.
- PC HELLO includes optional `computerName` (the Windows machine name), separate
  from the companion app's `name`. The Mac seeds a missing device alias from the
  validated handshake's central UUID, without changing control permissions,
  selecting a host, or overwriting an existing alias. Older companions omit it.
- `SCREEN_INFO` is sent after HELLO and again on `WM_DISPLAYCHANGE`. `monitor`
  in CONFIG is the `id` from SCREEN_INFO (`MONITORINFOEX.szDevice`).
- PC HELLO may advertise `"resume":true` (absent means unsupported). When a
  companion becomes ready during an existing remote handoff, the Mac sends
  RESUME on the control stream. Windows retains ownership across console-worker
  startup and applies it once fresh CONFIG is available, in either arrival order.
  EXIT and link reset cancel ownership; late CONFIG alone cannot restore it.
  RESUME never repeats ENTER's cursor placement.
- `span` is the fraction range of the PC edge that maps onto the Mac edge
  (Deskflow link interval); v1 UI exposes `[0,1]` only.
- `pushCounts` and `switchDelayMs` are legacy compatibility fields sent as zero.
  Windows ignores older nonzero values and sends LEAVE on the first outward
  movement at the exposed edge; no accumulation or dwell gate.

**Flow control.** Mac→PC companion traffic: FIFO with ctrl before bulk (§3.1),
drained until empty or `updateValue` returns `false`. Preserve the existing HID
send behavior; validate coexistence at the companion checkpoints. PC→Mac: `ctrlW` chunks
are write-without-response (one chunk, fire and forget); `bulkW` chunks are
write-with-response, so ATT itself paces them and no credit scheme is needed.
Reassembly: one partial message per stream per direction; a seq gap or a FIRST
while a message is partial drops the partial and sends NACK{stream,
expectedSeq}; the sender restarts that message from FIRST.

**Which central is the PC.** In M2 (no companion yet) the target is the single
active central subscribed to the HID report characteristics; if more than one
is subscribed the user picks it in Layout settings (the existing active/inactive
toggle per device). From M3 on, a central that subscribes to `ctrl` and
completes HELLO becomes the target and enables cursor placement and
edge-return; the HID subscription alone still suffices to arm edge switching
(§3.5 rule 8). The target's identifier, not `HIDInput.isConnected`, drives
`EdgeSwitchCoordinator`. HID reports keep going to all active centrals as
today.

### 3.4 Clipboard policy

- v1: UTF-8 text only, LF on the wire, cap 64 KiB over BLE. Grab → `CLIP_GRAB`
  marks the other side dirty; data moves on the next switch, or immediately if
  the other side is currently active. Sequence numbers drop stale data. Echo
  suppression on both sides. Capability `clipboard:1` in HELLO is required;
  older companions keep working without clipboard. CLIP_STATE, not CONFIG,
  controls availability and the Mac sharing preference (default on).
- Windows owns a session epoch. Lock, logout, desktop-worker/link changes and
  disabling sharing discard pending text; old-epoch packets cannot apply.
  Only the selected allowed PC participates. The logged-in desktop worker reads
  and writes text on its own STA; Session 0 and secure desktops never read it.
  Mac access runs off the input/main queue and pauses on sleep/session loss or
  secure input. No clipboard contents are logged or persisted by DeusKVM.
- Send one requested 1 KiB block at a time on the existing bulk stream, keeping
  control messages ahead of bulk and leaving the HID input path unchanged.
  Empty text is valid; malformed UTF-8, NUL and oversized items are rejected.
  Windows converts LF to CRLF on import. New local copies cancel older imports.
  Initial snapshots stay silent until handoff; imported items are never reoffered.
- Skip known concealed/transient/generated and Windows privacy markers before
  reading text. Unmarked text cannot be identified as a password. Unsupported
  items withdraw the offer without replacing the destination clipboard.
  See [clipboard implementation and checkpoint](docs/CLIPBOARD.md).
- v2: hybrid transport. Mac sends `LAN_OFFER{addrs, port, secret, certSha256}`
  over BLE; Mac only _listens_ (`NWListener`, add
  `com.apple.security.network.server`; no Bonjour so no Local Network prompt on
  macOS 15+); Windows only connects out (no firewall prompt); TLS 1.3 with the
  offered fingerprint pinned plus an HMAC hello. Formats: text, HTML (CF_HTML ↔
  public.html), RTF, PNG/DIB, later file lists. BLE stays the fallback for text.

### 3.5 Invariants (an implementing agent must not violate these)

1. **No hidden-cursor side effects on the Mac while remote.** The cursor is
   parked over our 1×1 panel and frozen before it is hidden; every input event
   is swallowed; motion events re-warp to the parking point. Nothing else on
   the Mac may receive an event or observe cursor movement between switch-out
   and switch-in.
2. **Returning is a local operation.** `returnLocal()` is idempotent and
   restores cursor association, balances the selected cursor-hiding strategy,
   removes the parking panel, restores the cursor position, and resumes tap
   pass-through. AppKit cleanup runs on the main actor. Sending all-keys-up /
   all-buttons-up to the PC is best-effort and never delays local restoration.
   Every return path uses this same operation.
3. **The toggle hotkey is the first check in the tap callback**, in both modes.
   It latches one toggle request before HID forwarding, consumes the toggle
   key down/repeats/up, and commits after physical input is released (§3.2).
   Modifier releases keep their existing routing. PC cursor position, desktop
   state, companion availability, and Bluetooth responses cannot prevent a
   hotkey return after the user releases the held input.
4. **The system disabling the tap is a release.** On `tapDisabledByTimeout` /
   `tapDisabledByUserInput` while remote: request `returnLocal()` and restore
   the cursor before resuming edge detection.
5. **Secure Input is a release.** Poll `IsSecureEventInputEnabled()` at 1 Hz
   while remote; if it turns on, request `returnLocal()` and show a menu-bar
   warning. With Secure Input on, keyboard events can bypass the tap and
   prevent it from receiving the hotkey.
6. **Link loss is a release.** PC central unsubscribes or disconnects, or an
   established companion heartbeat is missed three times while the user keeps
   moving the mouse → `returnLocal()`. An absent companion does not start a
   heartbeat timeout.
7. **The hotkey is the escape hatch for PC-side failures.** This design does
   not add a watchdog process, launchd recovery helper, or crash-signal recovery
   machinery. Normal app termination restores local input through
   `returnLocal()`.
8. **Never arm edge switching without a PC to receive input.** Edge detection
   is enabled only while the target central (§3.3) is subscribed to the HID
   report characteristics, so the Mac can never hand control to nothing. The
   companion is *not* required to arm (M2 works without it); it adds cursor
   placement and edge-return. Without a companion the menu bar shows "return:
   hotkey only".
9. **Preserve the working HID behavior.** The HID report map, characteristics
   and their order stay byte-identical through M2. M3 adds a separate companion
   service while retaining the existing HID tree. Descriptor, encoding, mapping
   and queue changes require a reproduced checkpoint issue or an explicitly
   requested capability; no milestone schedules them as speculative cleanup.

---

## 4. Milestones

Each milestone ends in something you can use.

### M0 — Toolchain and hygiene

Do these in order, building (and from step 2 on, committing) after each step:
toolchain → iOS removal → Classic removal → sandbox drop → lint fix → test
target → signing → CI. iOS first because it turns the Classic deletion in
`SetupView` into plain code removal.

- Install the build tools the repo expects but this Mac lacks:
  `brew install xcodegen swiftlint swiftformat xcbeautify` (the Xcode project is
  generated from `project.yml`; `build.sh` gates on swiftformat and swiftlint).
- Point the CLI at Xcode instead of the Command Line Tools:
  `sudo xcode-select -s /Applications/Xcode.app`, or prefix builds with
  `DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer`.
- Fetch the two gitignored bundle resources
  `DeusKVM/Resources/company_ids.json` and `service_uuids.json` (Bluetooth SIG
  company and service name tables from Nordic's bluetooth-numbers-database;
  `BluetoothNumbers.swift` uses them to label devices).
  `ci_scripts/ci_post_clone.sh` downloads them and runs `xcodegen generate`;
  already done in this worktree.
- Fix the lint gate: move a namespace out of `L10n.swift` into a new
  `L10n+Layout.swift` (new strings go there anyway).
- Delete the Classic backend: remove `DeusKVM/Classic/`, `TransportMode` and
  the `classic` state object and mode switch in `DeusKVMApp.swift`, the
  Classic branch of `HIDInput.make` (one LE-only `make`), the transport
  picker / paired-devices section / Classic status rows in `SetupView.swift`,
  `GuideView`'s `.classic` case, the `HIDClassicDevice()` in the `#Preview`
  blocks of `ContentView`/`SetupView`/`SettingsView`, and the now-dead
  `L10n.TransportMode`, `L10n.Classic`, `L10n.ErrorMessage` namespaces plus
  their `Localizable.xcstrings` keys. Update the README.
- Drop the iOS target: `project.yml` → `supportedDestinations: [macOS]`,
  remove the iOS deployment target, `IPHONEOS_DEPLOYMENT_TARGET` and
  `TARGETED_DEVICE_FAMILY`; delete `TouchpadView.swift` and the iOS half of
  `DirectInputController.swift` (GameController + `PointerLockHost`); strip the
  `#if os(iOS)` branches from the 14 files that have them (`DeusKVMApp`,
  `ContentView`, `SetupView`, `SettingsView`, `HIDInput`, `KeyboardView`,
  `TrackpadPanel`, `RemoteTabView`, `GuideView`, `DeviceListView`,
  `DeviceInfoView`, `HIDCentral`, …) and then the now-pointless `#if os(macOS)`
  wrappers, keeping the macOS branch (`NavigationStack`, `.formStyle(.grouped)`);
  drop the iOS keys from `Info.plist` (`UIBackgroundModes`,
  `UISupportedInterfaceOrientations`, `UILaunchScreen`,
  `UIApplicationSceneManifest`); delete the iOS section of `build.sh`, the
  `ios` entry in the workflow matrix and release step, the `ios` fastlane lane
  and `fastlane/metadata|screenshots/ios`.
- Drop the App Sandbox: this is a personal build, so the sandbox buys nothing
  and costs entitlement and TCC friction. Set `com.apple.security.app-sandbox`
  to false (or delete `entitlements.plist` and the `--entitlements` argument in
  `build.sh`). Note UserDefaults move out of the container path, so settings
  start fresh once.
- Add `DeusKVMTests` (unit-test bundle, `test:` in the scheme) so protocol
  framing, edge geometry and key mapping get tests from day one.
- Add `.github/workflows/ci.yml` (push/PR, unsigned build + lint on
  `DeusKVM/**`; dotnet build/test on `windows/**`). Leave the tag-triggered
  release workflow alone.
- Signing: `DEVELOPMENT_TEAM: UHD99KF9X7`, `CODE_SIGN_STYLE: Automatic` in
  `project.yml` (see §0), so the Accessibility grant survives rebuilds.
- **Manual checkpoint M0:** launch the app, grant Bluetooth and Accessibility
  once, confirm the existing Direct Input toggle still controls the PC exactly
  as before. Nothing new yet; this is the regression baseline.

### M1 — Spikes with pass/fail gates

| Spike         | What to build                                                                                                                                                                                                                                                                                                                                          | Pass                                                                                                                                                                                                                                                                                                                                      |
| ------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| S1 (U1)       | Tiny unsandboxed app, another app frontmost: first Deskflow's `CGSSetConnectionProperty("SetsCursorInBackground")` + `CGDisplayHideCursor` + `CGAssociate(false)`; then the public fallbacks (`NSCursor.hide()` alone, `.screenSaver` non-activating `NSPanel` + re-hide timer, warp-parking). Test over the Dock and a full-screen Space on macOS 26. | At least one strategy hides and freezes the cursor reliably; record which, it becomes the default in `CursorConcealer`. Also: confirm the local return hotkey restores the cursor and association while another app is frontmost; confirm Fn+Escape arrives in the tap as `keyDown` keycode 53 with `.maskSecondaryFn` (else pick another default). |
| S2 (U2)       | 20-line listen-only tap logging location + deltas while pushing against an outer edge.                                                                                                                                                                                                                                                                 | Deltas keep arriving while pinned. If not: trigger on arrival without a dwell delay.                                                                                                                                                                                                                                   |
| S3 (U3, MTU)  | Add the custom service after HID in `HIDPeripheral`. On a PC that bonded _before_: does Settings/Device Manager show the new "Bluetooth LE Generic Attribute Service" node without re-pairing? .NET console app: open service, subscribe, write, log `GattSession.MaxPduSize`; Mac logs `maximumUpdateValueLength` in `didSubscribeTo`.                | Companion can subscribe and write while HID keeps working. Record whether existing bonds need a re-pair.                                                                                                                                                                                                                                  |
| S4 (U6, §2.4) | Console app with Raw Input + `GetCursorPos`, driven by the Mac's HID mouse: confirm deltas at the clamped edge, coalescing rate, `RIDI_DEVICENAME` string.                                                                                                                                                                                             | Pinned-plus-push condition is detectable; device string known.                                                                                                                                                                                                                                                                            |
| S5 (U5)       | `SetCursorPos` from the console app while Task Manager is foreground; and while locked.                                                                                                                                                                                                                                                                | Know which blind states need reporting.                                                                                                                                                                                                                                                                                                   |

The service-context and pre-login worker gates in M3 extend S3–S5: interactive
console-app results alone do not establish service or Winlogon support.

S1 and S2 the agent builds and runs alone on this Mac. S3, S4 and S5 are
**manual checkpoints**: the agent writes the spike code (Swift changes and a
.NET console app), Amadeus runs them with the PC and reports what Device
Manager, the console output and the cursor did. M2 depends only on S1 and S2,
so it starts while S3–S5 wait for PC time. S3 also tells us whether adding the
service alone forces a re-pair. Do not change the HID report map just because
adding the companion service might require re-pairing.

### M2 — Mac-side edge switch out, hotkey back — first daily-usable build

M2 deliberately works with no companion installed: the edge hands control to
the PC over HID alone, the PC cursor stays wherever it was (no placement until
M3), and the way back is the toggle hotkey or an automatic release. Rule 8 in
§3.5 is written to allow this.

- `EdgeSwitchCoordinator` + `InputTap` (single always-on active tap on its own
  thread) + `EdgeGeometry` + `CursorConcealer` (parking panel + freeze + hide
  strategy from S1), owned by `DeusKVMApp`. Implement the §3.5 invariants
  here, including `returnLocal()`, the local toggle hotkey, and tap-disabled
  and Secure Input releases.
- Move `DirectInputController` ownership to the app/coordinator instead of
  `ContentView`; delete `SetupView.onDisappear { directInput.stop() }`. Keep
  its existing translation and forwarding logic. The manual Direct Input
  toggle calls the coordinator, as do edge activation and the return hotkey.
  Adapt tap ownership only as needed; remove the controller's own status item.
- Move backend start-up out of the window's `.onAppear` into `init()`/app
  delegate so it runs with no window open.
- `MenuBarExtra` (icon reflects local/remote/blind), `Settings` scene hosting
  the existing tabs plus a first `LayoutSettingsView`: pick display
  (`NSScreen.screens`, persisted by display UUID), pick edge, enable toggle,
  return hotkey. `AppSettings` keys: `edgeSwitchEnabled`,
  `edgeDisplayUUID`, `edgeSide`,
  `cornerSizePx`, `toggleHotkey`, `clipboardSync`.
- Switch gates: arrival + outward delta, corner exclusion, no
  switch while any physical key, modifier or mouse button is down,
  lock-to-screen toggle. Apply the release-before-switch rules from §3.2 to
  edge, hotkey and manual handoffs; preserve the existing HID translation.
- Toggle hotkey (default Fn+Escape, user-mappable or disabled): local → remote
  starts capture; remote → local restores the Mac cursor to its remembered
  position. Plus the automatic releases from §3.2, which warp 2 px inside the
  configured edge. Every return re-associates the cursor and drops one delta.
  The old Ctrl+Alt behaviour is removed.
- Preserve existing key mapping, mouse/scroll encoding, report descriptors
  and HID notification queue. Record any actual input problems at the manual
  checkpoint and fix only those reproduced problems before retesting.
- Exit: push past edge → PC controlled, cursor hidden; toggle hotkey flips
  focus either way with the cursor where you expect; pulling the PC's Bluetooth
  or quitting the app also gives the Mac back.
- **M2 is an iteration loop, not a gate to rush through.** Expect several
  rounds of checkpoint → bug fix or tunable change → rebuild → checkpoint
  before M3 starts. Functional bugs and feel problems found here (push
  behavior, hotkey choice, what happens at corners, how the
  cursor lands on return) are cheapest to fix now, and M3 only adds the PC-side
  half on top of this behaviour.
- **Manual checkpoint M2:** (a) push past the edge, drive the PC, confirm no
  hover/highlight changes anywhere on the Mac while you do; (b) toggle hotkey
  both ways; (c) turn the PC's Bluetooth off mid-session → Mac comes back by
  itself; (d) quit the app mid-session → cursor comes back; (e) focus a
  password field on the Mac before switching → switch is refused or released
  with the Secure Input warning; (f) hold a letter until it repeats, or hold
  Shift/Ctrl, then request a switch: input stays on the current machine until
  release, with no stuck key/modifier afterward; test both directions and
  holding the toggle shortcut to confirm it switches only once.

### M3 — Companion service: pre-login availability, edge return and cursor placement

- Windows reconnect: retain the selected paired BLE endpoint and initiate
  connection/service rediscovery when the Mac returns. One-shot uncached GATT
  discovery has restored HID control without re-pairing in the live M2 test;
  verify automatic recovery across normal Mac quit/relaunch and PC sleep/radio
  cycles. See docs/BLUETOOTH-RECONNECT.md.
- Mac: `CompanionService`, `CompanionLink` framing and companion send queue,
  HELLO/PING/STATE handling, `ENTER`/`ENTER_ACK`/`LEAVE`, secure-input warning.
- Mac: retain the existing HID descriptor and report encoding. If S3 shows
  the companion service requires re-pairing, remove the PC bond and pair again;
  otherwise retain it. Do not bundle unrelated HID changes into this milestone.
- First gate: install a minimal boot-started service and prove existing-pair
  BLE reconnect/discovery and custom GATT traffic before login and after logout.
  Then prove the service-managed console worker can monitor/place the cursor
  across Winlogon/Default desktop transitions. A tray-only prototype does not
  satisfy this gate. If a required path is unavailable, report the concrete
  blocker before continuing with a reduced scope.
- Windows: `windows/` solution as in §3.1, service installer/lifecycle,
  session/desktop worker, optional tray/device picker, `EdgeMonitor` from S4,
  `CursorPlacer` from S5, blind-state reporting, GitHub Actions publish job.
- Mac Layout pane gains the PC side: monitor list from `SCREEN_INFO`, edge
  picker, push threshold, and the pairing status of the companion.
- Exit: both directions by mouse alone; the toggle hotkey and automatic
  releases still work. The service is available before login and after logout;
  the menu bar reports blind when the desktop worker actually cannot operate.
- **Manual checkpoint M3:** re-pair only if needed; both directions by mouse with
  the cursor landing at the matching height; open Task Manager and a UAC prompt
  on the PC and verify cursor/edge capability, accurate blind status on failure,
  and that the toggle hotkey still returns; stop the Windows companion while controlling the PC and confirm
  the hotkey still returns immediately; sleep and wake the PC and confirm the
  companion reconnects. Additionally: reboot the PC and use it at the sign-in
  screen before the first login; restart DeusKVM while the PC is signed out;
  sign in, lock/unlock, sign out, and switch users. Verify Bluetooth recovery,
  cursor placement and edge return in each state, not just a running service.
  Closing the tray must leave control available. The Mac hotkey remains the
  escape hatch throughout; no separate emergency recovery process is added.

### M4 — Clipboard v1, text over BLE

- **Status:** implemented; Amadeus confirmed live copy/paste works well in both
  follow-up reports on 2026-09-14. Move on to M5. The specific stress/privacy
  checks below have automated coverage but were not individually confirmed.

- `ClipboardWatcher` on both sides, `CLIP_GRAB`/`CLIP_GET`/bulk stream, echo
  suppression, transient/concealed skipping, 64 KiB cap, sequence numbers.
- Exit: copy on either machine, paste on the other, no ping-pong loops, secrets
  from password managers not synced.
- **Manual checkpoint M4:** copy/paste text both ways, including a 20 KB block;
  copy marked private text and confirm it does not cross. Test real password
  manager behavior using a disposable test value, since markers vary by app.

### M5 — Feel and polish

- **Manual validation update (2026-09-14):** Amadeus confirmed the Mac header
  scrolling appearance and the remaining polish checks: cleanup after the fix,
  launch-at-login, hotkey/button cursor centering, and disabled/connection icon
  states. These checks are complete; the implementation-time deferrals below
  are historical. Documentation and the DeusKVM rename are now implemented;
  the fresh-install rename still needs native confirmation.
- **Current implementation (2026-09-14):** explicit-switch centering, in-app
  Windows pairing, complete Windows removal, Windows tray startup, optional Mac
  login startup and the Mac enable/disable control are implemented. Builds and
  automated tests are the validation for this pass; native interaction testing
  is deferred to Amadeus this evening. No app was launched/restarted and no
  computer-use tests were performed during implementation.
- **Mac enable/disable:** Settings and menu bar share one persisted enabled
  state. Disabling restores local input, stops advertising/capture/scanning and
  clipboard/control exchange; the menu icon changes to a pause symbol. Keep
  allowed devices and OS pairings. Enabling restores the saved setup and lets
  the Windows service reconnect. Disabled state survives app restart/login.

- Input feel: address only problems reported at checkpoints. Horizontal
  scrolling, alternate scroll accumulation, wider movement reports, added key
  mappings and media keys are candidates if requested or needed for a reproduced
  issue, not automatic changes to the working input path. Any descriptor change
  includes a focused validation and re-pair checkpoint.
- Double tap, per-edge spans (percent range like Deskflow links),
  lock-to-screen, remembered Mac exit point for hotkey return, wake PC display on
  enter (consumer report), toggle-key (Caps/Num) sync using the LED output
  report, first-run flow (Mac permissions → install companion → Connect a Mac
  in the companion → Enable control on the Mac → pick edges), launch at login (`SMAppService`, opt-in), companion
  tray states, Windows tray auto-launch after user login (deferred to final
  polish; independent of boot-started service), README rewrite.
- App naming: Amadeus chose **DeusKVM** and requested a full source/identity
  rename on 2026-09-14. Use DeusKVM for folders, filenames, project/scheme names,
  namespaces, app/service identities, preference keys, paths and packaging.
  The Mac bundle ID is `io.github.amadeus.deuskvm`; Windows uses the
  `DeusKVMCompanion` service and DeusKVM installation/data directories.
  No automatic migration is required: Amadeus will clean up the previous
  Windows installation and regrant Mac permissions before configuring again.
  Remove branding-migration code. Keep the upstream acknowledgment and link as
  the README's final section. Native fresh-install validation remains for Amadeus.
- Explicit **Switch to PC** actions, whether invoked by hotkey or any button/
  menu action, always place the pointer at the center of the selected Windows
  display. Edge crossings continue to use proportional placement on the entering
  edge. This replaces the earlier remembered-PC-position proposal for hotkey
  entry. Validate both hotkey and button entry with multiple Windows displays.
- In-app Windows pairing: replace the permanent paired-device dropdown,
  Refresh devices and Use selected Mac controls with **Connect a Mac**.
  Discover nearby candidates, let the user choose the Mac, perform pairing
  through Windows APIs (including required system confirmations), verify the
  DeusKVM service and save the endpoint automatically. Reuse an existing bond
  when available. Once configured, show the selected Mac and connection status
  with **Change Mac…** for replacement. Prototype against the actual Mac before
  finalizing discovery/pairing behavior; Windows Bluetooth Settings should not
  be required for the normal setup flow.
- Complete Windows removal: add **Remove DeusKVM from this PC…** to the tray
  app, with confirmation that includes removing the selected Mac's Windows
  Bluetooth pairing. Request administrator access as needed, stop workers and
  the service, unregister the service, remove startup entries/shortcuts, remove
  that specific pairing, clear remembered devices/preferences/logs and delete
  installed app files, then exit the tray. Remove cleanup helpers after use.
  Other Bluetooth pairings are untouched. A failed step must be reported and
  remain retryable instead of claiming successful removal. The downloaded
  EXE/ZIP can be deleted by the user afterward; removal covers app-managed
  state, not Windows execution/security history. This supersedes the earlier
  suggestion to preserve the selected Mac's pairing during cleanup.
- Polish validation: pair and reconnect entirely through the companion,
  including reusing an existing bond and changing Macs; exercise cleanup from
  a running and stopped service, verify selected pairing/service/startup/settings/
  logs/installed files are removed, and confirm a subsequent launch offers fresh
  setup. In-app pairing and complete removal are deferred to this polish pass.

### M6 — Later

- Clipboard v2 over the hybrid LAN channel (HTML/RTF/PNG/files).
- Further elevated-desktop compatibility beyond the service/desktop-worker
  support required and validated in M3; uiAccess is a candidate only if needed.
  Pre-login and logged-out availability are M3 requirements, not deferred here.
- Optional single-monitor "no companion" fallback with an absolute-pointer
  collection (Report ID 7). Rejected for now (§6); would cost another re-pair
  and needs a game-mode toggle back to relative.

---

## 5. Concrete touch points in the existing code

| File                                                                  | Change                                                                                                                                                                                                                                                                                                                                                                                                     |
| --------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `DeusKVMApp.swift`                                                   | Own `EdgeSwitchCoordinator` (which owns `InputTap`, `DirectInputController`, `CursorConcealer`) as a `@StateObject`; `MenuBarExtra` + `Settings` scenes; start backends without a window; factor the environment-injection chain into a `ViewModifier`; `LSUIElement` in `Info.plist`.                                                                                                                              |
| `ContentView.swift`                                                   | Drop the macOS `@StateObject directInput` and the connect/accessibility auto-prompts (coordinator handles them).                                                                                                                                                                                                                                                                                           |
| `SetupView.swift`                                                     | Delete `.onDisappear { directInput.stop() }`; keep manual toggle; remove the Classic transport picker, paired-devices section and status rows.                                                                                                                                                                                                                                                             |
| `DirectInputController.swift` | Preserve translation and forwarding; adapt ownership/tap lifecycle for edge activation and the local toggle hotkey; move status indication to the app. No speculative key-map, delta or scroll changes. |
| `LowEnergy/HIDPeripheral.swift` | Preserve the existing HID send path through M2. M3 adds the companion service and narrowly integrates its queue/readiness handling; validate simultaneous traffic before changing existing HID behavior. |
| `LowEnergy/HIDProfile.swift` | Add companion UUIDs in M3; retain the HID report map unless a reproduced issue or requested capability requires a change. |
| `LowEnergy/HIDReports.swift` | Preserve existing report layouts and key definitions; change only for reproduced checkpoint issues or explicitly requested capabilities. |
| `HIDInput.swift`                                                      | `isConnected` for a _specific_ central; optional `sendSystemControl`.                                                                                                                                                                                                                                                                                                                                      |
| `AppSettings.swift`, new `L10n+Layout.swift`, `Localizable.xcstrings` | Keys and strings per convention (`"DeusKVM.camelCase"`, `layout.snake_case`, manual/translated entries).                                                                                                                                                                                                                                                                                                  |
| `project.yml`, `.swiftlint.yml`, `.swiftformat`, `.gitignore`         | Test target; include tests in lint; exclude `windows/`; ignore `windows/**/bin`, `obj`, `.vs`.                                                                                                                                                                                                                                                                                                             |
| New                                                                   | `EdgeSwitchCoordinator.swift`, `InputTap.swift`, `EdgeGeometry.swift`, `CursorConcealer.swift`, `LayoutSettingsView.swift`, `ClipboardWatcher.swift`, `LowEnergy/CompanionService.swift`, `CompanionLink.swift`, `Companion/CompanionProtocol.swift` (AppKit-free source of truth, §3.3), `docs/protocol-vectors.json`, `windows/…` with `Protocol.cs` mirroring it |

Conventions to respect: Swift 6 strict concurrency (`@MainActor` classes,
`nonisolated static` C callbacks hopping via `Task { @MainActor in }`),
140-column swiftformat, private helpers prefixed `_`, lowercase comments, no
`// MARK:`, strings via `L10n`.

---

## 6. Decisions

### Decided

- **Personal build only, no App Store.** Consequences applied above: private
  API (Deskflow's CGS cursor trick) is allowed, the App Sandbox goes (M0), no
  review constraints on launch-at-login or an always-on agent, and the Windows
  companion can stay unsigned.
- **Low Energy only; Classic is deleted in M0.** Classic cannot reach Windows
  at all on macOS, and HID over GATT is what every modern Bluetooth mouse uses,
  so there is nothing to lose for a Mac Studio → PC setup.
- **Reuse the tested remote-control core.** Existing remote control worked
  well in Amadeus's testing. M2 adds edge activation and the local return hotkey
  with the minimum necessary ownership/tap/cursor changes. Preserve input
  translation, notification queue behavior, report formats and the HID
  descriptor. Revisit them only for a reproduced checkpoint issue or a later
  explicitly requested capability. M3's new companion service does not itself
  justify a HID rewrite or descriptor change; re-pair only if needed.
- **Drop the iOS target (M0).** Not wanted here. Removing it deletes the
  iOS-only code, all `#if os(iOS)` / `#if os(macOS)` conditionals, and the iOS
  build/CI/fastlane paths, so every later change is simpler.
- **Status-bar agent, no Dock icon.** Both apps live as status/tray icons that
  open a settings window: `LSUIElement` + `MenuBarExtra` + `Settings` scene on
  the Mac, optional `NotifyIcon` + settings form on Windows. The Windows
  service remains running independently of its UI. Remote/Keyboard views have
  been removed at Amadeus's request.
- **Edge detection is the switching mechanism; the Ctrl+Alt chord goes.** The
  only keyboard shortcut is an optional, fully user-mappable **toggle hotkey**
  that focuses whichever machine is not focused (default Fn+Escape, can be
  disabled). The hotkey is the escape hatch when a PC-side condition prevents
  edge-return; it is handled locally and never waits on the PC. Automatic
  releases in §3.2 also restore local input on link loss and tap disable.
- **Windows companion runs as an installed, automatic-start service.** Required
  before login, after logout and while locked. C# .NET service/core, separate
  console-session desktop worker, optional WinForms tray UI. Service-context
  Bluetooth and pre-login desktop access must pass the M3 spikes. The tray is
  not the lifetime owner; this supersedes the asInvoker/HKCU Run-only design.

- **M2 works without the companion.** Arming requires only a PC subscribed to
  HID, not a companion handshake; the companion adds placement and
  edge-return in M3. (Reviewer alternative, not taken: pull the minimal
  companion handshake into M2. Rejected because it makes the first usable
  build wait on the Windows side.)
- **Nothing waits on the companion.** Switching to the PC is the existing
  Direct Input behaviour, gated only on state the app already tracks; ENTER is
  fire-and-forget and its ACK only places the PC cursor. `switchId` makes late
  ACK/LEAVE harmless. (Reviewer alternative, not taken: stay local when ENTER
  is not acknowledged. Rejected because it would block going to a PC sitting
  at a UAC prompt or lock screen, and the return paths do not need the
  companion.)
- **Agent-driven.** The agent implements, builds, tests, stages and commits;
  Amadeus tests only at the manual checkpoints (§0, M0–M4, S3–S5).

Product requirements above are decided. Service-account BLE access and
pre-login desktop-worker mechanics remain engineering gates to verify on Windows.

---

## 7. Risks

| Risk                                                                                   | Mitigation                                                                                                                                                                                                                                        |
| -------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Cursor cannot be hidden from the background on macOS 26                                | S1 first; Deskflow's CGS trick is allowed in a personal build; warp-parking is the public fallback that always works.                                                                                                                             |
| Existing Windows bond does not see the new service                                     | S3; one-time re-pair; keep the Service Changed hack as a fallback with logging.                                                                                                                                                                   |
| BLE throughput too low for clipboard                                                   | Text only in v1 with a cap; LAN hybrid in v2.                                                                                                                                                                                                     |
| Service starts but cannot reconnect BLE or reach the console/Winlogon desktop           | Service-context and pre-login worker gates in M3; verify actual control, not process liveness. STATE/blind indicator and local toggle hotkey remain available.                                                                                                                                                              |
| Two menu-bar icons / capture dying when the window closes                              | Ownership move to the App and removal of `onDisappear` stop (M2).                                                                                                                                                                                 |
| Stuck with a hidden cursor and input going to the PC                                   | Local toggle hotkey returns without a PC or companion response; system tap-disable, Secure Input and link loss also restore local input (§3.5). |
| Lint gate already red                                                                  | Fix in M0 before any PR.                                                                                                                                                                                                                          |
| Swift 6 concurrency friction with new callbacks (display reconfiguration, tap, timers) | Follow the `DirectInputController` pattern (Unmanaged refcon + MainActor hop).                                                                                                                                                                    |

## Implementation progress

- Toolchain installed; baseline unsigned macOS Debug build succeeded.
- M0: moved the existing DirectInput localization namespace out of L10n.swift
  alongside iOS removal to make the first commit pass the existing lint gate.
  SwiftLint also needs DEVELOPER_DIR set on this Mac. Disabled the formatter's
  new environment-entry migration and single-line if expansion to preserve the
  existing conventions and HID code.
- The requested stopping point is the M2 manual checkpoint; M0's manual PC
  regression check will be included there rather than interrupting implementation.

- M0: Classic removed; kept L10n.ErrorMessage because HIDCentral still uses it.

- Signing correction: the installed certificate has OU/team UHD99KF9X7;
  J972UZ26TC in its display name is not the development team. Use the actual
  certificate team and explicit Apple Development identity.

- M0 complete: macOS-only app, Classic removal, sandbox removal, localization
  lint repair, standalone unit tests, verified development signing, and macOS CI.
- M2 implementation: app-owned coordinator, single dedicated-thread tap,
  exposed-edge geometry, release-before-switch gate, local configurable toggle,
  background cursor panel, settings, and persistent menu-bar controls. The
  existing input translation, HID reports, descriptor and peripheral send path
  are preserved. Setup/verification steps are in docs/M2-CHECKPOINT.md.
- Eight focused tests and signed Debug build pass; formatter and strict lint
  pass. Both `build test` actions are needed because the unit-test target is
  standalone. S3–S5 and Windows companion implementation remain for M3.
- S1 preliminary diagnostic: the private background property resolves and
  hide/show calls return success, but disassociation alone did not keep the
  background cursor parked. The implementation therefore uses the planned
  active-tap suppression plus per-motion parking path. Live validation of that
  combined path and S2 physical edge deltas remains part of this checkpoint.
  Until pinned deltas are verified, edge detection also supports arrival plus
  dwell, as permitted by S2's fallback.
- Accessibility: TCC logs confirm the old grant belongs to the upstream signing
  identity, while this worktree uses Amadeus's development identity. The exact
  built app must be added in Accessibility before live capture can be verified.
- Handoff detail: the dwell timer commits only after the final input-release
  callback has returned, so that release is routed to the old machine. The tap
  makes the suppression decision synchronously; ordered main-queue messages
  perform AppKit cursor setup and the unchanged report translation. Normal
  returns use the local hotkey; no recovery helper process is installed.

- M2 live checkpoint: user confirmed control is working after fixing stale
  Shift/Command tracking. Modifier transitions now use the event flags instead
  of querying global key state inside the callback; two regression tests cover
  modifier release and preserving ordinary held keys (ten tests total).
  Preference loading also suppresses writes until all saved values are restored.
- Next requested behavior: proportional Windows cursor placement on edge entry,
  already specified in M3. The current relative HID reports cannot set a screen
  coordinate; the planned companion's ENTER handler supplies that placement.
- Reconnect checkpoint: normal Mac quit/relaunch loses automatic Windows HID
  reconnection. Uncached discovery from Windows restored the existing pairing's
  HID subscriptions, and the user confirmed control still worked after the
  diagnostic exited. Use scripts/Test-DeusKVMConnection.ps1 as the verified
  manual workaround; automate recovery in M3. Peripheral state restoration did
  not fix normal quit and was reverted. See docs/BLUETOOTH-RECONNECT.md.
- Mac-only reconnect follow-up: while the PC remained connected over Classic
  AVRCP, native HID subscriber arrays were empty. A targeted Mac GATT connect
  attempt reported Classic GATT unsupported and remained pending over BLE;
  startup Service Changed and short-form HID advertising did not recover it.
  Temporary probes were removed. Windows discovery remains the only verified
  recovery, although these tests do not prove all Mac-only solutions impossible.

- New Windows lifecycle requirement (2026-09-13): companion must run as a
  service and remain available when logged out, including boot before first
  login. Replaced the tray-owned lifecycle with service + desktop worker +
  optional tray UI; added service-context BLE and Winlogon validation gates to
  M3. That update preceded the service implementation recorded below.

- M3 first service checkpoint: added a self-contained .NET 10 Windows service
  and optional tray/settings UI, Start/Stop/Automatic-vs-Manual startup controls,
  protected machine configuration, and installer/uninstaller. A separate STA
  Bluetooth worker inherits the service account in Session 0, retains the
  selected paired endpoint, requests uncached discovery, maintains the GATT
  session, and retries on connection/service changes. A watchdog bounds stalled
  operations; a Windows job ties the worker lifetime to the service.
- This first gate intentionally uses the existing Mac HID services and does
  not change the Mac app. Windows service-account reconnect, sign-out and
  pre-login recovery must be tested using windows/README.md before advancing.
  Custom GATT subscribe/write coexistence and Winlogon desktop-worker probes
  are the subsequent M3 gates; switching/clipboard coordination are not yet
  implemented by the Windows service.
- Local verification for the first service checkpoint: Windows x64 cross-build
  and self-contained publish pass with zero warnings; 13 core tests pass on
  macOS. PowerShell installer syntax and native-argument round-trip checks pass
  under portable PowerShell 7.6.6. Actual Windows service/WinRT/tray execution
  and Windows PowerShell 5.1 execution remain manual/Windows CI checks. The Mac
  formatter/linter pass; the running Mac app was not restarted or changed.
- M3 live service checkpoint (2026-09-13): Amadeus installed the Windows
  companion, selected the existing paired Mac, and confirmed that restarting
  the Mac DeusKVM app reconnects and restores control without re-pairing. The
  Windows UI showed Service Running, Bluetooth Discovered, and successful
  uncached discovery with BLE Connected. Signed-out recovery and boot before
  first login remain unverified; they are the next manual checks.

- User steering: defer signed-out/pre-login testing and proceed with signed-in
  Windows edge return and matching cursor placement now. Keep those service
  lifecycle checks pending; they no longer block the signed-in M3 work. The
  first desktop worker uses the logged-in console user's token and Default
  desktop; Winlogon access remains a later verification/implementation step.

- M3 signed-in edge implementation: encrypted custom GATT control/meta service,
  conservative 20-byte framing with CRC32C and per-stream sequence validation,
  HELLO/heartbeat, display/config exchange, ENTER placement, ENTER_ACK, LEAVE,
  and EXIT cancellation. Failed framing resets the link and starts a new HELLO.
  Native HID report translation and queues are unchanged.
- The service-owned STA Bluetooth worker coordinates an automatically launched
  console-user desktop worker. An ACL-restricted, process-identity-checked
  named pipe carries bounded messages; Windows jobs tie both worker lifetimes
  to the service. The tray remains optional. No Windows login credentials are
  stored, and the desktop worker runs at the logged-in user's privilege level.
- Desktop worker uses PerMonitorV2 physical coordinates, SetCursorPos, Raw Input
  filtered through HID PnP ancestry to the selected Mac's Bluetooth address,
  exposed-edge detection, a 12-count outward push, and mouse-button/confinement
  guards. The Mac accepts matching LEAVE only while physical keys/buttons are
  released and restores its pointer two pixels inside the mapped edge.
  Handoff IDs and EXIT prevent late messages after a hotkey return; desktop or
  display changes disarm the active Windows handoff. PC monitor selection and
  companion readiness are shown in Layout; readiness is also in the Mac menu.
- Local verification: 21 Swift tests and 28 .NET core tests pass, including
  shared protocol fixtures, CRC/sequence failure, fragment boundaries, screen
  coordinates and stale handoffs. Both apps compile; strict Swift lint passes.
  Actual custom GATT coexistence, desktop launch, Mac mouse identification and
  pinned-edge return remain the next Windows manual checkpoint in windows/README.md.
  Clipboard, Winlogon/elevated-desktop support and signed-out/pre-login testing
  remain pending. This is an implementation checkpoint, not a claim of those
  native runtime checks having passed.

- M3 live follow-up: user reported partial edge-switch success, an intermittent
  visible/moving Mac cursor and laggy PC movement. In the first focused
  five-second movement test, capture diagnostics showed the Mac cursor hidden
  and parked throughout; the return was the requested hotkey. Mac main-thread
  dispatch peaks were 31–41 ms. An idle process sample showed repeated SwiftUI
  layout work driven by unchanged coordinator status publications.
- Avoid publishing unchanged permission, secure-input, target-availability and
  companion-status values during polling. Preserve the polling frequency and
  input/HID routing. Signed Mac build, strict lint and all 21 Swift tests pass;
  a follow-up profile shows reduced idle UI activity. Added aggregate capture
  timing/cursor-health and return-reason logs without input contents or pointer
  coordinates. Live post-fix movement/Windows-edge-return verification remains
  pending; the intermittent cursor report is not considered resolved yet.

- Windows desktop-launch follow-up: runtime logs showed repeated "Access is
  denied" followed by an overwritten generic waiting status. The launch path
  inherited the service BLE worker's Session 0 job into a Session 1 process,
  violating the Windows job session constraint. Permit explicit breakaway for
  this desktop child, create it suspended outside that job, retain the native
  process handle, then resume after pipe supervision is installed. The BLE
  worker remains in its service-owned kill-on-close job. Desktop shutdown uses
  explicit process termination on normal stop and a background pipe pump that
  exits on parent disconnect independently of the desktop message loop.
- Startup errors now retain the failing operation and native error code in
  status.json. The desktop still runs as the console user; no account rights,
  filesystem permissions, or user-facing security settings are broadened.
  Windows build and 32 core tests pass, including pipe EOF with an unprocessed
  desktop message and invalid message lengths. Actual launch/edge return on
  the PC must be retried with the updated Windows package.
- The second Mac movement capture confirms steady-state dispatch delay fell
  from 31–41 ms peaks to 0–3 ms after eliminating redundant UI publications.
  The Mac cursor stayed hidden and parked. Windows edge return remained
  unavailable because the desktop worker was not starting; hotkey return worked.

- Single-EXE Windows setup: opening the published companion now installs or
  updates it through an elevated helper, then opens the installed UI under the
  original user's permissions. Start/Stop and automatic startup are available
  directly in the window as well as the tray. Reopening an already-running
  companion restores its settings window. The downloadable ZIP contains only
  the EXE and README; no setup script or manual tray shutdown is required.
- Updates stage the replacement before stopping the service, close the prior
  installed companion, retain a rollback binary until successful completion,
  and preserve the selected Mac, startup mode and running/stopped state.
  Privileged installation directories retain administrator/SYSTEM write access
  and ordinary-user read/execute access. Windows cross-build/publish and all
  32 core tests pass. Added Windows CI coverage for installation, update with
  the tray open, service controls, state preservation and window reopening;
  that native integration check and live UAC/update behavior have not run here.

- User correction: edge switching must have no intentional delay. Removed the
  Windows 12-count outward-push accumulator; the first outward event at the
  exposed edge now requests return, including when an older Mac sends a
  nonzero threshold. Removed the Mac 250 ms dwell, its UI slider and saved
  preference reads. Normal Mac edge arrival begins capture in the motion
  callback. Only a handoff blocked by held keys/buttons waits for their release
  to finish routing to the old machine. Legacy wire delay/threshold fields
  are zero. Historical dwell descriptions above record the superseded behavior.
- Validation: signed Mac build and 21 Swift tests pass; strict lint has zero
  violations. Windows publish and 35 tests pass, including one-count arrival
  on every edge, held/blocked/inward guards and legacy nonzero CONFIG values.
  The updated Mac app is running and its Layout view no longer has a delay
  slider. The Windows package is ready; perceived round-trip responsiveness
  still needs the user to try the updated builds on the physical machines.
- Return-lag follow-up: the user clarified that the cursor already appears on
  the Mac, then briefly refuses to move. CursorConcealer restored mouse/cursor
  association before its final cursor warp, matching a known macOS suppression
  issue fixed in GLFW. Warp first, reassociate immediately afterward, then show
  the cursor. This is a Mac-only correction; Windows edge/transport code is
  unchanged. Return diagnostics now record synchronous local restoration time
  (not Bluetooth transit or time until the next physical movement). Signed
  build, all 21 Swift tests and strict lint pass; physical feel confirmation
  remains pending.
  Reference: https://github.com/glfw/glfw/commit/157ebb80aafe263b86fd450f21a193f0412fb717
- The reassociation-only change did not resolve the user's return freeze.
  Added a bounded one-second tap diagnostic comparing physical movement with
  cursor-position changes. The live baseline showed first raw movement at
  4 ms, first cursor movement at 261 ms, and 30 movement events at the fixed
  return position, while the restoration calls completed in 3 ms. This
  isolates the observed freeze to local cursor-warp suppression.
- Bracket the final return warp with the legacy suppression interval set to
  zero and restored to its 0.25 s default, then reassociate as before. This
  follows Wine's fallback for systems where reassociation alone is insufficient;
  the symbol is present on this Mac. Only the Mac return warp changes; HID
  translation and Windows code are untouched. The diagnostic logs timing and
  counts only, ends on first movement or timeout, and has regression tests for
  fixed-position movement versus an idle user. Signed build, 23 Swift tests
  and strict lint pass; the new process is running for live comparison.
  Reference: https://github.com/wine-mirror/wine/blob/master/dlls/winemac.drv/cocoa_app.m#L1061-L1081
- Live comparison succeeded: ten post-fix Windows edge returns showed first
  cursor movement at 9–31 ms (most 12–17 ms), with only 0–2 fixed-position
  movement events, versus the baseline 261 ms and 30 fixed-position events.
  The measured quarter-second freeze is removed. The updated signed Mac app
  remains running; no Windows update was needed.
- Windows inactive-cursor checkpoint: after a matching Mac EXIT, the desktop
  worker hides the cursor using a temporary one-pixel nonactivating window at
  its current position. Re-entry reveals it, as do movement/button input from
  another PC mouse. A short-lived mouse hook reveals before click/wheel routing
  and never consumes input, so the first local click can reach the underlying
  application. No cursor-scheme changes, input capture, extra edge delay or
  service/tray entry points are added.
- Keep normal EXIT separate from link reset/failure: only an accepted handoff
  hides the cursor; reset, configuration/display/desktop changes and loss of
  readiness reveal it. The hiding window and hook belong to the desktop process
  and disappear when the service stops that process. Existing Mac input/HID
  code is unchanged. Windows build/publish and all 35 core tests pass; actual
  Windows hide/reveal, first-click delivery and stop-while-hidden behavior need
  the live PC check documented in windows/README.md.
- The user confirmed Windows inactive-cursor hiding works.
- Mac entry performance: retain the prepared cursor-hiding NSPanel between
  crossings instead of constructing and destroying a native window on every
  handoff. Keep it ordered out while local and reposition it on entry. Capture
  diagnostics now include synchronous setup time. Signed build, all 23 Swift
  tests and strict lint pass. The user reports entry feels better; repeated
  crossings measured setup mostly 1 ms (occasionally 8–9 ms), with recent input
  dispatch maxima 9–22 ms versus the earlier 26–42 ms entry samples. These are
  Mac timings, not end-to-end Windows cursor latency; some dispatch spikes remain.
- After this Mac restart, Windows edge return was temporarily unavailable while
  HID control and the hotkey worked. The Layout display picker was initially
  absent despite the ready label, then appeared; edge return recovered without
  another build/restart and repeated successful returns are logged. Late display
  configuration is suspected, not yet proven. Investigate reconnect readiness
  separately; the cursor-window change does not alter the companion protocol.
- Allowed-device checkpoint: Setup now saves an explicit Enable control list.
  New subscribers start unapproved; saved devices remain manageable offline.
  Select one ready allowed device for input, retain that target when another
  device arrives, and expose Use device for an explicit switch between ready
  allowed devices. Revoking permission returns control locally before changing
  recipients. Input notifications go only to the selected target, and other
  hosts reading input reports receive neutral state rather than cached input.
- Advertising is automatic: advertise while no allowed host has both mouse and
  keyboard subscriptions; stop once one is ready, and resume when none remains.
  Track mouse/keyboard report identities separately because all report
  characteristics share UUID 0x2A4D. Battery/media-only subscriptions cannot
  make a host ready. This controls DeusKVM advertising/input delivery; it does
  not remove OS Bluetooth bonds or prevent macOS from accepting other links.
- Validation: signed Mac build, strict lint and all 27 Swift tests pass. Policy
  regressions cover unknown hosts, partial subscriptions, target retention,
  explicit switching, revocation and disconnect/fallback. Live UI checks showed
  Maingear permission off -> Advertising Yes, on -> Advertising No. After an
  app restart, Maingear remained allowed, reconnected without re-pairing and
  advertising stopped automatically. Windows code and HID report map are
  unchanged; final physical edge-switch confirmation is pending.
- Setup layout correction: the fixed-size automatic toggle in the device HStack
  caused an oversized grouped-form row and horizontal overflow. Use an explicit
  switch beneath bounded device labels, constrain the row to its content height,
  and truncate long identifiers. Signed build and strict lint pass. Rendered
  screenshots now verify compact rows and side/bottom padding, including both
  Maingear and the unknown Mac present. The unknown Mac remains unapproved while
  Maingear stays current and advertising remains off.
- Shared form scrolling correction: negative horizontal scroll-content margins
  allowed sideways movement on every tab. Keep those margins at zero and expand
  the form viewport to preserve the 10-point grouped inset; compensate the
  scrollbar inset, clip the viewport and use size-based horizontal bounce.
  Signed build and strict lint pass. Live left/right scroll checks with
  screenshots verify Setup, Layout and Settings stay horizontally aligned.
  Vertical scrolling, the right-edge scrollbar and bottom padding were also
  checked in the running build.

- Keyboard forwarding correction (QMK layout audit): Ctrl+Alt+Delete was missing
  because forward Delete (Quartz 0x75 -> HID 0x4C) was absent from the input map.
  Complete the SDK virtual-key map for navigation, keypad, F13-F20, ISO/JIS,
  application/menu and volume keys; preserve left/right modifiers and synthesize
  Caps Lock press/release pairs from its latch transitions. Capture consumer
  media events, retain held-key state, and release keyboard/mouse/consumer reports
  when returning or disconnecting.
- Preserve raw PC-key identity with a non-exclusive IOHIDManager supplement on
  the tap run loop. Print Screen/Scroll Lock/Pause remain distinct from F13-F15;
  other standard keyboard usages omitted by Quartz (including F21-F24) use the
  same path. Backslash/non-US hash and Insert/Help aliases are also disambiguated.
  Suppress the corresponding duplicate Quartz events, preserve the configured
  local toggle shortcut, wait for raw/media releases before handoff, and release
  keys when their last physical source is unplugged. Input Monitoring is required
  for the supplement; Layout provides the permission action if unavailable.
  The keyboard firmware and Windows companion are unchanged.
- Replace the shared latest-report slot under Bluetooth backpressure with ordered
  keyboard/consumer/button transitions. Only adjacent pure-motion reports retain
  the previous latest-motion behavior; this does not introduce motion accumulation
  or change the mouse descriptor. No GATT/descriptor change or re-pair is needed.
  The existing keyboard report still has six non-modifier slots; overflow now
  emits standard ErrorRollOver and recovers all held keys as they are released.
- Validation: 47 Swift tests cover Ctrl+Alt+Delete press/release bytes, navigation,
  keypad, modifiers, Caps Lock, media release/repeat, raw PC/F-key distinction,
  multiple-keyboard/unplug state, handoff release rules, and notification ordering.
  Signed build and strict lint pass. The restarted Mac app logged raw monitoring
  opened=true/result=0 with existing permissions; Maingear reconnected and Windows
  edge return is ready. User confirmed the Windows keyboard test, including
  Ctrl+Alt+Delete. This does not establish signed-out reconnect behavior.
- Secure-desktop recovery: the Windows desktop worker called handoff.Exit() when
  Ctrl+Alt+Delete made the Default desktop inaccessible. Returning to Default
  restored the ready status but not the active handoff, so edge return stayed
  disabled until a hotkey return and fresh crossing. Suspend observation while
  blind and retain the active switch ID; an actual EXIT/reset/config change still
  invalidates it. Skip monitor enumeration while on a secure desktop, and refresh
  availability on the first returning raw mouse event rather than waiting for the
  one-second status timer. Remember valid ENTER ownership even when secure
  desktop prevents initial cursor placement. Companion update required; physical
  secure-screen-close/edge-return verification is pending. All 38 .NET tests
  pass; the win-x64 self-contained EXE publishes without warnings/errors and
  the replacement ZIP is verified.

- Windows scroll preferences: Settings now has independent vertical/horizontal
  inversion switches, both defaulting off. Save with AppStorage/UserDefaults and
  apply per scroll report so changes take effect immediately, including during
  capture. Only Windows HID scroll axes change; pointer movement and button state
  are preserved. Signed Mac build, strict lint and all 48 Swift tests pass; the
  added regression checks all four settings combinations on diagonal boundary
  deltas, live preference changes and held-button/release reports. Running Settings
  UI verified with both switches and existing compact padding. No Windows update
  or re-pairing is needed for these preferences.

- Live pre-login checkpoint (2026-09-14): after rebooting Windows, Amadeus
  crossed from the Mac and used mouse/keyboard input at the login screen to
  sign in. Pre-login HID control on that boot is confirmed. Edge return from
  the login screen is unavailable with the current Default-desktop worker;
  the Mac hotkey worked. Restarting the Mac app while Windows remains signed
  out and other session transitions still need testing. Immediately after sign-in,
  edge return also failed; the user did not establish whether waiting helped.
- The Windows tray utility did not auto-launch after login. Add tray auto-start
  in final polish, per user steering; no implementation change now. The tray
  is only the configuration UI and is independent of the Windows service.
- User confirmed the independent Windows scroll-inversion switches work.
- Login handoff correction: BLE control previously discarded the active switch
  on console-worker startup and CONFIG, and a late companion HELLO never learned
  about an existing HID-only handoff. Retain authoritative ownership across
  worker/display changes and add capability-gated RESUME for late readiness.
  Reattach the desktop detector after configuration without warping the pointer.
  EXIT/link reset still cancel the handoff. Winlogon edge return remains pending;
  this change addresses continuation on the signed-in desktop.
- Validation: 45 Windows core tests, 49 Swift tests, signed Mac build and strict
  Swift lint pass. Shared wire fixtures cover RESUME; lifecycle regressions cover
  login, worker replacement, reversed control/config arrival, hotkey cancellation,
  link reset and changed edge. Live Windows sign-in continuation still needs testing.
- User confirmed the login handoff correction works: edge return resumes after
  signing in during the existing handoff.
- Pairing-name experiment: removed Setup's Advertised Name editor and custom
  name preference from advertising. Read the local Bluetooth controller name
  for each advertising start; omit the optional name only if unavailable.
  Signed build and strict lint pass. Running app logs confirm advertising as
  `Amadeus’s Mac Studio`; Setup no longer exposes the old name row. Fresh
  Windows discovery/pairing behavior remains for the user to test.
- CI/test cleanup: removed the retired PowerShell Install.cmd/Install.ps1 entry
  point, its NativeCommand helper and argument test/CI step. EXE installation,
  update/state preservation and service lifecycle testing remains in Windows CI;
  the separate development uninstaller remains available. Renamed the now-internal
  peripheral trace method to satisfy CI's identifier naming rule. Formatting,
  strict lint, signed Mac build, 49 Swift tests and 45 Windows core tests pass
  locally. Windows EXE lifecycle validation requires the next Windows CI run.
- Windows CI lifecycle harness correction: cleanup used Stop-Process without
  waiting before deleting the mapped EXE, and cleanup exceptions hid the original
  assertion. Wait for exact test-binary processes to exit, attempt each cleanup
  step, report cleanup errors separately and rethrow the original failure.
  Launch test processes through System.Diagnostics.Process to retain exit codes
  for short-lived commands under Windows PowerShell. Add command progress and
  failure diagnostics. Local PowerShell checks cover parsing, zero/nonzero exit
  codes, termination waiting and injected primary/cleanup failures. Full Windows
  installation/update execution remains dependent on the next CI run.
- Subsequent Windows CI runs both reached settings reopening after passing
  installation, service controls and updates. CloseMainWindow failed because
  the test relied on process-idle readiness rather than refreshing/observing
  the settings window. Replace that assumption and the fixed close sleep with
  bounded waits for actual window appearance, disappearance and reappearance
  on the same live tray process. Local harness checks cover delayed transitions,
  early tray exit and missing/refusing-to-close windows; full Windows CI remains
  the required validation of native window behavior.


### M4 implementation — 2026-09-14

- Plain-text clipboard sharing is implemented on Mac and Windows, including
  the service/desktop-worker bridge, selected-device gating, availability
  handshake, bounded block transfers, privacy markers and loop prevention.
- Settings has a single sharing toggle controlling both directions. No Windows
  tray process is required for sync, and no HID descriptor or pairing changes
  are involved. The tray EXE installs/updates the matching service and worker.
- Automated coverage exercises Unicode/newline/empty text, 20 KB and 64 KiB
  copies, interrupted transfers, stale epochs, local-copy races, duplicate
  announcements, privacy policy, revision tracking and shared wire fixtures.
- Per the overnight work boundary, neither application was launched/restarted,
  no computer-use tools were used and the system clipboard was not accessed.
  Subsequently, Amadeus confirmed live copy/paste works well (2026-09-14).
- Validation: signed Mac build and 62 Swift tests pass; Windows solution builds
  with zero warnings/errors and 64 core tests pass. SwiftFormat and strict
  SwiftLint pass. The self-contained win-x64 ZIP is published and verified.
  Live copy/paste is now user-confirmed. Specific large-payload/privacy/session
  checks and measured hardware transfer timing were not individually reported.


### M5 implementation details — 2026-09-14

- Full deep rename: source/test folders, filenames, Swift project/scheme,
  Windows solution/projects/namespaces, bundle ID, preferences, log/clipboard
  identifiers, service/IPC/startup names, install/data/cache paths and build
  scripts now use DeusKVM. Removed the previous branding-migration code and
  its test fixture. No automatic migration from earlier development builds.
  Repository ownership/reporting metadata now points to this fork; the final
  README section acknowledges and links the upstream project.
- Deep-rename validation: 68 Mac unit tests, 85 Windows core tests, all five
  cleanup tests, diagnostic selection and lifecycle startup-query checks pass.
  Verified the Windows assembly's renamed embedded cleanup resource and service
  identity. Strict Swift lint/format checks pass. No app interaction tests were
  run; fresh-install permissions, pairing and Windows lifecycle remain manual
  or CI validation after pushing.

- Validation for the rename: signed Mac build and 68 unit tests pass; Windows
  build and 85 core tests pass; all five disposable-file cleanup tests, the
  PowerShell startup-query checks, SwiftFormat and strict SwiftLint pass. The
  renamed Windows ZIP and Mac signature/product metadata are verified. Full
  native Windows lifecycle validation requires a new CI run after pushing.
- Renamed the product to DeusKVM and rewrote the root README around the current
  Mac/Windows setup, edge switching, clipboard, startup, update and removal flow.
  Source project names and installed identities now also use DeusKVM; fresh
  installation replaces the earlier compatibility-preserving approach.
- PR #1's two Windows jobs failed in the lifecycle harness's clean-machine
  check: Get-ItemPropertyValue throws for a missing Run value under PowerShell
  5.1 even with SilentlyContinue. Read the key's property collection instead,
  preserving errors for actual access failures. The same correction applies to
  disabled-startup, update-preservation and removal assertions. The Windows
  lifecycle test checks the DeusKVM service, shortcut, startup and removal paths.

- Mac header scrolling appearance is complete and user-confirmed. Restoring
  the form viewport to its native pane bounds allows content to scroll beneath
  the header with the native progressive blur. Side and bottom content spacing
  now uses the native 20-point inset.
- Added capability-gated ENTER_CENTER (0x17, switchId u8 + edge u8). Explicit
  hotkey/button entry centers the selected Windows monitor in physical pixels;
  edge entry remains proportional, and RESUME still avoids repositioning.
  Legacy companions retain their prior entry behavior until updated.
- Windows setup uses Connect a Mac / Change Mac. Discover AEP candidates, reuse
  existing bonds, let Windows handle pairing consent/PINs, verify HID plus the
  DeusKVM GATT service, then save. Verification/save failure rolls back only a
  bond created by that attempt; the previous selected Mac stays configured.
- Removal stops workers/service, removes only the selected Mac pairing, service
  registration, event-source registration, startup entry, shortcut, settings/logs,
  installed files and standard DeusKVM .NET extraction caches. A built-in
  PowerShell process finishes file deletion after the EXE exits, without writing
  a helper script. Failures are reported and preserve a retry marker; opening
  the downloaded EXE again offers to finish removal. OS execution/security
  history and the downloaded EXE/ZIP are outside app-managed removal.
- Tray startup is a separate machine-wide Run entry, initially enabled on this
  update, then preserved across updates. It launches only the icon, never
  starts/stops the service, and does not reopen Settings in an existing tray.
  Mac startup uses SMAppService.mainApp and is opt-in with actual OS status.
- Disable/re-enable serializes tap lifetimes and rejects late readiness events
  from a stopped tap. Bluetooth/input settings and pairings are preserved.
- See docs/POLISH.md for update instructions, manual checks and validation scope.
- Mac menu bar reflects searching, connecting, ready, disabled and Bluetooth
  unavailable states. Ready preserves the existing keyboard/filled-keyboard
  icons and requires the selected allowed HID target plus a fresh companion
  handshake. Subscription changes update the icon immediately; the existing
  refresh timer also detects heartbeat expiry. No input/connection behavior is
  changed. Manual appearance checks remain deferred until Amadeus is home.
  Validation: 68 standalone Swift tests, SwiftFormat and strict SwiftLint pass;
  the updated Mac build is signed. No app launch or computer-use testing.
- Validation: signed Mac build, 65 Swift tests, strict SwiftLint and SwiftFormat
  pass. Windows solution builds with zero warnings/errors; 79 core tests and
  three disposable-file cleanup tests pass locally. The current win-x64 ZIP
  is published and verified. Native Windows lifecycle/GUI tests were not run.
