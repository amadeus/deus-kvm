# Enable to take over — two-Mac checkpoint

Update **Windows and both Macs**. Keep Bluetooth enabled and retain both pairings.
The earlier Windows-only build does not implement this flow.

## Windows performance trial removed — latest checkpoint

Update **Windows only** if you already installed the Mac low-latency build. Keep
that Mac app; no toggle, reset or Bluetooth re-pairing is needed.

The Windows-side performance request has been removed after the user reported no
noticeable smoothness improvement and temporary trouble returning from/re-entering
Windows. The cause of that incident is unconfirmed; the trial is withdrawn.
The replacement restores the pre-trial Windows connection and handoff behavior.

The current Mac build continues to request low Bluetooth latency for its enabled
HID target, which the user reported felt slightly better. Mouse reports,
acceleration, queue handling and the earlier ownership/lag fixes are unchanged.
After installation, confirm normal switching in both directions. Further hardware
validation remains pending; the rollback is not proof the transient issue is fixed.

## Pointer experiments removed

At the user's request, the pointer-speed slider and experimental direct-pointer
mode have been removed. This build restores the original Bluetooth HID mouse
reports. Saved experimental preferences are ignored; no reset or re-pairing is
needed. Update Windows and both Macs using the current ZIPs below. Automatic
ownership/takeover, the display-selection fix and bounded recovery polling remain.

## Update

1. On Windows, extract `DeusKVM-Companion-win-x64-standard-2026-09-22.zip`
   and open `DeusKVM.Companion.exe`. Approve the update prompt; it closes the old
   companion automatically. No scripts or .NET installation are needed.
2. On **each Mac**, quit DeusKVM, unzip
   `DeusKVM-mac-arm64-low-latency-2026-09-22.zip`, and replace the app in its usual
   location with the new `DeusKVM.app`. Open that copy. This build is for Apple Silicon only. Keep each Mac's existing permissions and layout.
3. If a Mac cannot switch, read **Layout → Windows**. The status now distinguishes
   waiting for Windows control from missing permissions or unavailable capture.
   Follow that specific status; do not remove Bluetooth pairings.

## Test this first

1. Open/enable DeusKVM on the main Mac first. Confirm **Switch to PC**, input,
   and edge/hotkey return work.
2. Open DeusKVM on the laptop while the main Mac stays connected. The laptop
   should automatically become **Disabled**; Windows keeps the main Mac active.
   If the laptop was already saved as disabled, it should stay disabled.
3. Click **Enable DeusKVM** on the laptop. Windows should disable the main Mac,
   wait for its input release, then make the laptop active. No Bluetooth toggle.
4. On the laptop, test **Switch to PC**, typing, pointer movement, and edge return.
5. Click **Enable DeusKVM** on the main Mac. Control should move back and the
   laptop should become disabled. Each Mac should use its own edge/display.

If a step fails, stop and report that step plus the exact status on the laptop
and Windows. The laptop's original clickable-but-inert button failure has not
been reproduced on hardware; the new readiness checks should expose a blocker.

## After that passes

- Disable the active Mac. With the other Mac already disabled, Windows should
  remain idle until you explicitly enable one of them.
- Copy fresh disposable text in both directions from each active Mac. Disabled
  Macs must not receive clipboard updates or send input.
- Enable alternate Macs quickly several times. Only one may own capture;
  outdated grants must not revive a disabled Mac. The last Enable request wins.
- During takeover while remote, verify the former Mac regains its local cursor
  and no keys/buttons remain held on Windows.
- Quit/reopen either app, sleep/wake, restart the Windows service, and test
  lock/unlock and reboot/login. Saved disabled state persists across Mac restarts.
  Previously verified paired Macs receive reconnect maintenance. Startup prefers
  the last active eligible Mac, then stable endpoint order; a second Mac is disabled.
- If the previous owner stops responding, Windows must wait for release or an
  actual Bluetooth disconnect. A heartbeat timeout alone must not let two Macs
  capture at once. The Mac returns local on companion heartbeat loss.

## Diagnostics

Windows: **Open diagnostics** opens `C:\ProgramData\DeusKVM` with `status.json`
and `service.log`. Mac: report the Layout/Windows status and Setup permissions.
No uninstall or re-pairing test is needed.

Builds and automated tests do not establish hardware success. This checkpoint
is pending until both updated Macs and Windows are tested together.

## Forwarded mouse stutter follow-up — September 22

The latest Windows build bounds recovery refreshes to four per second while
waiting for the active Mac's HID mouse or desktop access. Previously every raw
mouse event could enumerate devices and enqueue another Bluetooth status packet.
The normal available input path is unchanged. This fixes an overload path found
in source; it does not establish the cause of the reported two-Mac stutter.

1. The HID-restore ZIPs include the prior input-lag fix. Update Windows and both
   Macs to remove the pointer experiments before repeating the comparison.
2. With both Macs connected, confirm Windows shows one active and the other
   disabled. Move the active Mac's pointer around Windows, away from return edges.
3. Enable the other Mac to take over and repeat. Note which Mac stutters and the
   Windows companion's exact status at that moment.
4. If stutter persists, compare the same active Mac with Bluetooth temporarily
   off on the idle Mac, then restore it. Keep pairings. Report whether the change
   reliably removes the stutter; this is a diagnostic comparison, not normal use.

Pause after setup and this short check. Do not mark lag fixed without the user's
result. Current Mac capture logs do not measure Windows-side HID delivery timing.
