> For the automatic-selection build, start with [the two-Mac checkpoint](AUTOMATIC_MAC_CHECKPOINT.md).
> The checks below cover earlier features; this Windows-only update needs no Mac rebuild.

# Setup, startup, centering and disable controls

This build implements the four requested polish items and the added Mac disable
control. The running apps were left untouched during development. No Bluetooth
pairing, service installation/removal, login registration, cursor manipulation or
computer-use interaction test was performed on the user's machines.

## Update tonight

1. On Windows, extract `releases/DeusKVM-Companion-win-x64.zip` and open
   `DeusKVM.Companion.exe`. Approve its normal update prompt. It closes the old
   companion and preserves pairings and service running/startup state.
   There is no separate script to run and no need to stop the service first.
2. On the Mac, quit DeusKVM and reopen
   `.build/DerivedData/Build/Products/Debug/DeusKVM.app` from this checkout.
3. Keep the existing pairing for the initial checks. Complete removal is the
   last test below, because it removes the app and its settings.

## Suggested manual checks

### Centering

Use the configured hotkey and the **Switch to PC** button/menu item. Both should
place the pointer in the center of the display selected in Mac Layout. Try a
non-primary Windows display if available. Ordinary edge crossings should still
land proportionally along the entering edge. Return by edge and hotkey and
check that typing and clipboard sharing still work.

Both builds are needed for centering. An older Windows companion keeps its
previous placement behavior. Reattaching after login/reconnect uses the existing
RESUME behavior and does not unexpectedly warp the pointer.

### Mac enable/disable

Click **Disable DeusKVM** in Mac Settings or its menu. The menu icon should
change to a pause symbol, advertising should stop, and edge/hotkey entry should
stop. Clipboard changes should stay local. If disabled while remote, local
Mac control is restored first.

The same control becomes **Enable DeusKVM**. Re-enable, allow the Windows
service a few seconds to reconnect, and test switching/clipboard again without
re-pairing. Repeat disable/re-enable quickly once. Quit/reopen while disabled:
it should remain disabled, retaining the selected/allowed device and layout.

The existing OS pairing is preserved. macOS/Windows may still show the ordinary
Bluetooth bond/connection. Input and clipboard traffic stop; the lightweight
companion channel remains alive for ownership requests. Enable requests control
and automatically disables another active Mac. See the linked two-Mac checkpoint.

The Mac menu bar icon now follows connection progress: an antenna while searching,
two circular arrows while connecting, and the existing keyboard once an allowed
PC has HID input subscriptions, a current companion grant, and ready Mac input capture. The ready
keyboard still fills when controlling Windows. Disabled uses the pause icon;
unavailable Bluetooth uses a crossed-out antenna. The menu and tooltip name the
state. A stale companion heartbeat returns the icon to connecting, even if the
OS still reports Bluetooth connected. Check these states during your normal
connect/disconnect and enable/disable tests; no Windows update is needed for this
icon change.

### Startup

Windows has two independent controls in Settings and the tray menu:

- **Start service with Windows:** the existing boot-started service, available
  independently of whether a user is signed in.
- **Show tray icon at sign-in:** the optional configuration UI. This is enabled
  once on upgrade to this build, applies to users signing in on this PC, and is
  preserved on subsequent updates. It opens only the icon, not a settings window,
  and does not start a deliberately stopped service.

Sign out/in or reboot to test the tray. Disable tray startup, repeat, and verify
that the service still works without the icon. Reopen the EXE to get Settings.
Windows startup policies/Startup Apps controls can override automatic launch;
Windows also decides when during login to run registered startup apps.

On the Mac, **Launch DeusKVM at login** is opt-in. The toggle reads the real
SMAppService registration; if macOS requires approval, use **Allow in Login
Items…**. Enable and test a later login, then disable if unwanted. Disabled
DeusKVM remains disabled even when it starts at login.

### Pair inside the companion

Open **Add Mac…** and select the already paired Mac first. It should verify
DeusKVM and save without removing/recreating that bond. Cancel another attempt
and check that the active connection still works. Adding a Mac does not preempt it.

For fresh pairing, leave **System Settings → Bluetooth** open on the Mac, then
use **Add Mac…**, choose the Mac and approve Windows'
pairing prompt and any Mac prompt. Enable DeusKVM on the Mac; it must advertise
when no allowed PC is ready. Other nearby Bluetooth devices may appear in the
picker, so choose your Mac. The app verifies the HID and DeusKVM services before
saving. On the Mac, turn on **Enable control** for the PC if it is not already allowed.
Windows Bluetooth Settings should not be required for this normal flow.
The picker shows computers and the previously verified Mac by default. **Show
all devices** reveals other categories and devices whose category is unknown,
without restarting the search. Device names are not used to identify computers.
Try the checkbox both ways; pairing and service verification remain unchanged.

Pairing/verification failure leaves existing Mac pairings intact. A bond
created by that failed attempt is rolled back; an existing bond is not removed.
Rollback failure is reported explicitly. If cancellation occurs during a Windows
pairing prompt, finish/dismiss that prompt so the attempt can settle safely.
Amadeus confirmed the corrected in-app pairing flow works.

Discovery correction (September 14): Amadeus confirmed the Mac was absent in
both lists until Mac Bluetooth settings were opened, then visible only in
Windows Settings. The original picker only queried LE endpoints. It now scans
regular Bluetooth as well, pairs the chosen endpoint and resolves that Mac's
public Bluetooth address to a verified LE endpoint for the service. It does not
match devices by name. Pairing and subsequent cleanup were later confirmed by
Amadeus; retain these checks for updates.

### Complete removal — test last

Choose **Remove DeusKVM from this PC…** in Settings or the tray and confirm.
It preserves Windows Bluetooth pairings, stops/closes the workers and
tray, unregisters the service and its event source, removes the tray startup
entry and shortcut, and deletes settings, logs, installed app files and standard
DeusKVM .NET extraction caches. Other pairings are not removed.

Wait for the final **DeusKVM removed** message. A built-in Windows process
finishes deleting the EXE after it exits; no helper script is left on disk.
The downloaded EXE/ZIP remain yours to delete. Windows execution/security history
is not erased, and the Mac's app settings/allow list are not modified remotely.
Custom runtime extraction locations selected outside the app are not discovered.

If any stage fails, the app reports that removal is incomplete. Reopen the
**downloaded** EXE to retry; do not interpret the tray closing alone as success.
If retrying after updating to a fixed build, open the newly extracted EXE:
the retry runs that EXE's cleanup helper without reinstalling the old service.
After successful removal, reopening the downloaded EXE should perform a fresh
installation and show **Add Mac…**, with no saved preference. Existing paired
Macs reconnect automatically; add new Macs inside the app and verify that
switching and clipboard sharing work again.

## Automated coverage

- Swift: explicit/edge entry packets, old-companion compatibility, shared wire
  fixture, disabled advertising/readiness, retained allow list, and serialized
  input-tap stop/start with rejection of stale readiness callbacks.
- .NET core: physical monitor centering; discovery deduplication by address;
  verify-before-save; existing-bond preservation; rollback errors; removal stage
  ordering and stopping on failures; all existing input/clipboard tests.
- The real embedded file-cleanup script is tested on disposable directories,
  including repeat removal, refusal of linked roots, and preservation of targets
  linked from inside the owned tree. No installed app files are touched by these
  tests.
- Windows CI is extended for tray startup registration/update preservation,
  quiet login entry, reopening the same tray, and complete removal without a
  saved device. These native Windows checks were prepared but not run locally.

Amadeus subsequently confirmed in-app pairing, login startup, cleanup after its
fix, hotkey/button cursor centering, disabled/connection icon states, and the
Mac header scrolling appearance. Keep the checks above for regression testing.
The full DeusKVM rename uses fresh app/service identities and still needs a
native fresh-install/appearance check. Remove the previous Windows installation
with its own cleanup command first, and regrant permissions on the Mac.

Implementation references:
[Windows pairing](https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/pair-devices),
[Windows Run entries](https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys),
[.NET extraction](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview),
[Mac login registration](https://developer.apple.com/documentation/servicemanagement/smappservice/register()).
