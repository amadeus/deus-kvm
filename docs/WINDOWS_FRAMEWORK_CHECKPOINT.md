# Small Windows build — Framework 4.8 checkpoint

This is the complete Windows companion, using the runtime already included with
Windows 10 2004+ and Windows 11. Keep your current Mac app and Bluetooth pairings.
It includes the existing ownership, edge switching, text clipboard and two-way
network file paste (up to 2,000,000,000 bytes). File contents still transfer only
when pasted; there is no pre-download.

## Install

1. Extract **the entire ZIP into a new folder**. Keep the EXE, DLLs, config and
   `package.json` together. Do not open the EXE from inside the ZIP or copy just
   the EXE into the old folder.
2. You can quit the old tray, then open **DeusKVM.Companion.exe** from the new
   folder and approve the normal administrator update prompt. The updater stops
   the service/workers, installs the whole payload, and preserves settings and
   the previous service running/stopped and startup preferences.
3. After installation, use the normal DeusKVM Start menu shortcut. No Mac update,
   re-pairing or additional runtime download is intended for supported Windows.

## Test, in order

- Check that the tray opens, the existing Mac becomes active, and service status
  becomes ready. If the service was stopped before the update, click Start Service.
- Cross to Windows; move, scroll and type. Return using both the edge and hotkey.
- Enable the second Mac to take ownership, then switch back. Leave both paired.
- Copy/paste plain text both ways. Paste a small file both ways, then a larger
  file; verify contents and cancel another transfer. The native Windows encryption
  adapter is new and particularly needs this check.
- Quit/reopen the tray, then restart Windows and verify service recovery, login
  control and edge return after sign-in. Check display scaling if using mixed DPI.
- Compare idle CPU and pointer responsiveness with the old build. Size reduction
  alone does not imply a speed improvement.

If startup or file paste fails, keep the error text and `C:\ProgramData\DeusKVM\service.log`
/ `status.json`; file diagnostics are at `%LOCALAPPDATA%\DeusKVM\file-paste.log`.
Use the Mac's toggle hotkey to return locally if needed.

The complete app and both test targets compile on macOS. Local .NET 10 regression
checks run here; Framework execution, Windows CNG, real service lifecycle,
Bluetooth, tray and OLE clipboard validation remain pending Windows testing.
These are not reported as passed by a successful cross-build.

## Fallback

The previous large Windows ZIP is retained in `releases/Previous/` in the project.
Extract that ZIP to a separate folder and launch its EXE to reinstall the previous
working build. Do not uninstall first; that would remove saved app settings.
The old updater replaces the main EXE; leftover small-build DLLs do not get loaded
by its self-contained app. A later small-build update verifies/replaces all its
own payload files again. Hardware validation of downgrade also remains pending.
