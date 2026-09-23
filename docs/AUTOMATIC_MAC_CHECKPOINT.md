# Enable to take over — two-Mac checkpoint

Update **Windows and both Macs**. Keep Bluetooth enabled and retain both pairings.
The earlier Windows-only build does not implement this flow.

## Update

1. On Windows, extract `DeusKVM-Companion-win-x64-direct-pointer-2026-09-22.zip`
   and open `DeusKVM.Companion.exe`. Approve the update prompt; it closes the old
   companion automatically. No scripts or .NET installation are needed.
2. On **each Mac**, quit DeusKVM, unzip
   `DeusKVM-mac-arm64-direct-pointer-2026-09-22.zip`, and replace the app in its usual
   location with the new `DeusKVM.app`. Open that copy. This build is for Apple Silicon only. Keep each Mac's existing permissions and layout.
3. If a Mac cannot switch, read **Layout → Windows**. The status now distinguishes
   waiting for Windows control from missing permissions or unavailable capture.
   Follow that specific status; do not remove Bluetooth pairings.

## Direct-pointer experiment (latest checkpoint)

After installing both new builds, on the active Mac open **Settings** and enable
**Direct Windows pointer (experimental)**. Reset **Windows pointer speed** to
**1.00×** initially. Switch to Windows. Layout → Windows should report
**Direct Windows pointer active**. An older companion or unavailable desktop
instead reports HID fallback. Return locally before changing the mode.

Compare slow precision movements and quick sweeps with the toggle off/on. Then
check clicking, dragging, both scroll axes, edge/hotkey return and typing. A slow
Bluetooth link may limit update frequency; report any extra latency separately
from acceleration feel. No exact macOS curve match is claimed yet.

Turn the option off to use the original HID input path. Direct mode targets the
normal desktop; elevated apps and sign-in may reject software injection. If a
direct session fails it returns locally, with an error, instead of switching
mouse transports mid-drag. An unavailable companion/desktop falls back on entry.

Once the initial feel check passes, repeat on the second Mac and test takeover,
including during a drag. Verify buttons release and the old Mac returns locally.
Check a directly attached Windows mouse, mixed-DPI/negative-origin displays,
Bluetooth loss, service restart and lock/unlock. Windows global mouse settings
must remain unchanged. These hardware checks are still pending.

## Ownership regression check

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

1. The latest direct-pointer ZIPs include the prior input-lag fix. Update both
   platforms for direct-pointer mode; test with it off first to compare HID.
2. With both Macs connected, confirm Windows shows one active and the other
   disabled. Move the active Mac's pointer around Windows, away from return edges.
3. Enable the other Mac to take over and repeat. Note which Mac stutters and the
   Windows companion's exact status at that moment.
4. If stutter persists, compare the same active Mac with Bluetooth temporarily
   off on the idle Mac, then restore it. Keep pairings. Report whether the change
   reliably removes the stutter; this is a diagnostic comparison, not normal use.

Pause after setup and this short check. Do not mark lag fixed without the user's
result. Current Mac capture logs do not measure Windows-side HID delivery timing.

## Optional pointer speed check

The latest Mac build adds **Settings → Windows pointer speed**, 0.25×–2× with
Reset to 1×. Each Mac saves its own setting. Compare fine movement and dragging
at 0.5×, 1× and 1.5×, then check app restart and ownership changes. Windows global
mouse settings, directly attached mice, scrolling and local Mac movement should
remain unchanged. The slider controls speed, not the exact acceleration curve.
