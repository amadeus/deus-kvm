# Edge-out / hotkey-back checkpoint

> Status 2026-11-11: historical M2 checkpoint (September 13). Later milestones
> added the Windows companion (pairing with **Add Mac…**, Windows edge return,
> clipboard) and removed the edge delay. Current setup is in the
> [README](../README.md). The checks below remain useful for regressions.

## Run

Run `./build.sh`, then:

```sh
open .build/DerivedData/Build/Products/Debug/DeusKVM.app
```

The app lives in the menu bar after you close its window. Choose **Open controls…**
to reopen it. (Current builds have two tabs, Setup and Configuration, and open
the window at launch only while setup is incomplete.)

## Set up

1. Allow Bluetooth. In System Settings → Privacy & Security → Accessibility,
   add this worktree's built `DeusKVM.app` if necessary and enable it. An older
   DeusKVM permission entry may belong to a different build/signature.
2. Pair the Mac from Windows Bluetooth Settings as with the original app.
   Horizontal scrolling now adds a field to the HID mouse descriptor. An existing
   Windows pairing may need to be removed and paired once more to refresh its
   cached descriptor.
3. In **Setup**, keep only the PC active. **Layout** waits for exactly one active
   central subscribed to HID; selecting multiple active hosts leaves switching
   unarmed. (Current builds: turn on **Enable control** for the PC; with several
   ready, **Use device** picks one.)
4. In **Layout** (now **Configuration**), choose the Mac display and an exposed
   edge, then enable edge switching. The default delay was 250 ms (removed
   2026-09-13: crossing is immediate). An edge segment adjoining another Mac
   display is excluded so ordinary Mac display navigation still works.
5. Confirm the toggle shortcut. Default: **Fn + Escape**. If your keyboard handles
   Fn internally, use **Record shortcut** to choose a combination macOS receives.
   Keep the hotkey enabled for this checkpoint. No Windows companion is needed.

## Try

- Move to the configured edge: the Mac cursor parks and hides,
  and input controls the PC from its existing cursor position.
- Press and release the toggle shortcut: control returns to the Mac. Repeat
  from another foreground app and with the controls window closed.
- In a Windows app with content wider than its viewport, try horizontal trackpad
  scrolling in both directions, then vertical and diagonal scrolling. Horizontal
  scrolling uses the standard HID AC Pan field; confirm the content follows the
  same gesture direction as on the Mac. Live Windows validation is pending.
- Hold a letter (including autorepeat), Shift/Ctrl, or a mouse button while
  requesting a handoff. Releases should reach the current machine before it
  switches. A pending Mac edge request cancels if you move away from the edge.
- Hold the toggle shortcut: it must switch only once, after release.
- Check that moving remotely does not cause Dock, menu-bar, or application hover
  changes on the Mac, including on a full-screen Space.
- Try the hotkey while Windows is locked or showing UAC. Return does not need
  the PC to respond. (PC edge return was added by the Windows companion.)
- Turn off the PC's Bluetooth while remote: Mac input should return when the
  subscription disappears. Quitting the app also restores the Mac cursor.
- With Secure Input active on the Mac, switching should stay local or release
  with a warning in Layout (now Configuration).

Report any stuck key, cursor visibility/hover issue, unexpected switch, or change
in how remote movement/typing feels. Core HID changes are deferred until a
reproduced issue calls for them.

## Developer verification

```sh
DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer \
  xcodebuild -project DeusKVM.xcodeproj -scheme DeusKVM \
  -configuration Debug -destination 'platform=macOS' \
  -derivedDataPath .build/DerivedData build test
```

The standalone tests cover exposed-edge geometry, negative coordinates, corner
exclusion, exact hotkey matching, repeat suppression, and release-before-switch.
They also cover horizontal and diagonal scroll capture, signed report encoding,
the mouse descriptor's field layout, and separate three-byte boot-mouse reports.
The report-mode mouse payload is now five bytes: buttons, X, Y, wheel, pan.

A bounded `--cursor-spike` diagnostic runs inside the same signed app, without
starting Bluetooth or forwarding input, and writes `/tmp/bt-cursor-spike.log`.
It checks the actual tap/cursor path and restores local control after three
seconds of capture (eight-second overall timeout). Quit the normal instance
before launching this diagnostic. It is only a test mode, not a helper process.
