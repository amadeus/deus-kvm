# Small Windows build — Framework 4.8 checkpoint

This is the complete Windows companion, using the runtime already included with
Windows 10 2004+ and Windows 11. Keep your current Mac app and Bluetooth pairings.
It includes the existing ownership, edge switching, text clipboard and two-way
network file paste (up to 2,000,000,000 bytes). File contents still transfer only
when pasted; there is no pre-download.

## Install

1. Download and open the **single DeusKVM Companion EXE**. No manual extraction
   or other files alongside it are needed.
2. Approve the normal administrator update prompt if an update is needed. The
   launcher stages its embedded package privately; the existing updater preserves
   settings and the previous service running/stopped and startup preferences.
3. The launcher cleans up after handing off. Reopen the same downloaded EXE or use
   the normal DeusKVM Start menu shortcut. Keep your current Mac app and pairings.

The earlier Framework ZIP remains a fallback in `releases/Previous/`; if using
that ZIP, extract all files together and launch its `DeusKVM.Companion.exe`.

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

The user confirmed the preceding small Framework build works. The new launcher
has local extraction/build checks; its Windows UAC, process handoff and temporary
cleanup remain pending. Detailed lifecycle and full hardware cases above remain
tracked separately from the user's general confirmation.

## Fallback

The confirmed small Framework ZIP and previous large Windows ZIP are retained in
`releases/Previous/` in the project. Prefer the confirmed small ZIP as fallback.
Extract that ZIP to a separate folder and launch its EXE to reinstall the previous
working build. Do not uninstall first; that would remove saved app settings.
The old updater replaces the main EXE; leftover small-build DLLs do not get loaded
by its self-contained app. A later small-build update verifies/replaces all its
own payload files again. Hardware validation of downgrade also remains pending.
