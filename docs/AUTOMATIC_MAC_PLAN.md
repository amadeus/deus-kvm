# Automatic Mac selection on Windows

Requested 2026-09-21. Status: explicit takeover implemented; verified builds ready, awaiting user setup and hardware checkpoint.

## Current agreed behavior (supersedes the first checkpoint)

Windows owns the grant. Both Macs remain paired; Bluetooth can stay on.
The first connected, enabled DeusKVM Mac wins. A later Mac automatically becomes
**Disabled**. Clicking **Enable DeusKVM** on either Mac explicitly requests
control, disabling the previous owner before the new grant. The previous Mac
acknowledges that local capture stopped and final HID release reports were
accepted for notification. The last explicit Enable request wins. Disabling the
owner alone leaves Windows idle if the other Macs are disabled.

- Pairing remains Add Mac; each Mac keeps its own layout and permission settings.
- Only paired devices verified to expose DeusKVM and HID services qualify.
- Startup cannot recover historical connection times. Among eligible Macs,
  prefer the last active identity, then stable endpoint order.
- Cursor return is separate from ownership. Returning locally retains the grant.
- Epochs reject stale grants across rapid disable/re-enable. GATT retries retain
  arbitration state; release acknowledgement or actual physical disconnect is
  required before another owner. Heartbeat expiry alone cannot bypass release.
- Update Windows and both Macs. Older Mac builds lack release acknowledgements
  and are excluded from automatic ownership in this build.

## Revised phases — explicit takeover (2026-09-21)

The main Mac passed initial use, but the laptop reported ready while edges and
the clickable Switch to PC button did nothing. The exact laptop failure is not
reproduced. The user replaced disconnect-driven priority with full Disable and
then requested automatic disable on arrival plus explicit Enable-to-steal.

- [x] Phase A: negotiated availability, ownership epochs, atomic enable/request,
      release acknowledgement, and a Windows arbitration policy.
- [x] Phase B: keep both companion channels alive; only the granted Mac owns
      desktop, edge and clipboard sessions. Auto-disable arrivals; Enable steals.
- [x] Phase C: restore local capture on disable, retain pairings, preserve final
      input releases under Bluetooth backpressure, and wait for release ACK.
- [x] Phase D: gate edge/hotkey/button entry on ownership and actual capture
      readiness; explain missing permission/display/capture prerequisites.
- [x] Phase E: reconnect maintenance for previously verified paired Macs,
      cross-platform wire fixtures, arbitration and readiness regression tests.
- [x] Phase F: finish signed Apple Silicon Mac and Windows x64 packages in visible
      `releases/`, verify artifacts, record evidence, then pause for user setup.
- [ ] Phase G: user hardware checkpoint in AUTOMATIC_MAC_CHECKPOINT.md; update
      Windows and both Macs, test auto-disable arrival and Enable stealing both ways.
- [ ] Phase H: reproduce/fix any checkpoint failures, prepare replacement builds,
      and complete only after user hardware confirmation.

## Original Windows-only phases (historical)

The checked work below describes the first checkpoint. Its disconnect-driven
handoff behavior and Mac-build compatibility are superseded by the revision above.

## Phase 1 — policy and persistence

- [x] Add a platform-independent first-connected priority policy.
- [x] Cover startup ties, duplicate events, disconnect/reconnect, rejection of
      unrelated devices and stale asynchronous results with focused tests.
- [x] Preserve old settings as a migration/pairing hint, not a selection lock;
      persist last active identity separately for startup ties.

## Phase 2 — paired-device monitoring and active session

- [x] Monitor paired LE endpoints and connection updates on the BLE worker STA.
- [x] Observe connection state before uncached discovery; do not initiate
      background GATT connections to unrelated/disconnected paired devices.
- [x] Verify connected candidates; keep only one active companion session.
- [x] Release priority on disconnect/unpair/removal; dispose superseded sessions
      and reject results/callbacks from earlier connection lifetimes. In-flight
      WinRT operations may finish later; their stale results are discarded.
- [x] Tear down the old companion/desktop/clipboard session before starting the
      replacement; construct its desktop worker with the new Bluetooth address.
- [x] Keep idle monitoring healthy under the service watchdog, including with no
      saved settings. Preserve service start/stop and Session 0 ownership.
- [x] Record observed identities, connection transitions and discovery failures
      in diagnostics without logging input or clipboard contents.

## Phase 3 — UI and lifecycle

- [x] Replace selection controls with Add Mac and live active/waiting status.
- [x] Adding a Mac does not replace or restart a healthy active session.
- [x] Update uninstall to retain pairings and make its confirmation accurate.
- [x] Update setup documentation for automatic selection and compatibility with
      the existing Mac build.

## Phase 4 — automated verification and checkpoint builds

- [x] Build the Windows solution and run core regression tests.
- [x] Review disconnect races, hung WinRT operations, desktop address changes,
      stale configuration and clipboard isolation.
- [x] Publish a self-contained Windows x64 ZIP; verify archive contents and hash.
- [x] Write a short user checkpoint guide and record exact artifact paths here.
- [x] Stop and wait for the user to set up the machines. Do not mark hardware
      results complete based on cross-compilation or unit tests.

## Phase 5 — original hardware checkpoint (superseded)

- [x] Prepared the Windows-only build and paused for user setup.
- [x] Recorded main-Mac initial success and laptop failure (ready, clickable
      Switch to PC, no input/edges). Full two-Mac behavior did not pass.
- [ ] Remaining hardware verification moved to revised Phase G above.

## Phase 6 — checkpoint fixes

- [x] Adopted the user's new full-disable and explicit-takeover scope.
- [ ] Revised Phase H remains pending real hardware observations.

## Evidence and open platform questions

Starting-point code before this work: BluetoothWorker read one CompanionSettings endpoint;
CompanionService waits for that setting before launching it. BluetoothControl
already accepts each Mac's edge configuration. DesktopWorker binds its HID
address at construction, so switching peers must replace that worker.

Microsoft documents that uncached GATT discovery and MaintainConnection can
initiate connections; creating a BluetoothLEDevice alone does not necessarily
connect. Therefore event discovery precedes active probing. Actual paired-device
watcher updates, Session 0 enumeration and HID reconnection require Windows
hardware evidence before claiming the feature complete.

Source: https://learn.microsoft.com/en-us/windows/uwp/devices-sensors/gatt-client

## Validation log

- 2026-09-21: Plan created before implementation. Working tree initially clean.

- 2026-09-21: Implemented the pure priority policy, passive paired-LE watcher,
  per-connection verification/session ownership, isolated desktop/clipboard
  replacement, persisted startup preference, migration hints, active/waiting UI
  and pairing-preserving removal. Mac sources and wire protocol are unchanged.
- Unverified candidates with native errors are temporarily skipped; verified
  active Macs retain priority during retry. A stalled unrelated-device probe is
  quarantined without stopping an active Mac. Native operation cleanup remains
  bounded by worker recycling when no active session can be preserved.
- Windows solution Release build: passed, 0 warnings, 0 errors. SDK 10.0.401 was
  installed under the ignored `.build/dotnet` directory for this checkpoint.
- .NET core regression suite: **97 passed, 0 failed, 0 skipped**. Includes startup
  ties, first-connected priority, reconnect ordering, duplicate updates, stale
  results, retry non-preemption, migration, idle watchdog health and existing
  protocol/handoff/clipboard behavior. The test runner required local socket
  access outside the filesystem sandbox; no user app was launched.
- `git diff --check`: passed. Native Windows service/tray integration and actual
  Bluetooth/input/clipboard behavior were not run on this macOS host.
- Published self-contained **Windows x64** checkpoint:
  `.build/windows/DeusKVM-Companion-win-x64.zip` (52,387,487 bytes).
  ZIP CRC, exact contents and PE x64 architecture verified. Archive contains
  `DeusKVM.Companion.exe`, `README.md` and `CHECKPOINT.md`.
- ZIP SHA-256:
  `4ac979165b91d4085f8048e916dbf866937c958d9751eb5018c96ce90e5995c0`.
  EXE SHA-256:
  `29fda007a7b8eca41308b9a7c73766fdfbb9255082a1d7e2494b4431a5462e7b`.
- Setup/check sequence: [AUTOMATIC_MAC_CHECKPOINT.md](AUTOMATIC_MAC_CHECKPOINT.md).
  Keep existing Mac builds and pairings. No Mac rebuild is required.
- **Waiting for the user to set up the computers.** Phase 5 is entirely pending;
  passive OS HID reconnection, simultaneous-Mac takeover and Session 0 watcher
  behavior remain explicitly unverified. No staged files, commits or pushes.

- Artifact clarification: the old September 14 ZIPs are in the `big-hacks`
  worktree. This checkpoint was built in `main` on September 21 at 22:56 local
  time. An identical dated copy is available at
  `.build/windows/DeusKVM-Companion-win-x64-auto-mac-2026-09-21.zip`
  with the same SHA-256 above.

- User-requested artifact location change: published Windows ZIPs now go to the
  visible, Git-ignored `releases/` folder at the repository root. Both checkpoint
  ZIPs above were moved there unchanged (same hashes); `.build/` is only for
  intermediate files. The dated checkpoint is now
  `releases/DeusKVM-Companion-win-x64-auto-mac-2026-09-21.zip`.

- User checkpoint update: the user reports the new build seems to work with the
  main Mac. Initial single-Mac use is provisionally successful; specific edge/
  clipboard checks and two-Mac priority, takeover and recovery remain pending.
  Next: connect the second Mac while the main Mac remains connected, verify it
  waits, then disconnect the main Mac and check automatic takeover.

## Explicit takeover implementation and validation — 2026-09-21

- Windows now keeps verified companion channels for all connected Macs, but
  grants one desktop/clipboard session. A later eligible Mac is auto-disabled.
  Explicit Enable requests take over only after old-owner release ACK (or a real
  disconnect). Same-connection native retries cannot bypass this barrier.
- Mac Disable retains the companion metadata channel, restores local control,
  stops capture and clipboard, and preserves final zero input reports under
  notification backpressure. The disable state persists across app restarts.
- Availability `0x18` carries a little-endian epoch and state (0 disabled,
  1 available, 2 available + explicit request). Selection `0x19` echoes the epoch
  plus grant/revoke. RequestControl `0x1a` and ReleaseAck `0x1b` carry a four-byte
  epoch. HELLO negotiates `selection:1`, `takeover:true`, availability and optional
  pending request. Atomic state 2 prevents a new Enable being mistaken for an
  unsolicited arrival before its separate request frame is delivered.
- Local readiness now checks Accessibility, Input Monitoring, event-tap setup,
  current display, Secure Input, HID target, and current companion ownership.
  This addresses misleading readiness; the laptop's specific cause is unverified.
- Windows Release solution build: **0 warnings, 0 errors**. Core suite:
  **105 passed, 0 failed**. Includes release-ACK gating, wrong/stale ACKs,
  competing and cancelled requests, physical disconnect, rapid old-owner Enable,
  availability/epoch, and shared protocol fixture checks.
- Mac Debug app/test build: **77 passed, 0 failed**. Signed universal Release:
  **arm64 + x86_64**, signature verified on the app extracted from the delivered
  ZIP. SwiftLint strict, SwiftFormat on changed Swift sources, and diff whitespace
  check passed. Verification requiring certificate trust/test-runner sockets ran
  outside the filesystem sandbox; no user app was replaced or launched.
- Both archives passed ZIP CRC checks. Windows archive contains the x64 EXE,
  README and current CHECKPOINT; no .NET installation is needed on Windows.
- Native Windows execution, Bluetooth handoff, backpressure, two-Mac input and
  clipboard isolation are still **pending user testing**. Release ACK establishes
  local capture stop and CoreBluetooth acceptance, not measured Windows input
  delivery timing. Build success is not hardware confirmation.
- No staging, commits, or pushes. Pausing for user setup after this delivery.

### Delivered artifacts (corrected to Apple Silicon only)

User correction: macOS targets arm64 only. Rebuilt and signed the arm64 app;
the publisher now accepts only arm64 and defaults to it. The extracted ZIP
was verified to contain only arm64, and its strict code-signature check passed. Universal archives
were moved out of `releases/` to `.build/superseded-releases/`. The Windows EXE
is unchanged; its bundled checkpoint now names the arm64 Mac ZIP.

- `releases/DeusKVM-Companion-win-x64-takeover-2026-09-21.zip` — 52,394,111 bytes.
  SHA-256: `1d4dc2e698f3ad0bbc95b5dba4ae617472d4395deaed2ac99da7d42520aea23a`.
- `releases/DeusKVM-mac-arm64-takeover-2026-09-21.zip` — 942,263 bytes.
  SHA-256: `3c787925fe0abc683cb1c6a67a6cec0115c7c4a2ba7df3aa8ba57c6a2b4a2b7d`.

## MacBook Pro display blocker — 2026-09-22

- [x] User screenshot identifies the current blocker: “Choose an available Mac
  display in Layout,” with a blank Mac display picker. This is a local display
  prerequisite; it does not establish that the rest of takeover has passed.
- [x] Source diagnosis: display refresh only replaced an empty saved UUID. A
  nonempty UUID absent from the current screen list kept geometry unavailable,
  preventing both edge entry and Switch to PC.
- [x] Fix: preserve a valid display selection; otherwise select the first current
  screen. If no screens are temporarily available, retain the saved selection
  until a display update arrives. Existing preference saving handles the fallback.
- [x] Mac tests: 77 passed. Strict SwiftLint, SwiftFormat and diff checks passed.
  Signed Release built successfully; delivered ZIP CRC, extracted-app signature,
  and arm64-only architecture verified. No Windows code or protocol changes.
- [x] Prepared `releases/DeusKVM-mac-arm64-display-fix-2026-09-22.zip`
  (942,454 bytes), SHA-256:
  `82c823c6fb0810c3d5c61044670e992bc99325d39b329b96ca9d854c102670d5`.
- [ ] User check: selecting the MacBook display manually in the existing build,
  or installing this Mac update, clears the display blocker. Then verify laptop
  Switch to PC, edges, and takeover both ways. Awaiting user confirmation.

## Git workflow — 2026-09-22

The user requested committing completed code. The implementation, display fix,
tests, build scripts and tracked plan are being committed together. AGENTS.md
now records that workflow. Hardware checkpoint results remain pending.


## Forwarded mouse lag investigation — 2026-09-22

- [x] Committed the implementation/display checkpoint as `0abff1d` after the user
  authorized ongoing commits. Its hardware checks remain pending.
- [x] User reports intermittent Windows pointer overload/lag, possibly when both
  Macs are connected, and confirms input comes through DeusKVM from the Mac.
- [x] Inspected active/inactive session isolation: only active BluetoothControl
  runs a desktop worker; inactive channels retain heartbeats and unavailable
  clipboard advertisements. No second desktop worker was found in the intended path.
- [x] Read this Mac's last ten minutes of Capture-category logs: 108 samples,
  maximum capture-to-main-thread dispatch 21 ms (13 samples above 10 ms), zero
  blocked companion-queue samples. This does not measure the HID notification
  queue, radio scheduling, or Windows delivery, nor prove correlation to stutter.
- [x] Found an unbounded recovery path in DesktopWorker.RawInput: while a handoff
  is active but HID/desktop availability is missing, each mouse packet called
  Refresh, performing device/desktop queries and enqueueing another status update.
- [x] Bound input-triggered recovery probes to four per second; first probe stays
  immediate, normal available input is unaffected, and the periodic timer remains.
  This is a source-confirmed amplification risk, not a confirmed hardware root cause.
- [x] Validate tests/build, publish Windows-only test ZIP and prepare this fix for commit.
- [ ] User setup/check: both Macs connected, alternate active Mac, compare any
  persistent lag with idle Mac temporarily disconnected. Awaiting observations.

Validation: Windows Release build passed with zero warnings/errors; **108 core
tests passed**, including 5,000 input events over five seconds producing only
20 recovery probes. ZIP CRC, x64 executable and packaged instructions verified.
Artifact: `releases/DeusKVM-Companion-win-x64-input-lag-2026-09-22.zip` (52,394,855 bytes).
SHA-256: `7b23a87bee25b10c566abf3054de117e556977387fdaa9528324fdc3a474e409`.
User hardware validation remains pending; no Mac rebuild was needed.

## Next session and pointer-feel research — 2026-09-22

The user deferred the input-lag checkpoint until tomorrow. They also report that
forwarded mouse acceleration feels different from macOS and require preserving
Windows mouse settings for directly attached mice. Research and a staged tuning
proposal are in [POINTER_FEEL_RESEARCH.md](POINTER_FEEL_RESEARCH.md). No acceleration
or input-path implementation was added during this research; hardware tests remain
pending. Any tuning must apply only to DeusKVM-forwarded input.
